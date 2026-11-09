using InternetShop.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InternetShop.NotificationSubscriber;

public sealed class Worker(
    IOptions<BrokerOptions> options,
    ILogger<Worker> logger) : BrokerSubscriber(options, logger)
{
    protected override string QueueName => ShopTopology.NotificationQueue;

    protected override Task HandleAsync(
        EventEnvelope message,
        CancellationToken ct)
    {
        switch (message.EventType)
        {
            case "OrderCreated":
                logger.LogInformation(
                    "NOTIFY Order created. EventId={EventId} Order={OrderId} Customer={CustomerId} Total={Total}",
                    message.EventId,
                    message.Data.GetProperty("orderId").GetInt32(),
                    message.Data.GetProperty("customerId").GetInt32(),
                    message.Data.GetProperty("total").GetDecimal());
                break;

            case "OrderCancelled":
                logger.LogWarning(
                    "NOTIFY Order cancelled. EventId={EventId} Order={OrderId} Customer={CustomerId}",
                    message.EventId,
                    message.Data.GetProperty("orderId").GetInt32(),
                    message.Data.GetProperty("customerId").GetInt32());
                break;

            case "PaymentCompleted":
                logger.LogInformation(
                    "NOTIFY Payment completed. EventId={EventId} Payment={PaymentId} Order={OrderId} Amount={Amount}",
                    message.EventId,
                    message.Data.GetProperty("paymentId").GetInt32(),
                    message.Data.GetProperty("orderId").GetInt32(),
                    message.Data.GetProperty("amount").GetDecimal());
                break;

            default:
                throw new InvalidOperationException(
                    $"Unexpected event: {message.EventType}");
        }

        return Task.CompletedTask;
    }
}
