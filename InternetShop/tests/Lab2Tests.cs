using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InternetShop.Api.Contracts;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace InternetShop.Api.Tests;

// HTTP integration tests with an in-process server and real PostgreSQL.
[Collection("PostgreSQL integration")]
public sealed class Lab2Tests(PostgresFixture postgres, ITestOutputHelper output)
    : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task OneClient_CreatesOrderAndDecreasesStock()
    {
        await using var factory = Factory();
        using var client = Client(factory, 1);
        var demo = await Prepare(client);
        using var response = await CreateOrder(client, demo);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        Assert.NotNull(order);
        Assert.Equal(demo.CustomerId, order.CustomerId);
        var item = Assert.Single(order.Items);
        Assert.Equal(demo.Product.Id, item.ProductId);
        Assert.Equal(1, item.Quantity);
        var state = await ReadState(client, demo.Product.Id);
        Assert.Equal(0, state.StockQuantity);
        Assert.Equal(1L, state.OrderedQuantity);
        Assert.Equal(order.Id, Assert.Single(state.OrderIds));
    }

    [Fact]
    public async Task TwoClients_BothOrdersAreProcessed()
    {
        await using var factory = Factory();
        using var first = Client(factory, 1);
        using var second = Client(factory, 2);
        var demo1 = await Prepare(first);
        var demo2 = await Prepare(second);
        var a = CreateOrder(first, demo1);
        var b = CreateOrder(second, demo2);
        using var response1 = await a;
        using var response2 = await b;
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        var order1 = await response1.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        var order2 = await response2.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        Assert.NotNull(order1);
        Assert.NotNull(order2);
        Assert.NotEqual(order1.Id, order2.Id);
        foreach (var demo in new[] { demo1, demo2 })
        {
            var state = await ReadState(first, demo.Product.Id);
            Assert.Equal(0, state.StockQuantity);
            Assert.Equal(1L, state.OrderedQuantity);
            Assert.Single(state.OrderIds);
        }
    }

    [Fact]
    public async Task ParallelProcessing_SecondStartsBeforeFirstEnds()
    {
        using var logs = new CapturedLogs();
        await using var factory = Factory(requestDelay: 10, logs: logs);
        using var first = Client(factory, 1);
        using var second = Client(factory, 2);
        var a = first.PostAsJsonAsync("/api/operations/GetProducts", new { });
        var start1 = await logs.Wait("1", "START");
        var b = second.PostAsJsonAsync("/api/operations/GetProducts", new { });
        var start2 = await logs.Wait("2", "START");
        using var response1 = await a;
        using var response2 = await b;
        var end1 = await logs.Wait("1", "END");
        var end2 = await logs.Wait("2", "END");
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.True(start1.Time <= start2.Time && start2.Time < end1.Time);
        Assert.True(end1.Time - start1.Time >= TimeSpan.FromSeconds(9.5));
        Assert.True(end2.Time - start2.Time >= TimeSpan.FromSeconds(9.5));
        Assert.True(start1.TaskId > 0 && start2.TaskId > 0);
        Assert.NotEqual(start1.TaskId, start2.TaskId);
        Dump(logs);
    }

    [Fact]
    [Trait("Category", "RaceCondition")]
    public async Task RaceCondition_OneUnitProducesTwoAcceptedOrders()
    {
        using var logs = new CapturedLogs();
        await using var factory = Factory(raceDelay: 10, logs: logs);
        using var setup = Client(factory, 99);
        var demo = await Prepare(setup);
        using var first = Client(factory, 1);
        using var second = Client(factory, 2);
        var a = CreateOrder(first, demo, operation: "RaceCreateOrder");
        var check1 = await logs.Wait("1", "CHECK");
        var b = CreateOrder(second, demo, operation: "RaceCreateOrder");
        var check2 = await logs.Wait("2", "CHECK");
        using var response1 = await a;
        using var response2 = await b;
        var write1 = await logs.Wait("1", "WRITE");
        var write2 = await logs.Wait("2", "WRITE");
        Assert.True(check2.Time < write1.Time && check1.Time < write2.Time);
        Assert.Contains("Available=1", check1.Message);
        Assert.Contains("Available=1", check2.Message);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        var order1 = await response1.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        var order2 = await response2.Content.ReadFromJsonAsync<OrderResponse>(JsonOptions);
        Assert.NotNull(order1);
        Assert.NotNull(order2);
        Assert.NotEqual(order1.Id, order2.Id);
        Assert.Equal(1, Assert.Single(order1.Items).Quantity);
        Assert.Equal(1, Assert.Single(order2.Items).Quantity);
        var state = await ReadState(setup, demo.Product.Id);
        Assert.Equal(0, state.StockQuantity); // Both requests wrote their snapshot: 1 - 1 = 0.
        Assert.Equal(2L, state.OrderedQuantity); // Two orders accepted with only ONE initial unit.
        Assert.Equal(2, state.OrderIds.Count);
        Assert.Contains(order1.Id, state.OrderIds);
        Assert.Contains(order2.Id, state.OrderIds);
        output.WriteLine("Initial stock=1; accepted orders=2; ordered quantity=2; final stock=0.");
        Dump(logs);
    }

    [Fact]
    public async Task DisconnectDuringProcessing_ServerContinues()
    {
        using var logs = new CapturedLogs();
        await using var factory = Factory(requestDelay: 10, logs: logs);
        using var first = Client(factory, 1);
        using var cancellation = new CancellationTokenSource();
        var pending = first.PostAsJsonAsync("/api/operations/GetProducts", new { }, cancellation.Token);
        await logs.Wait("1", "START");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await pending; });
        await logs.Wait("1", "CANCELLED");
        await logs.Wait("1", "END");
        using var second = Client(factory, 2);
        using var healthy = await second.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    [Theory]
    [InlineData("RaceCreateOrder")]
    [InlineData("TransactionCreateOrder")]
    [InlineData("SyncCreateOrder")]
    [InlineData("SemaphoreCreateOrder")]
    [Trait("Category", "RaceCondition")]
    public async Task DisconnectDuringRaceDelay_DoesNotPersistOrderOrChangeStock(string operation)
    {
        using var logs = new CapturedLogs();
        await using var factory = Factory(raceDelay: 10, logs: logs);
        using var setup = Client(factory, 99);
        var demo = await Prepare(setup);
        using var client = Client(factory, 1);
        using var cancellation = new CancellationTokenSource();
        var pending = CreateOrder(client, demo, cancellation.Token, operation);
        await logs.Wait("1", "CHECK");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await pending; });
        await logs.Wait("1", "END");
        var state = await ReadState(setup, demo.Product.Id);
        Assert.Equal(1, state.StockQuantity);
        Assert.Equal(0L, state.OrderedQuantity);
        Assert.Empty(state.OrderIds);
        // A cancelled protected request must release its lock or semaphore.
        using var retry = await CreateOrder(setup, demo, operation: operation);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Theory]
    [InlineData("CreateOrder")]
    [InlineData("TransactionCreateOrder")]
    [InlineData("SyncCreateOrder")]
    [InlineData("SemaphoreCreateOrder")]
    [Trait("Category", "RaceCondition")]
    public async Task ProtectedOrders_OneUnitAcceptsOnlyOneOrder(string operation)
    {
        using var logs = new CapturedLogs();
        await using var factory = Factory(raceDelay: 2, logs: logs);
        using var setup = Client(factory, 99);
        var demo = await Prepare(setup);
        using var first = Client(factory, 1);
        using var second = Client(factory, 2);
        var a = CreateOrder(first, demo, operation: operation);
        // For the teaching variants, start Client 2 while Client 1 holds the gate.
        if (operation != "CreateOrder") await logs.Wait("1", "CHECK");
        var b = CreateOrder(second, demo, operation: operation);
        using var response1 = await a;
        using var response2 = await b;
        var statuses = new[] { response1.StatusCode, response2.StatusCode };
        Assert.Single(statuses.Where(x => x == HttpStatusCode.OK));
        Assert.Single(statuses.Where(x => x == HttpStatusCode.BadRequest));
        var state = await ReadState(setup, demo.Product.Id);
        Assert.Equal(0, state.StockQuantity);
        Assert.Equal(1L, state.OrderedQuantity);
        Assert.Single(state.OrderIds);

        // An insufficient-stock failure must also release the gate for the next product.
        var next = await Prepare(setup);
        using var retry = await CreateOrder(setup, next, operation: operation);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Dump(logs);
    }

    [Fact]
    public async Task InvalidRequest_Returns400AndServerContinues()
    {
        await using var factory = Factory();
        using var client = Client(factory, 1);
        using var malformed = await client.PostAsync("/api/operations/CreateOrder",
            new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var invalid = await client.PostAsJsonAsync("/api/operations/CreateOrder",
            new CreateOrderRequest(0, new List<OrderItemRequest> { new(1, 1) }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var unknown = await client.PostAsJsonAsync("/api/operations/Unknown", new { });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var healthy = await client.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    [Fact]
    public async Task MissingResource_Returns404AndServerContinues()
    {
        await using var factory = Factory();
        using var client = Client(factory, 1);
        var customer = await client.GetFromJsonAsync<BootstrapResponse>("/api/bootstrap");
        Assert.NotNull(customer);
        using var missing = await client.PostAsJsonAsync("/api/operations/CreateOrder",
            new CreateOrderRequest(customer.CustomerId,
                new List<OrderItemRequest> { new(int.MaxValue, 1) }));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var healthy = await client.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    [Fact]
    public async Task ProcessingFailure_Returns500AndNextRequestSucceeds()
    {
        await using var factory = Factory(enableFailureInjection: true);
        using var client = Client(factory, 1);
        using var failure = await client.PostAsJsonAsync("/api/lab/fail", new { });
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        using var healthy = await client.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    [Fact]
    public async Task FailureInjection_IsDisabledByDefault()
    {
        await using var factory = Factory();
        using var client = Client(factory, 1);
        using var response = await client.PostAsJsonAsync("/api/lab/fail", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DatabaseUnavailable_Returns503()
    {
        const string unavailable = "Host=127.0.0.1;Port=1;Database=unavailable;Username=invalid;Password=invalid;Timeout=1;Command Timeout=1";
        await using var factory = new InternetShopApiFactory(unavailable);
        using var client = Client(factory, 1);
        using var response = await client.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private InternetShopApiFactory Factory(int requestDelay = 0, int raceDelay = 0,
        ILoggerProvider? logs = null, bool enableFailureInjection = false) =>
        new(postgres.Container.GetConnectionString(), requestDelay, raceDelay, logs, enableFailureInjection);

    private static HttpClient Client(InternetShopApiFactory factory, int id)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", id.ToString());
        return client;
    }

    private static async Task<RaceDemoResponse> Prepare(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/lab/prepare-race", new { });
        response.EnsureSuccessStatusCode();
        var demo = await response.Content.ReadFromJsonAsync<RaceDemoResponse>();
        Assert.NotNull(demo);
        Assert.Equal(1, demo.Product.StockQuantity);
        return demo;
    }

    private static Task<HttpResponseMessage> CreateOrder(HttpClient client, RaceDemoResponse demo,
        CancellationToken ct = default, string operation = "CreateOrder") =>
        client.PostAsJsonAsync($"/api/operations/{operation}",
            new CreateOrderRequest(demo.CustomerId, new List<OrderItemRequest> { new(demo.Product.Id, 1) }), ct);

    private static async Task<RaceStateResponse> ReadState(HttpClient client, int productId)
    {
        var state = await client.GetFromJsonAsync<RaceStateResponse>($"/api/lab/products/{productId}/state");
        Assert.NotNull(state);
        return state;
    }

    private void Dump(CapturedLogs logs)
    {
        foreach (var entry in logs.Entries) output.WriteLine(entry.Message);
    }

    private sealed record Entry(string Client, string Phase, DateTimeOffset Time, int TaskId, string Message);

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Entries);
        public void Dispose() { }

        public async Task<Entry> Wait(string client, string phase)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (true)
            {
                var entry = Entries.FirstOrDefault(x => x.Client == client && x.Phase == phase);
                if (entry is not null) return entry;
                await Task.Delay(20, timeout.Token);
            }
        }

        private sealed class Capture(ConcurrentQueue<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
                var fields = values.ToDictionary(x => x.Key, x => x.Value);
                if (!fields.ContainsKey("Phase")) return;
                entries.Enqueue(new Entry((string)fields["ClientId"]!, (string)fields["Phase"]!,
                    (DateTimeOffset)fields["Timestamp"]!, (int)fields["TaskId"]!, formatter(state, exception)));
            }
        }
    }
}
