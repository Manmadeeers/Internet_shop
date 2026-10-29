using InternetShop.Api.Domain;

namespace InternetShop.Api.Contracts;

public sealed record OrderItemRequest(int ProductId, int Quantity);

public sealed record CreateOrderRequest(
    int CustomerId,
    List<OrderItemRequest>? Items);

public sealed record CancelOrderRequest(int OrderId);

public sealed record PayOrderRequest(
    int OrderId,
    decimal Amount,
    string? Method);

public sealed record ProductResponse(
    int Id,
    string Sku,
    string Name,
    decimal Price,
    int StockQuantity);

public sealed record CustomerResponse(
    int Id,
    string Name,
    string Email);

public sealed record OrderItemResponse(
    int ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record PaymentResponse(
    int Id,
    int OrderId,
    decimal Amount,
    string Method,
    DateTimeOffset CompletedAt);

public sealed record OrderResponse(
    int Id,
    int CustomerId,
    OrderStatus Status,
    decimal Total,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? CancelledAt,
    IReadOnlyCollection<OrderItemResponse> Items,
    PaymentResponse? Payment);

public sealed record BootstrapResponse(
    int CustomerId,
    string CustomerName,
    string CustomerEmail);
