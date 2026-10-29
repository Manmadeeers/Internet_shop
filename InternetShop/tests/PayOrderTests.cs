using InternetShop.Api.Contracts;
using InternetShop.Api.Data;
using InternetShop.Api.Domain;
using InternetShop.Api.Exceptions;
using InternetShop.Api.Infrastructure;
using InternetShop.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace InternetShop.Api.Tests;

[Trait("Category", "Payments")]
[Collection("PostgreSQL integration")]
public sealed class PayOrderTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private const string IntentionalFailureMessage =
        "Transaction intentionally failed. Check the database";

    [Theory]
    [InlineData("PayOrder")]
    [InlineData("PayOrderFail")]
    [InlineData("PayOrderFailNoTran")]
    public async Task Payment_WithoutIntentionalFailure_SavesPaymentAndMarksOrderPaid(
        string operation)
    {
        var order = await PrepareOrderAsync();
        var request = new PayOrderRequest(order.Id, order.Total, "  Card  ");

        var response = await ExecutePaymentAsync(
            operation, request, intentionalFail: false);

        Assert.Equal(order.Id, response.Id);
        Assert.Equal(OrderStatus.Paid, response.Status);
        Assert.NotNull(response.PaidAt);
        Assert.Null(response.CancelledAt);
        Assert.NotNull(response.Payment);
        Assert.Equal(order.Id, response.Payment.OrderId);
        Assert.Equal(order.Total, response.Payment.Amount);
        Assert.Equal("Card", response.Payment.Method);
        Assert.Equal(response.PaidAt.Value, response.Payment.CompletedAt);

        await AssertPaidAsync(order);
    }

    [Fact]
    public async Task PayOrderFail_RollsBackPaymentAndAllowsSuccessfulRetry()
    {
        var order = await PrepareOrderAsync();
        var request = new PayOrderRequest(order.Id, order.Total, "Card");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecutePaymentAsync("PayOrderFail", request));

        Assert.Equal(IntentionalFailureMessage, exception.Message);
        await AssertPendingWithoutPaymentAsync(order);

        var response = await ExecutePaymentAsync("PayOrder", request);

        Assert.Equal(OrderStatus.Paid, response.Status);
        await AssertPaidAsync(order);
    }

    [Fact]
    public async Task PayOrderFailNoTran_KeepsSavedPaymentButLeavesOrderPending()
    {
        var order = await PrepareOrderAsync();
        var request = new PayOrderRequest(order.Id, order.Total, "Card");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecutePaymentAsync("PayOrderFailNoTran", request));

        Assert.Equal(IntentionalFailureMessage, exception.Message);

        await using var db = OpenDb();
        var savedOrder = await db.Orders.AsNoTracking()
            .SingleAsync(x => x.Id == order.Id);
        var payments = await db.Payments.AsNoTracking()
            .Where(x => x.OrderId == order.Id)
            .ToListAsync();

        Assert.Equal(OrderStatus.Pending, savedOrder.Status);
        Assert.Null(savedOrder.PaidAt);
        Assert.Null(savedOrder.CancelledAt);

        var payment = Assert.Single(payments);
        Assert.True(payment.Id > 0);
        Assert.Equal(order.Id, payment.OrderId);
        Assert.Equal(order.Total, payment.Amount);
        Assert.Equal("Card", payment.Method);
        Assert.NotEqual(default(DateTimeOffset), payment.CompletedAt);

        await AssertOrderContentsUnchangedAsync(db, order);
    }

    [Theory]
    [InlineData("PayOrder")]
    [InlineData("PayOrderFail")]
    [InlineData("PayOrderFailNoTran")]
    public async Task Payment_WithWrongAmount_DoesNotSavePaymentOrChangeOrder(
        string operation)
    {
        var order = await PrepareOrderAsync();
        var request = new PayOrderRequest(order.Id, order.Total + 1m, "Card");

        await Assert.ThrowsAsync<IncorrectInputDataException>(
            () => ExecutePaymentAsync(operation, request));

        await AssertPendingWithoutPaymentAsync(order);
    }

    [Fact]
    public async Task PayOrder_AlreadyPaidOrder_DoesNotCreateSecondPayment()
    {
        var order = await PrepareOrderAsync();
        var request = new PayOrderRequest(order.Id, order.Total, "Card");

        await ExecutePaymentAsync("PayOrder", request);

        DateTimeOffset? originalPaidAt;
        int originalPaymentId;

        await using (var db = OpenDb())
        {
            originalPaidAt = await db.Orders.AsNoTracking()
                .Where(x => x.Id == order.Id)
                .Select(x => x.PaidAt)
                .SingleAsync();
            originalPaymentId = await db.Payments.AsNoTracking()
                .Where(x => x.OrderId == order.Id)
                .Select(x => x.Id)
                .SingleAsync();
        }

        await Assert.ThrowsAsync<IncorrectInputDataException>(
            () => ExecutePaymentAsync("PayOrder", request));

        await AssertPaidAsync(order);

        await using var verificationDb = OpenDb();
        var savedOrder = await verificationDb.Orders.AsNoTracking()
            .SingleAsync(x => x.Id == order.Id);
        var savedPayment = await verificationDb.Payments.AsNoTracking()
            .SingleAsync(x => x.OrderId == order.Id);

        Assert.Equal(originalPaidAt, savedOrder.PaidAt);
        Assert.Equal(originalPaymentId, savedPayment.Id);
    }

    private ShopDbContext OpenDb()
    {
        var dbOptions = new DbContextOptionsBuilder<ShopDbContext>()
            .UseNpgsql(postgres.Container.GetConnectionString())
            .Options;

        return new ShopDbContext(dbOptions);
    }

    private static ShopService CreateService(ShopDbContext db)
    {
        return new ShopService(
            db,
            new DomainEventLogger(NullLogger<DomainEventLogger>.Instance),
            new RequestExecution(),
            Options.Create(new LabOptions
            {
                Enabled = true,
                RequestDelaySeconds = 0,
                RaceDelaySeconds = 0
            }),
            NullLogger<ShopService>.Instance);
    }

    private async Task<OrderResponse> ExecutePaymentAsync(
        string operation,
        PayOrderRequest request,
        bool intentionalFail = true)
    {
        await using var db = OpenDb();
        var service = CreateService(db);

        return operation switch
        {
            "PayOrder" => await service.PayOrderAsync(
                request, CancellationToken.None),
            "PayOrderFail" => await service.PayOrderFailAsync(
                request, CancellationToken.None, intentionalFail),
            "PayOrderFailNoTran" => await service.PayOrderFailNoTranAsync(
                request, CancellationToken.None, intentionalFail),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private async Task<TestOrder> PrepareOrderAsync()
    {
        await using var db = OpenDb();
        var token = Guid.NewGuid().ToString("N");
        var customer = new Customer
        {
            Name = "Payment test customer",
            Email = $"payment-{token}@internetshop.test"
        };
        var product = new Product
        {
            Sku = $"PAYMENT-TEST-{token}",
            Name = "Lab 2: last unit",
            Price = 1m,
            StockQuantity = 0
        };
        var order = new Order
        {
            Customer = customer,
            Status = OrderStatus.Pending,
            Total = product.Price,
            CreatedAt = DateTimeOffset.UtcNow
        };

        order.Items.Add(new OrderItem
        {
            Product = product,
            Quantity = 1,
            UnitPrice = product.Price
        });

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        return new TestOrder(order.Id, customer.Id, product.Id, order.Total);
    }

    private async Task AssertPaidAsync(TestOrder order)
    {
        await using var db = OpenDb();
        var savedOrder = await db.Orders.AsNoTracking()
            .SingleAsync(x => x.Id == order.Id);
        var payments = await db.Payments.AsNoTracking()
            .Where(x => x.OrderId == order.Id)
            .ToListAsync();

        Assert.Equal(OrderStatus.Paid, savedOrder.Status);
        Assert.NotNull(savedOrder.PaidAt);
        Assert.Null(savedOrder.CancelledAt);

        var payment = Assert.Single(payments);
        Assert.True(payment.Id > 0);
        Assert.Equal(order.Id, payment.OrderId);
        Assert.Equal(order.Total, payment.Amount);
        Assert.Equal("Card", payment.Method);
        Assert.Equal(savedOrder.PaidAt.Value, payment.CompletedAt);

        await AssertOrderContentsUnchangedAsync(db, order);
    }

    private async Task AssertPendingWithoutPaymentAsync(TestOrder order)
    {
        await using var db = OpenDb();
        var savedOrder = await db.Orders.AsNoTracking()
            .SingleAsync(x => x.Id == order.Id);

        Assert.Equal(OrderStatus.Pending, savedOrder.Status);
        Assert.Null(savedOrder.PaidAt);
        Assert.Null(savedOrder.CancelledAt);
        Assert.False(await db.Payments.AnyAsync(x => x.OrderId == order.Id));

        await AssertOrderContentsUnchangedAsync(db, order);
    }

    private static async Task AssertOrderContentsUnchangedAsync(
        ShopDbContext db,
        TestOrder order)
    {
        var savedOrder = await db.Orders.AsNoTracking()
            .SingleAsync(x => x.Id == order.Id);
        var savedProduct = await db.Products.AsNoTracking()
            .SingleAsync(x => x.Id == order.ProductId);
        var items = await db.OrderItems.AsNoTracking()
            .Where(x => x.OrderId == order.Id)
            .ToListAsync();

        Assert.Equal(order.CustomerId, savedOrder.CustomerId);
        Assert.Equal(order.Total, savedOrder.Total);
        Assert.Equal(0, savedProduct.StockQuantity);

        var item = Assert.Single(items);
        Assert.Equal(order.ProductId, item.ProductId);
        Assert.Equal(1, item.Quantity);
        Assert.Equal(order.Total, item.UnitPrice);
    }

    private sealed record TestOrder(
        int Id,
        int CustomerId,
        int ProductId,
        decimal Total);
}
