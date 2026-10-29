using InternetShop.Api.Contracts;
using InternetShop.Api.Data;
using InternetShop.Api.Domain;
using InternetShop.Api.Exceptions;
using InternetShop.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InternetShop.Api.Services;

public sealed class SyncOrderService(
    ShopDbContext db,
    ShopService shop,
    DomainEventLogger events,
    RequestExecution execution,
    IOptions<LabOptions> options,
    ILogger<SyncOrderService> logger)
{
    private static readonly object OrderLock = new();

    public async Task<OrderResponse> CreateOrderAsync(
        CreateOrderRequest request,
        CancellationToken ct)
    {
        if (request.CustomerId <= 0)
        {
            throw new IncorrectInputDataException(
                "Customer ID must be positive.");
        }

        if (request.Items is not { Count: > 0 } ||
            request.Items.Any(x => x is null))
        {
            throw new IncorrectInputDataException(
                "Order must contain at least one product.");
        }

        if (request.Items.Any(x => x.ProductId <= 0 || x.Quantity <= 0))
        {
            throw new IncorrectInputDataException(
                "Product IDs and quantities must be positive.");
        }

        if (request.Items.Select(x => x.ProductId).Distinct().Count() !=
            request.Items.Count)
        {
            throw new IncorrectInputDataException(
                "A product occurs more than once in the order.");
        }

        if (!await db.Customers.AnyAsync(
                x => x.Id == request.CustomerId, ct))
        {
            throw new ObjectMissingException(
                $"Customer '{request.CustomerId}' was not found.");
        }

        var productIds = request.Items
            .Select(x => x.ProductId)
            .OrderBy(x => x)
            .ToList();

        Order order;
        var stockEvents = new List<StockUpdated>();

        execution.Write(
            logger,
            "WAIT",
            $"Products={string.Join(",", productIds)}");

        lock (OrderLock)
        {
            execution.Write(
                logger,
                "LOCK",
                $"Products={string.Join(",", productIds)}");

            try
            {
                ct.ThrowIfCancellationRequested();

                var products = db.Products
                    .AsTracking()
                    .Where(x => productIds.Contains(x.Id))
                    .ToDictionary(x => x.Id);

                foreach (var productId in productIds)
                {
                    if (!products.ContainsKey(productId))
                    {
                        throw new ObjectMissingException(
                            $"Product '{productId}' was not found.");
                    }
                }

                foreach (var requestedItem in request.Items)
                {
                    var product = products[requestedItem.ProductId];

                    if (product.StockQuantity < requestedItem.Quantity)
                    {
                        throw new IncorrectInputDataException(
                            $"Not enough '{product.Name}' in stock; " +
                            $"requested {requestedItem.Quantity}, " +
                            $"available {product.StockQuantity}.");
                    }

                    execution.Write(
                        logger,
                        "CHECK",
                        $"Product={product.Id} " +
                        $"Available={product.StockQuantity} " +
                        $"Requested={requestedItem.Quantity}");
                }

                ct.WaitHandle.WaitOne(
                    TimeSpan.FromSeconds(options.Value.RaceDelaySeconds));

                ct.ThrowIfCancellationRequested();

                order = new Order
                {
                    CustomerId = request.CustomerId,
                    Status = OrderStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                foreach (var requestedItem in request.Items)
                {
                    var product = products[requestedItem.ProductId];

                    product.StockQuantity -= requestedItem.Quantity;

                    order.Items.Add(new OrderItem
                    {
                        ProductId = product.Id,
                        Quantity = requestedItem.Quantity,
                        UnitPrice = product.Price
                    });
                }

                order.Total = order.Items.Sum(
                    x => x.UnitPrice * x.Quantity);

                db.Orders.Add(order);

                ct.ThrowIfCancellationRequested();
                db.SaveChanges();

                foreach (var productId in productIds)
                {
                    var product = products[productId];

                    execution.Write(
                        logger,
                        "WRITE",
                        $"Product={productId} " +
                        $"Available={product.StockQuantity} " +
                        $"Order={order.Id}");

                    stockEvents.Add(new StockUpdated(
                        productId,
                        product.StockQuantity,
                        DateTimeOffset.UtcNow));
                }
            }
            finally
            {
                execution.Write(
                    logger,
                    "UNLOCK",
                    $"Products={string.Join(",", productIds)}");
            }
        }

        events.Publish(new OrderCreated(
            order.Id,
            order.CustomerId,
            order.Total,
            DateTimeOffset.UtcNow));

        foreach (var stockEvent in stockEvents)
        {
            events.Publish(stockEvent);
        }

        return await shop.GetOrderAsync(order.Id, ct);
    }
}