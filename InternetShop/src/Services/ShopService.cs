using InternetShop.Api.Contracts;
using InternetShop.Api.Data;
using InternetShop.Api.Domain;
using InternetShop.Api.Exceptions;
using InternetShop.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InternetShop.Api.Services;

public sealed class ShopService(
    ShopDbContext db, DomainEventLogger events,
    RequestExecution execution, IOptions<LabOptions> options,
    ILogger<ShopService> logger)
{
    public async Task<IReadOnlyCollection<ProductResponse>> GetProductsAsync(CancellationToken ct)
    {
        return await db.Products.AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new ProductResponse(x.Id, x.Sku, x.Name, x.Price, x.StockQuantity))
            .ToListAsync(ct);
    }

    public Task<OrderResponse> CreateOrderAsync(CreateOrderRequest request, CancellationToken ct) =>
        CreateOrderCoreAsync(request, false, ct);

    public Task<OrderResponse> TransactionCreateOrderAsync(CreateOrderRequest request, CancellationToken ct) =>
        CreateOrderCoreAsync(request, true, ct);

    private static readonly object OrderLock = new();

    private async Task<OrderResponse> CreateOrderCoreAsync(
        CreateOrderRequest request,
        bool useLabDelay,
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
            try
            {
                execution.Write(
                    logger,
                    "LOCK",
                    $"Products={string.Join(",", productIds)}");

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

                if (useLabDelay)
                {
                    ct.WaitHandle.WaitOne(
                        TimeSpan.FromSeconds(options.Value.RaceDelaySeconds));
                }

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

        return await GetOrderAsync(order.Id, ct);
    }

    public async Task<OrderResponse> CancelOrderAsync(CancelOrderRequest request, CancellationToken ct)
    {
        if (request.OrderId <= 0)
            throw new IncorrectInputDataException("Order ID must be positive.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockOrderAsync(request.OrderId, ct);

        var order = await db.Orders.Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.Id == request.OrderId, ct);
        if (order is null)
            throw new ObjectMissingException($"Order '{request.OrderId}' was not found.");
        if (order.Status != OrderStatus.Pending)
            throw new IncorrectInputDataException("Only pending orders can be cancelled.");

        var productIds = order.Items.Select(x => x.ProductId).OrderBy(x => x).ToList();
        foreach (var productId in productIds)
        {
            execution.Write(logger, "DB-WAIT", $"Product={productId}");
            await LockProductAsync(productId, ct);
            execution.Write(logger, "DB-LOCK", $"Product={productId}");
        }

        var products = await db.Products
            .Where(x => productIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        foreach (var item in order.Items)
        {
            var product = products[item.ProductId];
            if ((long)product.StockQuantity + item.Quantity > int.MaxValue)
                throw new IncorrectInputDataException("Restoring this order would overflow product stock.");
            product.StockQuantity += item.Quantity;
        }

        order.Status = OrderStatus.Cancelled;
        order.CancelledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        events.Publish(new OrderCancelled(order.Id, order.CustomerId, DateTimeOffset.UtcNow));
        foreach (var productId in productIds)
            PublishStockUpdated(products[productId]);

        return await GetOrderAsync(order.Id, ct);
    }


    //Intentionaly failing transaction method
    public async Task<OrderResponse> PayOrderFailAsync(PayOrderRequest request, CancellationToken ct, bool intentionalFail = true)
    {
        if (request.OrderId <= 0)
        {
            throw new IncorrectInputDataException("Order ID must be positive.");
        }

        if (request.Amount <= 0)
        {
            throw new IncorrectInputDataException("Payment amount must be positive.");

        }

        var method = request.Method?.Trim();
        if (string.IsNullOrWhiteSpace(method) || method.Length > 50)
        {
            throw new IncorrectInputDataException("Payment method must contain 1 to 50 characters.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockOrderAsync(request.OrderId, ct);

        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == request.OrderId, ct);
        if (order is null)
        {
            throw new ObjectMissingException($"Order '{request.OrderId}' was not found.");
        }

        if (order.Status != OrderStatus.Pending)
        {
            throw new IncorrectInputDataException("Only pending orders can be paid.");
        }

        if (request.Amount != order.Total)
        {
            throw new IncorrectInputDataException(
                $"Payment amount must equal the order total of {order.Total:0.00}.");
        }


        var completedAt = DateTimeOffset.UtcNow;
        var payment = new Payment
        {
            OrderId = order.Id,
            Amount = request.Amount,
            Method = method,
            CompletedAt = completedAt
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);


        if (intentionalFail)
        {
            throw new InvalidOperationException("Transaction intentionally failed. Check the database");
        }


        order.Status = OrderStatus.Paid;
        order.PaidAt = completedAt;
        await db.SaveChangesAsync(ct);


        await transaction.CommitAsync(ct);

        events.Publish(new PaymentCompleted(payment.Id, order.Id, payment.Amount, completedAt));
        return await GetOrderAsync(order.Id, ct);
    }

    //Intentionally failing method with no transactions
    public async Task<OrderResponse> PayOrderFailNoTranAsync(PayOrderRequest request, CancellationToken ct, bool intentionalFail = true)
    {
        if (request.OrderId <= 0)
        {
            throw new IncorrectInputDataException("Order ID must be positive.");
        }

        if (request.Amount <= 0)
        {
            throw new IncorrectInputDataException("Payment amount must be positive.");

        }

        var method = request.Method?.Trim();
        if (string.IsNullOrWhiteSpace(method) || method.Length > 50)
        {
            throw new IncorrectInputDataException("Payment method must contain 1 to 50 characters.");
        }

        await LockOrderAsync(request.OrderId, ct);

        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == request.OrderId, ct);
        if (order is null)
        {
            throw new ObjectMissingException($"Order '{request.OrderId}' was not found.");
        }

        if (order.Status != OrderStatus.Pending)
        {
            throw new IncorrectInputDataException("Only pending orders can be paid.");
        }

        if (request.Amount != order.Total)
        {
            throw new IncorrectInputDataException(
                $"Payment amount must equal the order total of {order.Total:0.00}.");
        }


        var completedAt = DateTimeOffset.UtcNow;
        var payment = new Payment
        {
            OrderId = order.Id,
            Amount = request.Amount,
            Method = method,
            CompletedAt = completedAt
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);


        if (intentionalFail)
        {
            throw new InvalidOperationException("Transaction intentionally failed. Check the database");
        }


        order.Status = OrderStatus.Paid;
        order.PaidAt = completedAt;

        await db.SaveChangesAsync(ct);

        events.Publish(new PaymentCompleted(payment.Id, order.Id, payment.Amount, completedAt));
        return await GetOrderAsync(order.Id, ct);
    }

    //
    public async Task<OrderResponse> PayOrderAsync(PayOrderRequest request, CancellationToken ct)
    {
        if (request.OrderId <= 0)
        {
            throw new IncorrectInputDataException("Order ID must be positive.");
        }

        if (request.Amount <= 0)
        {
            throw new IncorrectInputDataException("Payment amount must be positive.");

        }

        var method = request.Method?.Trim();
        if (string.IsNullOrWhiteSpace(method) || method.Length > 50)
        {
            throw new IncorrectInputDataException("Payment method must contain 1 to 50 characters.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockOrderAsync(request.OrderId, ct);

        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == request.OrderId, ct);
        if (order is null)
        {
            throw new ObjectMissingException($"Order '{request.OrderId}' was not found.");
        }

        if (order.Status != OrderStatus.Pending)
        {
            throw new IncorrectInputDataException("Only pending orders can be paid.");
        }

        if (request.Amount != order.Total)
        {
            throw new IncorrectInputDataException(
                $"Payment amount must equal the order total of {order.Total:0.00}.");
        }


        var completedAt = DateTimeOffset.UtcNow;
        var payment = new Payment
        {
            OrderId = order.Id,
            Amount = request.Amount,
            Method = method,
            CompletedAt = completedAt
        };

        db.Payments.Add(payment);
        order.Status = OrderStatus.Paid;
        order.PaidAt = completedAt;

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        events.Publish(new PaymentCompleted(payment.Id, order.Id, payment.Amount, completedAt));
        return await GetOrderAsync(order.Id, ct);
    }

    public async Task<OrderResponse> GetOrderAsync(int orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking()
            .Include(x => x.Items).ThenInclude(x => x.Product)
            .Include(x => x.Payment)
            .SingleOrDefaultAsync(x => x.Id == orderId, ct);
        if (order is null)
            throw new ObjectMissingException($"Order '{orderId}' was not found.");

        return ToResponse(order);
    }

    public async Task<IReadOnlyCollection<OrderResponse>> GetOrdersAsync(CancellationToken ct)
    {
        var orders = await db.Orders.AsNoTracking()
            .Include(x => x.Items).ThenInclude(x => x.Product)
            .Include(x => x.Payment)
            .OrderByDescending(x => x.Id)
            .ToListAsync(ct);
        return orders.Select(ToResponse).ToList();
    }

    public async Task<BootstrapResponse> GetBootstrapAsync(CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Email == "demo@internetshop.local", ct);
        if (customer is null)
            throw new ObjectMissingException("Run database/seed.sql to create the demo customer.");
        return new BootstrapResponse(customer.Id, customer.Name, customer.Email);
    }

    private static OrderResponse ToResponse(Order order)
    {
        var items = order.Items.OrderBy(x => x.ProductId)
            .Select(x => new OrderItemResponse(
                x.ProductId,
                x.Product.Name,
                x.Quantity,
                x.UnitPrice,
                x.UnitPrice * x.Quantity))
            .ToList();
        var payment = order.Payment is null
            ? null
            : new PaymentResponse(
                order.Payment.Id,
                order.Payment.OrderId,
                order.Payment.Amount,
                order.Payment.Method,
                order.Payment.CompletedAt);
        return new OrderResponse(
            order.Id,
            order.CustomerId,
            order.Status,
            order.Total,
            order.CreatedAt,
            order.PaidAt,
            order.CancelledAt,
            items,
            payment);
    }

    private async Task LockProductAsync(int productId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({81}, {productId})", ct);
    }

    private async Task LockOrderAsync(int orderId, CancellationToken ct)
    {
        execution.Write(logger, "ORDER-WAIT", $"Order={orderId}");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({82}, {orderId})", ct);
        execution.Write(logger, "ORDER-LOCK", $"Order={orderId}");
    }

    private void PublishStockUpdated(Product product)
    {
        events.Publish(new StockUpdated(product.Id, product.StockQuantity, DateTimeOffset.UtcNow));
    }
}
