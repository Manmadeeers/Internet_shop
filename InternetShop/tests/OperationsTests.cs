using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using InternetShop.Api.Contracts;
using InternetShop.Api.Domain;
using Microsoft.AspNetCore.Mvc;

namespace InternetShop.Api.Tests;

[Collection("PostgreSQL integration")]
public sealed class OperationsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task GetProducts_ReturnsSeededCatalog()
    {
        await using var factory = new InternetShopApiFactory(postgres.Container.GetConnectionString());
        using var client = factory.CreateClient();
        var products = await client.GetFromJsonAsync<List<ProductResponse>>("/api/products");
        Assert.NotNull(products);
        Assert.True(products.Count >= 4);
        Assert.All(products, product => Assert.True(product.StockQuantity >= 0));
    }

    [Fact]
    public async Task CreateAndPayOrder_CompletesLifecycle()
    {
        await using var factory = new InternetShopApiFactory(postgres.Container.GetConnectionString());
        using var client = factory.CreateClient();
        var bootstrap = await client.GetFromJsonAsync<BootstrapResponse>("/api/bootstrap");
        var products = await client.GetFromJsonAsync<List<ProductResponse>>("/api/products");
        Assert.NotNull(bootstrap);
        Assert.NotNull(products);
        var product = products.First(x => x.StockQuantity >= 2);

        var createdResponse = await client.PostAsJsonAsync("/api/operations/CreateOrder",
            new CreateOrderRequest(bootstrap.CustomerId, new List<OrderItemRequest>
            {
                new(product.Id, 2)
            }));
        Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal(OrderStatus.Pending, created.Status);
        Assert.Equal(product.Price * 2, created.Total);

        var paidResponse = await client.PostAsJsonAsync("/api/operations/PayOrder",
            new PayOrderRequest(created.Id, created.Total, "Card"));
        Assert.Equal(HttpStatusCode.OK, paidResponse.StatusCode);
        var paid = await paidResponse.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        Assert.NotNull(paid);
        Assert.Equal(OrderStatus.Paid, paid.Status);
        Assert.NotNull(paid.Payment);
    }

    [Fact]
    public async Task CancelOrder_RestoresStock()
    {
        await using var factory = new InternetShopApiFactory(postgres.Container.GetConnectionString());
        using var client = factory.CreateClient();
        var bootstrap = await client.GetFromJsonAsync<BootstrapResponse>("/api/bootstrap");
        var before = await client.GetFromJsonAsync<List<ProductResponse>>("/api/products");
        Assert.NotNull(bootstrap);
        Assert.NotNull(before);
        var product = before.First(x => x.StockQuantity >= 1);

        var createdResponse = await client.PostAsJsonAsync("/api/operations/CreateOrder",
            new CreateOrderRequest(bootstrap.CustomerId, new List<OrderItemRequest>
            {
                new(product.Id, 1)
            }));
        var created = await createdResponse.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        Assert.NotNull(created);

        var cancelledResponse = await client.PostAsJsonAsync("/api/operations/CancelOrder",
            new CancelOrderRequest(created.Id));
        Assert.Equal(HttpStatusCode.OK, cancelledResponse.StatusCode);
        var cancelled = await cancelledResponse.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        Assert.NotNull(cancelled);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);

        var after = await client.GetFromJsonAsync<List<ProductResponse>>("/api/products");
        Assert.NotNull(after);
        Assert.Equal(product.StockQuantity, after.Single(x => x.Id == product.Id).StockQuantity);
    }

    [Fact]
    public async Task UnknownOperation_ReturnsBadRequest()
    {
        await using var factory = new InternetShopApiFactory(postgres.Container.GetConnectionString());
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/operations/DeleteStore", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Incorrect operation", problem?.Title);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
