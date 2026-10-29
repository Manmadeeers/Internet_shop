using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InternetShop.Api.Infrastructure.Replication;

public sealed class DemoReplicationOptions
{
    public bool Enabled { get; set; }
    public int PollSeconds { get; set; } = 2;
    public int ProbeTimeoutSeconds { get; set; } = 2;
    public int SyncTimeoutSeconds { get; set; } = 30;
    public string StateFile { get; set; } = ".replication/state.json";
}

public sealed class DatabaseSelection
{
    public string? Node { get; set; }
    public string? ConnectionString { get; set; }
}

public sealed class DemoReplicationCoordinator : BackgroundService
{
    private readonly DemoReplicationOptions settings;
    private readonly IConfiguration configuration;
    private readonly DatabaseSnapshotCopier copier;
    private readonly ILogger<DemoReplicationCoordinator> logger;

    private readonly AsyncDatabaseGate gate = new();
    private readonly SemaphoreSlim stateMutex = new(1, 1);
    private readonly string statePath;
    private readonly FileStream? instanceLock;

    private volatile ReplicationState state =
        new("primary", false, null);

    private volatile bool ready;
    private volatile bool primaryOnline;
    private volatile bool replicaOnline;
    private volatile string? error;

    public bool Enabled => settings.Enabled;

    public DemoReplicationCoordinator(
        IOptions<DemoReplicationOptions> options,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        DatabaseSnapshotCopier copier,
        ILogger<DemoReplicationCoordinator> logger)
    {
        settings = options.Value;
        this.configuration = configuration;
        this.copier = copier;
        this.logger = logger;

        statePath = Path.GetFullPath(
            Path.Combine(environment.ContentRootPath, settings.StateFile));

        if (!Enabled)
        {
            return;
        }

        _ = ConnectionString("primary");
        _ = ConnectionString("replica");

        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);

        instanceLock = new FileStream(
            statePath + ".lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        if (File.Exists(statePath))
        {
            state = JsonSerializer.Deserialize<ReplicationState>(
                File.ReadAllText(statePath))
                ?? throw new InvalidOperationException(
                    "Replication state file is empty.");

            if (state.Active is not ("primary" or "replica"))
            {
                throw new InvalidOperationException(
                    "Replication state contains an invalid active database.");
            }
        }
    }

    public object GetStatus()
    {
        var current = state;

        var activeOnline = current.Active == "primary"
            ? primaryOnline
            : replicaOnline;

        return new
        {
            enabled = Enabled,
            activeDatabase = current.Active,
            ready = !Enabled || ready && activeOnline,
            synchronized = current.Synchronized,
            primaryOnline,
            replicaOnline,
            lastSyncUtc = current.LastSyncUtc,
            error
        };
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        if (Enabled)
        {
            await ReconcileAsync(ct);
        }

        await base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            return;
        }

