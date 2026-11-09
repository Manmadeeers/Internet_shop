using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InternetShop.Messaging;

public abstract class BrokerSubscriber : BackgroundService
{
    private readonly BrokerClient client;
    private readonly ILogger logger;

    protected BrokerSubscriber(
        IOptions<BrokerOptions> options,
        ILogger logger)
    {
        client = new BrokerClient(options);
        this.logger = logger;
    }

    protected abstract string QueueName { get; }

    protected abstract Task HandleAsync(
        EventEnvelope message,
        CancellationToken ct);

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Subscriber is listening to {Queue}",
            QueueName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delivery = await client.ReceiveAsync(
                    QueueName,
                    stoppingToken);

                if (delivery is null)
                {
                    continue;
                }

                try
                {
                    await HandleAsync(delivery.Envelope, stoppingToken);
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
                        "Processing failed. Queue={Queue} EventId={EventId} Attempt={Attempt}",
                        QueueName,
                        delivery.Envelope.EventId,
                        delivery.Attempt);

                    var rejected = await client.RejectAsync(
                        QueueName,
                        delivery.Receipt,
                        exception.Message,
                        stoppingToken);

                    if (!rejected)
                    {
                        logger.LogWarning(
                            "Rejection receipt expired. EventId={EventId}",
                            delivery.Envelope.EventId);
                    }

                    continue;
                }

                var acknowledged = await client.AckAsync(
                    QueueName,
                    delivery.Receipt,
                    stoppingToken);

                if (!acknowledged)
                {
                    logger.LogWarning(
                        "Acknowledgement receipt expired. Event may be delivered again. EventId={EventId}",
                        delivery.Envelope.EventId);
                }
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
                    "Broker communication failed. Queue={Queue}. Retrying.",
                    QueueName);

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    public override void Dispose()
    {
        client.Dispose();
        base.Dispose();
    }
}
