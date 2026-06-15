using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Auth;

/// <summary>
/// Catches unhandled exceptions from HTTP functions, logs the full error, and returns a 500
/// whose body carries the root-cause message (e.g. a SQL "Invalid column name" error) so the
/// client/UI can surface it instead of an opaque 500. Registered outermost so it wraps auth too.
/// </summary>
public sealed class ExceptionHandlingMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(ILogger<ExceptionHandlingMiddleware> logger) => _logger = logger;

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in {Function}", context.FunctionDefinition.Name);

            var http = context.GetHttpContext();
            if (http is null || http.Response.HasStarted)
                throw; // non-HTTP trigger, or response already begun — let the host handle it.

            var message = ex.GetBaseException().Message;
            if (message.Length > 500) message = message[..500];

            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsJsonAsync(new { error = message });
        }
    }
}
