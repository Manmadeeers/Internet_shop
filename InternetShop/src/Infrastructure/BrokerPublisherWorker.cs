using InternetShop.Messaging;
using Microsoft.Extensions.Options;

namespace InternetShop.Api.Infrastructure;

public sealed class BrokerPublisherWorker(
    ShopEventBuffer buffer,
    IOptions<BrokerOptions> options,
    ILogger<BrokerPublisherWorker> logger) : BackgroundService
{
    private readonly BrokerClient client = new(options);

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        try
        {
            await foreach (
                var pending in buffer.Reader.ReadAllAsync(stoppingToken))
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await client.PublishAsync(
                            pending.RoutingKey,
                            pending.Envelope,
                            stoppingToken);
                        break;
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            exception,
                            "Publishing failed. Retrying EventId={EventId}",
                            pending.Envelope.EventId);

                        await Task.Delay(
                            TimeSpan.FromSeconds(2),
                            stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override void Dispose()
    {
        client.Dispose();
        base.Dispose();
    }
}
