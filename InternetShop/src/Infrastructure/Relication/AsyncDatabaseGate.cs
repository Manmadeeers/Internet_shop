namespace InternetShop.Api.Infrastructure.Replication;

public sealed class AsyncDatabaseGate
{
    private readonly SemaphoreSlim turnstile = new(1, 1);
    private readonly SemaphoreSlim readerMutex = new(1, 1);
    private readonly SemaphoreSlim roomEmpty = new(1, 1);

    private int readers;

    public int ReaderCount => Volatile.Read(ref readers);

    public async ValueTask<IAsyncDisposable> ReadAsync(CancellationToken ct)
    {
        await turnstile.WaitAsync(ct);

        try
        {
            await readerMutex.WaitAsync(ct);

            try
            {
                if (readers == 0)
                {
                    await roomEmpty.WaitAsync(ct);
                }

                readers++;
            }
            finally
            {
                readerMutex.Release();
            }
        }
        finally
        {
            turnstile.Release();
        }

        return new Lease(ReleaseReaderAsync);
    }

    public async ValueTask<IAsyncDisposable> WriteAsync(CancellationToken ct)
    {
        await turnstile.WaitAsync(ct);

        try
        {
            await roomEmpty.WaitAsync(ct);
        }
        catch
        {
            turnstile.Release();
            throw;
        }

        return new Lease(() =>
        {
            roomEmpty.Release();
            turnstile.Release();
            return ValueTask.CompletedTask;
        });
    }

    private async ValueTask ReleaseReaderAsync()
    {
        await readerMutex.WaitAsync();

        try
        {
            readers--;

            if (readers == 0)
            {
                roomEmpty.Release();
            }
        }
        finally
        {
            readerMutex.Release();
        }
    }

    private sealed class Lease(Func<ValueTask> release) : IAsyncDisposable
    {
        private Func<ValueTask>? callback = release;

        public ValueTask DisposeAsync()
        {
            var action = Interlocked.Exchange(ref callback, null);
            return action is null ? ValueTask.CompletedTask : action();
        }
    }
}