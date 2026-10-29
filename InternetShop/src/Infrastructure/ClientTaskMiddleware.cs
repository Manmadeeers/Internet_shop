using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace InternetShop.Api.Infrastructure;

public sealed class LabOptions
{
    public bool Enabled { get; set; } = true;
    public int RequestDelaySeconds { get; set; }
    public int RaceDelaySeconds { get; set; } = 10;
    public bool EnableFailureInjection { get; set; }
}

public sealed class RequestExecution
{
    public string ClientId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string RequestId { get; set; } = "";
    public int TaskId { get; set; }

    public void Write(ILogger logger, string phase, string detail = "") =>
        logger.LogInformation(
            "[{Timestamp:HH:mm:ss.fff}] Client {ClientId} -> {Phase} " +
            "Operation={Operation} Task={TaskId} Thread={ThreadId} Request={RequestId} {Detail}",
            DateTimeOffset.Now, ClientId, phase, Operation, TaskId,
            Environment.CurrentManagedThreadId, RequestId, detail);
}

public sealed class ClientTaskMiddleware(
    RequestDelegate next,
    ILogger<ClientTaskMiddleware> logger,
    IOptions<LabOptions> options)
{
    public async Task InvokeAsync(HttpContext context, RequestExecution execution)
    {
        var id = context.Request.Headers["X-Client-Id"].ToString();
        execution.ClientId = id.Length is > 0 and <= 64 &&
            id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? id : context.Connection.Id;
        execution.Operation = $"{context.Request.Method} {context.Request.Path}";
        execution.RequestId = context.TraceIdentifier;
        context.Response.Headers["X-Client-Id"] = execution.ClientId;
        context.Response.Headers["X-Request-Id"] = execution.RequestId;

        await Task.Run(async () =>
        {
            execution.TaskId = Task.CurrentId ?? -1;
            execution.Write(logger, "START");
            try
            {
                context.RequestAborted.ThrowIfCancellationRequested();
                if (HttpMethods.IsPost(context.Request.Method) &&
                    context.Request.Path.Equals("/api/operations/CreateOrder") || context.Request.Path.Equals("/api/operations/RaceCreateOrder") || context.Request.Path.Equals("/api/operations/SyncCreateOrder") || context.Request.Path.Equals("/api/operations/SemaphoreCreateOrder"))
                    await Task.Delay(TimeSpan.FromSeconds(options.Value.RaceDelaySeconds),
                        context.RequestAborted);
                await next(context);
            }
            catch (Exception ex) when (context.RequestAborted.IsCancellationRequested &&
                ex is OperationCanceledException or IOException or SocketException)
            {
                execution.Write(logger, "DISCONNECTED");
                if (!context.Response.HasStarted) context.Response.StatusCode = 499;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled request failure for client {ClientId}", execution.ClientId);
                if (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        title = "Internal server error",
                        status = 500,
                        traceId = context.TraceIdentifier
                    }, context.RequestAborted);
                }
                else if (context.Response.HasStarted)
                {
                    context.Abort();
                }
            }
            finally
            {
                if (context.RequestAborted.IsCancellationRequested)
                    execution.Write(logger, "CANCELLED");
                execution.Write(logger, "END", $"Status={context.Response.StatusCode}");
            }
        });
    }
}
