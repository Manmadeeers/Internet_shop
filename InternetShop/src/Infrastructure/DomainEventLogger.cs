namespace InternetShop.Api.Infrastructure;

public sealed class DomainEventLogger(ILogger<DomainEventLogger> logger)
{
    public void Publish<T>(T domainEvent) where T : notnull
    {
        logger.LogInformation("Domain event {EventName}: {@DomainEvent}", typeof(T).Name, domainEvent);
    }
}
