using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using InternetShop.Api.Exceptions;

namespace InternetShop.Api.Infrastructure;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            IncorrectOperationException => (StatusCodes.Status400BadRequest, "Incorrect operation"),
            IncorrectInputDataException => (StatusCodes.Status400BadRequest, "Incorrect input data"),
            ObjectMissingException => (StatusCodes.Status404NotFound, "Object not found"),
            DbUpdateException update when IsConnectionFailure(update)
                => (StatusCodes.Status503ServiceUnavailable, "Database unavailable"),
            DbUpdateException => (StatusCodes.Status409Conflict, "Database conflict"),
            NpgsqlException => (StatusCodes.Status503ServiceUnavailable, "Database unavailable"),
            _ => (StatusCodes.Status500InternalServerError, "Internal server error")
        };

        if (status >= 500)
            logger.LogError(exception, "Request failed with status {StatusCode}", status);
        else
            logger.LogWarning(exception, "Request rejected with status {StatusCode}", status);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status >= 500
                ? "The server could not complete the request. See the server log."
                : exception.Message,
            Instance = httpContext.Request.Path
        };

        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }

    private static bool IsConnectionFailure(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
            if (current is NpgsqlException and not PostgresException ||
                current is System.Net.Sockets.SocketException)
                return true;
        return false;
    }
}
