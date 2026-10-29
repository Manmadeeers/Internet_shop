namespace InternetShop.Api.Infrastructure.Replication;

public sealed class DemoReplicationMiddleware(
    RequestDelegate next,
    DemoReplicationCoordinator coordinator)
{
    public async Task InvokeAsync(
        HttpContext context,
        DatabaseSelection selection)
    {
        if (!coordinator.Enabled || !UsesDatabase(context.Request.Path))
        {
            await next(context);
            return;
        }

        var mayWrite =
            !HttpMethods.IsGet(context.Request.Method) &&
            !HttpMethods.IsHead(context.Request.Method) &&
            !HttpMethods.IsOptions(context.Request.Method);

        var lease = await coordinator.EnterRequestAsync(
            selection,
            mayWrite,
            context.RequestAborted);

        if (lease is null)
        {
            context.Response.StatusCode =
                StatusCodes.Status503ServiceUnavailable;

            await context.Response.WriteAsJsonAsync(new
            {
                title = "Database unavailable",
                status = StatusCodes.Status503ServiceUnavailable,
                detail = "No safe active database is currently available.",
                replication = coordinator.GetStatus()
            }, context.RequestAborted);

            return;
        }

        var originalBody = context.Response.Body;

        using var buffer = new MemoryStream();

        context.Response.Body = buffer;
        context.Response.Headers["X-Database-Node"] = selection.Node;

        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;

            await lease.DisposeAsync();

            if (mayWrite)
            {
                await coordinator.ReconcileAsync(CancellationToken.None);
            }
        }

        buffer.Position = 0;

        await buffer.CopyToAsync(
            originalBody,
            context.RequestAborted);
    }

    private static bool UsesDatabase(PathString path)
    {
        if (path.StartsWithSegments("/api/replication") ||
            path.StartsWithSegments("/api/time"))
        {
            return false;
        }

        return path.StartsWithSegments("/api") ||
               path.Equals(new PathString("/health"));
    }
}