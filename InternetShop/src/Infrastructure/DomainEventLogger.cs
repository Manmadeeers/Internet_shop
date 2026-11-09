namespace InternetShop.Api.Infrastructure;

public sealed class DomainEventLogger(
    ILogger<DomainEventLogger> logger,
    ShopEventBuffer? publishBuffer = null)
{
    public void Publish<T>(T domainEvent) where T : notnull
    {
        logger.LogInformation("Domain event {EventName}: {@DomainEvent}", typeof(T).Name, domainEvent);

        if (publishBuffer is not null
            && !publishBuffer.TryPublish(domainEvent))
        {
            logger.LogError(
                "Publishing buffer is full. Event {EventType} was dropped.",
                domainEvent.GetType().Name);
        }
    }
}
