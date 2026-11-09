using InternetShop.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InternetShop.InventorySubscriber;

public sealed class Worker(
    IOptions<BrokerOptions> options,
    IConfiguration configuration,
    ILogger<Worker> logger) : BrokerSubscriber(options, logger)
{
    private readonly int lowStockThreshold =
        configuration.GetValue("Inventory:LowStockThreshold", 5);

    protected override string QueueName => ShopTopology.InventoryQueue;

    protected override Task HandleAsync(
        EventEnvelope message,
        CancellationToken ct)
    {
        if (message.EventType != "StockUpdated")
        {
            throw new InvalidOperationException(
                $"Unexpected event: {message.EventType}");
        }

        var productId = message.Data
            .GetProperty("productId")
            .GetInt32();
        var stockQuantity = message.Data
            .GetProperty("stockQuantity")
            .GetInt32();

        logger.LogInformation(
            "INVENTORY EventId={EventId} Product={ProductId} Stock={StockQuantity}",
            message.EventId,
            productId,
            stockQuantity);

        if (stockQuantity <= lowStockThreshold)
        {
            logger.LogWarning(
                "LOW STOCK Product={ProductId} Stock={StockQuantity} Threshold={Threshold}",
                productId,
                stockQuantity,
                lowStockThreshold);
        }

        return Task.CompletedTask;
    }
}
