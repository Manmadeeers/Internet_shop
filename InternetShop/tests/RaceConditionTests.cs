using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using InternetShop.Api.Contracts;
using InternetShop.Api.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace InternetShop.Api.Tests;

[Trait("Category", "RaceCondition")]
[Collection("PostgreSQL integration")]
public sealed class RaceConditionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Theory]
    [InlineData("TransactionCreateOrder", "TransactionCreateOrder")]
    [InlineData("SyncCreateOrder", "SemaphoreCreateOrder")]
    [InlineData("SemaphoreCreateOrder", "SyncCreateOrder")]
    [InlineData("TransactionCreateOrder", "SyncCreateOrder")]
    [InlineData("TransactionCreateOrder", "SemaphoreCreateOrder")]
    [InlineData("SyncCreateOrder", "CreateOrder")]
    public async Task ProtectedModes_AcrossTwoApiHosts_OnlyOneCanBuyLastUnit(string firstMode, string secondMode)
    {
        using var logs = new Probe();
        await using var firstHost = Factory(logs);
        await using var secondHost = Factory(logs);
        using var first = Client(firstHost, "1");
        using var second = Client(secondHost, "2");
        var demo = await Prepare(first);
        var body = new CreateOrderRequest(demo.CustomerId, new List<OrderItemRequest> { new(demo.Product.Id, 1) });
        var a = first.PostAsJsonAsync($"/api/operations/{firstMode}", body);
        await logs.Wait("1", "CHECK");
        var b = second.PostAsJsonAsync($"/api/operations/{secondMode}", body);
        await logs.Wait("2", "DB-WAIT");
        using var response1 = await a.WaitAsync(TimeSpan.FromSeconds(20));
        using var response2 = await b.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response2.StatusCode);
        var state = await first.GetFromJsonAsync<RaceStateResponse>($"/api/lab/products/{demo.Product.Id}/state");
        Assert.NotNull(state);
        Assert.Equal(0, state.StockQuantity);
        Assert.Equal(1, state.OrderedQuantity);
        Assert.Single(state.OrderIds);
    }

    [Fact]
    public async Task ReversedProductOrder_DoesNotDeadlockOrOversell()
    {
        await using var factory = Factory();
        using var first = Client(factory, "1");
        using var second = Client(factory, "2");
        var one = await Prepare(first);
        var two = await Prepare(first);
        var a = first.PostAsJsonAsync("/api/operations/TransactionCreateOrder",
            new CreateOrderRequest(one.CustomerId, new List<OrderItemRequest>
            { new(one.Product.Id, 1), new(two.Product.Id, 1) }));
        var b = second.PostAsJsonAsync("/api/operations/TransactionCreateOrder",
            new CreateOrderRequest(one.CustomerId, new List<OrderItemRequest>
            { new(two.Product.Id, 1), new(one.Product.Id, 1) }));
        using var response1 = await a.WaitAsync(TimeSpan.FromSeconds(20));
        using var response2 = await b.WaitAsync(TimeSpan.FromSeconds(20));
        AssertOneSuccess(response1, response2);
        foreach (var demo in new[] { one, two })
        {
            var state = await first.GetFromJsonAsync<RaceStateResponse>($"/api/lab/products/{demo.Product.Id}/state");
            Assert.NotNull(state);
            Assert.Equal(0, state.StockQuantity);
            Assert.Equal(1, state.OrderedQuantity);
            Assert.Single(state.OrderIds);
        }
    }

    [Theory]
    [InlineData("CancelOrder", "CancelOrder")]
    [InlineData("PayOrder", "PayOrder")]
    [InlineData("PayOrder", "CancelOrder")]
    public async Task ConcurrentTransitions_OnlyOneCommits(string firstOperation, string secondOperation)
    {
        using var logs = new Probe();
        await using var firstHost = Factory(logs);
        await using var secondHost = Factory(logs);
        using var first = Client(firstHost, "1");
        using var second = Client(secondHost, "2");
        var demo = await Prepare(first);
        using var created = await first.PostAsJsonAsync("/api/operations/CreateOrder",
            new CreateOrderRequest(demo.CustomerId, new List<OrderItemRequest> { new(demo.Product.Id, 1) }));
        created.EnsureSuccessStatusCode();
        var order = await created.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        Assert.NotNull(order);

        await using var connection = new NpgsqlConnection(postgres.Container.GetConnectionString());
        await connection.OpenAsync();
        await using var gate = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(82, @id)", connection, gate))
        {
            command.Parameters.AddWithValue("id", order.Id);
            await command.ExecuteNonQueryAsync();
        }
        var a = Transition(first, firstOperation, order);
        var b = Transition(second, secondOperation, order);
        await logs.Wait("1", "ORDER-WAIT");
        await logs.Wait("2", "ORDER-WAIT");
        await gate.CommitAsync();
        using var response1 = await a.WaitAsync(TimeSpan.FromSeconds(20));
        using var response2 = await b.WaitAsync(TimeSpan.FromSeconds(20));
        AssertOneSuccess(response1, response2);

        var final = await first.GetFromJsonAsync<OrderResponse>($"/api/orders/{order.Id}", JsonOptions);
        var state = await first.GetFromJsonAsync<RaceStateResponse>($"/api/lab/products/{demo.Product.Id}/state");
        Assert.NotNull(final);
        Assert.NotNull(state);
        if (final.Status == OrderStatus.Cancelled)
        {
            Assert.Null(final.Payment);
            Assert.Null(final.PaidAt);
            Assert.NotNull(final.CancelledAt);
            Assert.Equal(1, state.StockQuantity);
            Assert.Equal(0, state.OrderedQuantity);
        }
        else
        {
            Assert.Equal(OrderStatus.Paid, final.Status);
            Assert.NotNull(final.Payment);
            Assert.NotNull(final.PaidAt);
            Assert.Null(final.CancelledAt);
            Assert.Equal(order.Total, final.Payment.Amount);
            Assert.Equal(0, state.StockQuantity);
            Assert.Equal(1, state.OrderedQuantity);
        }
    }

    [Fact]
    public async Task CancelAndCreate_ShareProductLockAndPreserveTotalStock()
    {
        using var logs = new Probe();
        await using var factory = Factory(logs);
        using var setup = Client(factory, "99");
        using var cancelling = Client(factory, "1");
        using var buying = Client(factory, "2");
        var demo = await Prepare(setup);
        var body = new CreateOrderRequest(demo.CustomerId, new List<OrderItemRequest> { new(demo.Product.Id, 1) });
        using var creation = await setup.PostAsJsonAsync("/api/operations/CreateOrder", body);
        creation.EnsureSuccessStatusCode();
        var order = await creation.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        Assert.NotNull(order);

        await using var connection = new NpgsqlConnection(postgres.Container.GetConnectionString());
        await connection.OpenAsync();
        await using var gate = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(81, @id)", connection, gate))
        {
            command.Parameters.AddWithValue("id", demo.Product.Id);
            await command.ExecuteNonQueryAsync();
        }
        var a = cancelling.PostAsJsonAsync("/api/operations/CancelOrder", new CancelOrderRequest(order.Id));
        var b = buying.PostAsJsonAsync("/api/operations/TransactionCreateOrder", body);
        await logs.Wait("1", "DB-WAIT");
        await logs.Wait("2", "DB-WAIT");
        await gate.CommitAsync();
        using var cancelled = await a.WaitAsync(TimeSpan.FromSeconds(20));
        using var bought = await b.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Contains(bought.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest });
        var state = await setup.GetFromJsonAsync<RaceStateResponse>($"/api/lab/products/{demo.Product.Id}/state");
        Assert.NotNull(state);
        Assert.Equal(1L, state.StockQuantity + state.OrderedQuantity);
        Assert.Equal(bought.IsSuccessStatusCode ? 0 : 1, state.StockQuantity);
        Assert.Equal(bought.IsSuccessStatusCode ? 1 : 0, state.OrderIds.Count);
    }

    private InternetShopApiFactory Factory(ILoggerProvider? logs = null) =>
        new(postgres.Container.GetConnectionString(), raceDelaySeconds: 1, logs: logs);

    private static HttpClient Client(InternetShopApiFactory factory, string id)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", id);
        return client;
    }

    private static async Task<RaceDemoResponse> Prepare(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/lab/prepare-race", new { });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RaceDemoResponse>())!;
    }

    private static Task<HttpResponseMessage> Transition(HttpClient client, string operation, OrderResponse order)
    {
        object body = operation == "PayOrder"
            ? new PayOrderRequest(order.Id, order.Total, "Card")
            : new CancelOrderRequest(order.Id);
        return client.PostAsJsonAsync($"/api/operations/{operation}", body);
    }

    private static void AssertOneSuccess(params HttpResponseMessage[] responses)
    {
        Assert.Single(responses.Where(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Single(responses.Where(x => x.StatusCode == HttpStatusCode.BadRequest));
    }

    private sealed class Probe : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Client, string Phase)> entries = new();
        public ILogger CreateLogger(string categoryName) => new Capture(entries);
        public void Dispose() { }

        public async Task Wait(string client, string phase)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!entries.Contains((client, phase))) await Task.Delay(10, timeout.Token);
        }

        private sealed class Capture(ConcurrentQueue<(string Client, string Phase)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
                var fields = values.ToDictionary(x => x.Key, x => x.Value);
                if (fields.TryGetValue("Phase", out var phase) && fields.TryGetValue("ClientId", out var client))
                    entries.Enqueue(((string)client!, (string)phase!));
            }
        }
    }
}
