using System.Text.Json;
using System.Threading.Channels;
using InternetShop.Messaging;

namespace InternetShop.Api.Infrastructure;

public sealed record PendingEvent(
    string RoutingKey,
    EventEnvelope Envelope);

public sealed class ShopEventBuffer
{
    private readonly Channel<PendingEvent> channel =
        Channel.CreateBounded<PendingEvent>(
            new BoundedChannelOptions(10_000)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

    public ChannelReader<PendingEvent> Reader => channel.Reader;

    public bool TryPublish(object domainEvent)
    {
        var eventType = domainEvent.GetType();
        var routingKey = eventType.Name switch
        {
            "OrderCreated" => ShopTopology.OrderCreatedKey,
            "OrderCancelled" => ShopTopology.OrderCancelledKey,
            "PaymentCompleted" => ShopTopology.PaymentCompletedKey,
            "StockUpdated" => ShopTopology.StockUpdatedKey,
            _ => $"internetshop.other.{eventType.Name.ToLowerInvariant()}"
        };

        var data = JsonSerializer.SerializeToElement(
            domainEvent,
            eventType,
            EventJson.Options);

        var envelope = new EventEnvelope(
            Guid.NewGuid(),
            eventType.Name,
            1,
            DateTimeOffset.UtcNow,
            data);

        return channel.Writer.TryWrite(
            new PendingEvent(routingKey, envelope));
    }
}
