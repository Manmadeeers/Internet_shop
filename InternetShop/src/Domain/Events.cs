namespace InternetShop.Api.Domain;

public sealed record OrderCreated(
    int OrderId,
    int CustomerId,
    decimal Total,
    DateTimeOffset OccurredAt);

public sealed record OrderCancelled(
    int OrderId,
    int CustomerId,
    DateTimeOffset OccurredAt);

public sealed record PaymentCompleted(
    int PaymentId,
    int OrderId,
    decimal Amount,
    DateTimeOffset OccurredAt);

public sealed record StockUpdated(
    int ProductId,
    int StockQuantity,
    DateTimeOffset OccurredAt);
