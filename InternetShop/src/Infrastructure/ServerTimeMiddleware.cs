using System.Globalization;

namespace InternetShop.Api.Infrastructure;

public sealed class ServerTimeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var received = DateTimeOffset.UtcNow;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Server-Received-Time"] = received.ToString("O", CultureInfo.InvariantCulture);
            context.Response.Headers["X-Server-Time"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        });
        await next(context);
    }
}
