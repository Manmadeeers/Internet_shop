using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using InternetShop.MessageBroker;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 1_048_576);

builder.Services.AddSingleton<BrokerStore>();
builder.Services.AddHostedService<BrokerMaintenanceWorker>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        var store = context.RequestServices
            .GetRequiredService<BrokerStore>();

        if (!store.Ready && context.Request.Path != "/health")
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(
                new { error = "Broker is initializing. Retry shortly." },
                context.RequestAborted);
            return;
        }

        await next(context);
    }
    catch (BrokerHttpException exception)
    {
        context.Response.StatusCode = exception.StatusCode;
        await context.Response.WriteAsJsonAsync(
            new { error = exception.Message },
            context.RequestAborted);
    }
    catch (BadHttpRequestException exception)
    {
        context.Response.StatusCode = exception.StatusCode;
        await context.Response.WriteAsJsonAsync(
            new { error = exception.Message },
            context.RequestAborted);
    }
    catch (OperationCanceledException)
        when (context.RequestAborted.IsCancellationRequested)
    {
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Broker request failed.");
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(
            new
            {
                error = "Broker operation failed. Retry the request. " +
                        "When publishing, reuse the same EventId."
            },
            context.RequestAborted);
    }
});

app.MapGet("/health", async Task<IResult> (
    BrokerStore store,
    CancellationToken ct) =>
{
    if (!store.Ready)
    {
        return Results.Json(
            new { status = "initializing" },
            statusCode: 503);
    }

    await store.PingAsync(ct);
    return Results.Ok(new { status = "ready" });
});

app.MapPost("/api/events", async Task<IResult> (
    PublishRequest request,
    BrokerStore store,
    CancellationToken ct) =>
{
    ValidatePublication(request);
    return Results.Ok(await store.PublishAsync(request, ct));
});

app.MapPost("/api/queues/{queue}/receive", async Task<IResult> (
    string queue,
    ReceiveRequest request,
    BrokerStore store,
    CancellationToken ct) =>
{
    if (request.WaitSeconds is < 0 or > 20)
    {
        throw new BrokerHttpException(
            400,
            "WaitSeconds must be between 0 and 20.");
    }

    if (request.LeaseSeconds is < 5 or > 300)
    {
        throw new BrokerHttpException(
            400,
            "LeaseSeconds must be between 5 and 300.");
    }

    await store.EnsureQueueAsync(queue, ct);
    var stopwatch = Stopwatch.StartNew();

    while (true)
    {
        var delivery = await store.ReceiveAsync(
            queue,
            request.LeaseSeconds,
            ct);

        if (delivery is not null)
        {
            return Results.Ok(delivery);
        }

        var remaining =
            TimeSpan.FromSeconds(request.WaitSeconds) - stopwatch.Elapsed;

        if (remaining <= TimeSpan.Zero)
        {
            return Results.NoContent();
        }

        await Task.Delay(
            remaining < TimeSpan.FromMilliseconds(250)
                ? remaining
                : TimeSpan.FromMilliseconds(250),
            ct);
    }
});

app.MapPost("/api/queues/{queue}/ack", async Task<IResult> (
    string queue,
    ReceiptRequest request,
    BrokerStore store,
    CancellationToken ct) =>
{
    await store.EnsureQueueAsync(queue, ct);
    var accepted = await store.AckAsync(queue, request.Receipt, ct);
    return accepted
        ? Results.NoContent()
        : Results.Conflict(
            new { error = "Receipt is invalid or its lease expired." });
});

app.MapPost("/api/queues/{queue}/reject", async Task<IResult> (
    string queue,
    ReceiptRequest request,
    BrokerStore store,
    CancellationToken ct) =>
{
    await store.EnsureQueueAsync(queue, ct);
    var error = request.Error ?? "Subscriber rejected the delivery.";

    if (error.Length > 1000)
    {
        error = error[..1000];
    }

    var accepted = await store.RejectAsync(
        queue,
        request.Receipt,
        error,
        ct);

    return accepted
        ? Results.NoContent()
        : Results.Conflict(
            new { error = "Receipt is invalid or its lease expired." });
});

app.MapGet("/api/queues", (
    BrokerStore store,
    CancellationToken ct) => store.StatisticsAsync(ct));

app.MapGet("/api/queues/{queue}/deadletters", async (
    string queue,
    BrokerStore store,
    CancellationToken ct) =>
{
    await store.EnsureQueueAsync(queue, ct);
    return await store.DeadLettersAsync(queue, ct);
});

app.Run();

static void ValidatePublication(PublishRequest request)
{
    if (string.IsNullOrWhiteSpace(request.RoutingKey)
        || request.RoutingKey.Length > 200
        || !Regex.IsMatch(
            request.RoutingKey,
            @"^internetshop(?:\.[a-z0-9_-]+)+$"))
    {
        throw new BrokerHttpException(
            400,
            "RoutingKey must contain internetshop.* topic words.");
    }

    var envelope = request.Envelope;

    if (envelope is null
        || envelope.EventId == Guid.Empty
        || string.IsNullOrWhiteSpace(envelope.EventType)
        || envelope.EventType.Length > 100
        || envelope.Version < 1
        || envelope.OccurredAtUtc == default
        || envelope.Data.ValueKind != JsonValueKind.Object)
    {
        throw new BrokerHttpException(
            400,
            "A valid event envelope with an object payload is required.");
    }
}
