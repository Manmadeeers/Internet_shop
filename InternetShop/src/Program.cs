using System.Text.Json.Serialization;
using InternetShop.Api.Data;
using InternetShop.Api.Infrastructure;
using InternetShop.Api.Services;
using Microsoft.EntityFrameworkCore;
using InternetShop.Api.Infrastructure.Replication;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
        .AddJsonFile(
            "appsettings.Replication.json",
            optional: false,
            reloadOnChange: false)
        .AddEnvironmentVariables()
        .AddCommandLine(args);

builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddScoped<RequestExecution>();
builder.Services.AddOptions<LabOptions>()
    .Bind(builder.Configuration.GetSection("Lab"))
    .Validate(x => x.RequestDelaySeconds is >= 0 and <= 60 &&
                   x.RaceDelaySeconds is >= 0 and <= 60,
        "Lab delays must be between 0 and 60 seconds.")
    .ValidateOnStart();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("ShopClient", policy => policy
        .WithOrigins("http://localhost:5500", "http://127.0.0.1:5500")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders("X-Client-Id", "X-Request-Id", "X-Server-Time", "X-Server-Received-Time"));
});

builder.Services.AddDbContext<ShopDbContext>((services, options) =>
{
    var coordinator =
        services.GetRequiredService<DemoReplicationCoordinator>();

    var selection =
        services.GetRequiredService<DatabaseSelection>();

    if (coordinator.Enabled && selection.ConnectionString is null)
    {
        throw new InvalidOperationException(
            "ShopDbContext must be resolved after database selection.");
    }

    var connectionString =
        selection.ConnectionString ??
        builder.Configuration.GetConnectionString("InternetShopDb") ??
        throw new InvalidOperationException(
            "ConnectionStrings:InternetShopDb is required.");

    options.UseNpgsql(connectionString);
});
builder.Services.AddScoped<ShopService>();
builder.Services.AddScoped<RaceOrderService>();
builder.Services.AddScoped<SyncOrderService>();
builder.Services.AddScoped<SemaphoreOrderService>();
builder.Services.AddScoped<DomainEventLogger>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services
    .AddOptions<DemoReplicationOptions>()
    .Bind(builder.Configuration.GetSection("DemoReplication"))
    .Validate(
        options =>
            options.PollSeconds > 0 &&
            options.ProbeTimeoutSeconds > 0 &&
            options.SyncTimeoutSeconds > 0 &&
            !string.IsNullOrWhiteSpace(options.StateFile),
        "Replication intervals and state-file path must be valid.")
    .ValidateOnStart();

builder.Services.AddScoped<DatabaseSelection>();

builder.Services.AddSingleton<DatabaseSnapshotCopier>();
builder.Services.AddSingleton<DemoReplicationCoordinator>();

builder.Services.AddHostedService(
    services =>
        services.GetRequiredService<DemoReplicationCoordinator>());

var app = builder.Build();
app.UseMiddleware<ServerTimeMiddleware>();
app.UseMiddleware<ClientTaskMiddleware>();
app.UseExceptionHandler();
app.UseCors("ShopClient");
app.UseMiddleware<DemoReplicationMiddleware>();

app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();
app.MapGet("/health", async (ShopDbContext db, CancellationToken ct) =>
{
    try
    {
        return await db.Database.CanConnectAsync(ct)
            ? Results.Ok(new { status = "Healthy", database = "Connected" })
            : Results.Json(new { status = "Unhealthy", database = "Disconnected" }, statusCode: 503);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        return Results.Json(new { status = "Unhealthy", database = "Unavailable" }, statusCode: 503);
    }
});

app.MapGet(
    "/api/replication/status",
    (DemoReplicationCoordinator coordinator) =>
        Results.Ok(coordinator.GetStatus()));

app.Run();

public partial class Program { }
