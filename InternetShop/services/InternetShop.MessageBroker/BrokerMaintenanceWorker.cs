namespace InternetShop.MessageBroker;

public sealed class BrokerMaintenanceWorker(
    BrokerStore store,
    ILogger<BrokerMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!store.Ready)
                {
                    await store.InitializeAsync(stoppingToken);
                    logger.LogInformation("Broker database initialized.");
                }

                await store.RecoverExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Broker maintenance failed. Retrying.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
