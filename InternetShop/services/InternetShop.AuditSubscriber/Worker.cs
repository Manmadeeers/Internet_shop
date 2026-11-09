using InternetShop.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InternetShop.AuditSubscriber;

public sealed class Worker(
    IOptions<BrokerOptions> options,
    ILogger<Worker> logger) : BrokerSubscriber(options, logger)
{
    protected override string QueueName => ShopTopology.AuditQueue;

    protected override Task HandleAsync(
        EventEnvelope message,
        CancellationToken ct)
    {
        logger.LogInformation(
            "AUDIT EventId={EventId} Type={EventType} OccurredAt={OccurredAt} Data={Data}",
            message.EventId,
            message.EventType,
            message.OccurredAtUtc,
            message.Data.GetRawText());

        return Task.CompletedTask;
    }
}