        using var timer =
            new PeriodicTimer(TimeSpan.FromSeconds(settings.PollSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ReconcileAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task<IAsyncDisposable?> EnterRequestAsync(
        DatabaseSelection selection,
        bool mayWrite,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var lease = await gate.ReadAsync(ct);

            try
            {
                var current = state;

                if (ready && await ProbeAsync(current.Active, ct))
                {
                    if (mayWrite)
                    {
                        await SaveStateAsync(
                            value => value with { Synchronized = false },
                            ct);
                    }

                    selection.Node = current.Active;
                    selection.ConnectionString =
                        ConnectionString(current.Active);

                    return lease;
                }
            }
            catch
            {
                await lease.DisposeAsync();
                throw;
            }

            await lease.DisposeAsync();
            await ReconcileAsync(ct);
        }

        return null;
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            return;
        }

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(ct);

        timeout.CancelAfter(
            TimeSpan.FromSeconds(settings.SyncTimeoutSeconds));

        try
        {
            if (gate.ReaderCount > 0)
            {
                await RefreshHealthAsync(timeout.Token);
                return;
            }

            await using var maintenance =
                await gate.WriteAsync(timeout.Token);

            await RefreshHealthAsync(timeout.Token);

            var current = state;
            var other = current.Active == "primary"
                ? "replica"
                : "primary";

            var activeOnline = IsOnline(current.Active);
            var otherOnline = IsOnline(other);

            if (!activeOnline)
            {
                ready = false;

                if (!otherOnline)
                {
                    error = "Both databases are unavailable.";
                    return;
                }

                if (!current.Synchronized)
                {
                    error =
                        "The active database is unavailable and the standby " +
                        "is not confirmed synchronized. Automatic failover is blocked.";

                    return;
                }

                await SaveStateAsync(
                    value => value with { Active = other },
                    timeout.Token);

                logger.LogWarning(
                    "FAILOVER: {Previous} -> {Current}",
                    current.Active,
                    other);

                current = state;
                other = current.Active == "primary"
                    ? "replica"
                    : "primary";
            }

            ready = true;

            if (!IsOnline(other))
            {
                error = $"{other} is unavailable; using {state.Active}.";
                return;
            }

            if (!state.Synchronized)
            {
                var source = state.Active;

                logger.LogInformation(
                    "SYNC START: {Source} -> {Destination}",
                    source,
                    other);

                await copier.CopyAsync(
                    ConnectionString(source),
                    ConnectionString(other),
                    timeout.Token);

                await SaveStateAsync(
                    value => value with
                    {
                        Synchronized = true,
                        LastSyncUtc = DateTimeOffset.UtcNow
                    },
                    timeout.Token);

                logger.LogInformation(
                    "SYNC COMPLETE: {Source} -> {Destination}",
                    source,
                    other);
            }

            if (state.Active == "replica" && primaryOnline)
            {
                await SaveStateAsync(
                    value => value with { Active = "primary" },
                    timeout.Token);

                logger.LogInformation(
                    "SWITCHBACK: replica -> primary");
            }

            error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            error = exception.Message;

            logger.LogWarning(
                exception,
                "Database replication check or synchronization failed.");
        }
    }

    private async Task RefreshHealthAsync(CancellationToken ct)
    {
        var primaryCheck = ProbeAsync("primary", ct);
        var replicaCheck = ProbeAsync("replica", ct);

        await Task.WhenAll(primaryCheck, replicaCheck);

        primaryOnline = await primaryCheck;
        replicaOnline = await replicaCheck;
    }

    private bool IsOnline(string node)
    {
        return node == "primary" ? primaryOnline : replicaOnline;
    }

    private string ConnectionString(string node)
    {
        var key = node == "primary"
            ? "InternetShopDb"
            : "InternetShopReplicaDb";

        var value = configuration.GetConnectionString(key)
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{key} is required.");

        var builder = new NpgsqlConnectionStringBuilder(value)
        {
            Timeout = settings.ProbeTimeoutSeconds,
            CommandTimeout = settings.SyncTimeoutSeconds,
            ApplicationName = $"InternetShop demo {node}"
        };

        return builder.ConnectionString;
    }

    private async Task<bool> ProbeAsync(
        string node,
        CancellationToken ct)
    {
        try
        {
            await using var connection =
                new NpgsqlConnection(ConnectionString(node));

            await connection.OpenAsync(ct);

            await using var command = new NpgsqlCommand(
                """
                SELECT
                    current_setting('transaction_read_only') = 'off'
                    AND to_regclass('public.customer') IS NOT NULL
                    AND to_regclass('public.product') IS NOT NULL
                    AND to_regclass('public.orders') IS NOT NULL
                    AND to_regclass('public.orderitem') IS NOT NULL
                    AND to_regclass('public.payment') IS NOT NULL;
                """,
                connection)
            {
                CommandTimeout = settings.ProbeTimeoutSeconds
            };

            return await command.ExecuteScalarAsync(ct) is true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Health check failed for {Database}",
                node);

            return false;
        }
    }

    private async Task SaveStateAsync(
        Func<ReplicationState, ReplicationState> update,
        CancellationToken ct)
    {
        await stateMutex.WaitAsync(ct);

        try
        {
            var next = update(state);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
            var temporaryPath = statePath + ".tmp";

            await using (var file = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes.AsMemory(), ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, statePath, overwrite: true);
            state = next;
        }
        finally
        {
            stateMutex.Release();
        }
    }

    public override void Dispose()
    {
        instanceLock?.Dispose();
        base.Dispose();
    }

    private sealed record ReplicationState(
        string Active,
        bool Synchronized,
        DateTimeOffset? LastSyncUtc);
}