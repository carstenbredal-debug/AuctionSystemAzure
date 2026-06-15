using System.Collections.Concurrent;
using System.Reflection;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Auth;

/// <summary>
/// Enforces authentication and role authorization for HTTP-triggered functions.
///
/// Default policy: a valid x-ms-client-principal (injected by the SWA linked backend /
/// App Service auth) is required. [AllowAnonymous] opts a function out; [RequireRole(...)]
/// additionally requires the caller's AppUser.Role. The resolved principal and AppUser are
/// stored on the FunctionContext (see FunctionContextAuthExtensions) for handlers to use
/// for "me" lookups and ownership checks.
///
/// SECURITY: this trusts the x-ms-client-principal header. It is only safe when the Function
/// App is reachable solely via the SWA linked backend (or App Service auth) so the platform
/// validates and injects the header. The Function App must NOT be directly internet-exposed.
/// </summary>
public sealed class AuthenticationMiddleware : IFunctionsWorkerMiddleware
{
    internal const string AppUserKey = "AuthAppUser";
    internal const string PrincipalKey = "AuthClientPrincipal";

    private static readonly ConcurrentDictionary<string, (bool Anonymous, string[] Roles)> Cache = new();

    private readonly bool _enforce;
    private readonly ILogger<AuthenticationMiddleware> _logger;

    public AuthenticationMiddleware(IConfiguration config, ILogger<AuthenticationMiddleware> logger)
    {
        // Set AUTH_ENFORCE=false to run in audit mode (log would-be denials, allow through)
        // during the SWA-linked-backend rollout. Default (unset) enforces.
        var flag = config["AUTH_ENFORCE"] ?? config["Values:AUTH_ENFORCE"];
        _enforce = !string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase);
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var http = context.GetHttpContext();
        if (http is null)
        {
            // Non-HTTP trigger (e.g. timer) — nothing to authenticate.
            await next(context);
            return;
        }

        var (anonymous, roles) = ResolveRequirements(context);
        if (anonymous)
        {
            await next(context);
            return;
        }

        var principal = ClientPrincipal.FromHeader(http.Request.Headers["x-ms-client-principal"]);
        AppUser? user = null;
        if (!string.IsNullOrEmpty(principal?.ObjectId))
        {
            var db = context.InstanceServices.GetRequiredService<AuctionDbContext>();
            var oid = principal.ObjectId;
            var email = principal.Email;
            user = await db.AppUsers.AsNoTracking()
                .FirstOrDefaultAsync(u => u.IsActive && (u.AzureAdObjectId == oid || (email != null && u.Email == email)));

            context.Items[PrincipalKey] = principal;
            if (user is not null)
                context.Items[AppUserKey] = user;
        }

        // Decide the verdict.
        int? denyStatus = null;
        string denyMessage = "";
        if (principal is null || string.IsNullOrEmpty(principal.ObjectId))
        {
            denyStatus = StatusCodes.Status401Unauthorized;
            denyMessage = "Authentication required.";
        }
        else if (roles.Length > 0 && (user is null || !roles.Contains(user.Role, StringComparer.OrdinalIgnoreCase)))
        {
            denyStatus = StatusCodes.Status403Forbidden;
            denyMessage = "Insufficient permissions.";
        }

        if (denyStatus is int status)
        {
            if (_enforce)
            {
                await WriteError(http, status, denyMessage);
                return;
            }
            _logger.LogWarning("Auth (audit mode) would block {Function} with {Status}: {Message}",
                context.FunctionDefinition.Name, status, denyMessage);
        }

        await next(context);
    }

    private static (bool Anonymous, string[] Roles) ResolveRequirements(FunctionContext context) =>
        Cache.GetOrAdd(context.FunctionDefinition.Name, _ =>
        {
            var method = ResolveMethod(context.FunctionDefinition.EntryPoint);
            if (method is null)
                return (false, Array.Empty<string>());
            var anon = method.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
            var roleAttr = method.GetCustomAttribute<RequireRoleAttribute>();
            return (anon, roleAttr?.Roles ?? Array.Empty<string>());
        });

    private static MethodInfo? ResolveMethod(string entryPoint)
    {
        var lastDot = entryPoint.LastIndexOf('.');
        if (lastDot < 0) return null;
        var typeName = entryPoint[..lastDot];
        var methodName = entryPoint[(lastDot + 1)..];
        var type = typeof(AuthenticationMiddleware).Assembly.GetType(typeName);
        return type?.GetMethod(methodName);
    }

    private static async Task WriteError(HttpContext http, int status, string message)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync($"{{\"error\":\"{message}\"}}");
    }
}

/// <summary>Accessors for the authenticated identity stored by the middleware.</summary>
public static class FunctionContextAuthExtensions
{
    public static AppUser? GetAppUser(this FunctionContext context) =>
        context.Items.TryGetValue(AuthenticationMiddleware.AppUserKey, out var u) ? u as AppUser : null;

    public static ClientPrincipal? GetClientPrincipal(this FunctionContext context) =>
        context.Items.TryGetValue(AuthenticationMiddleware.PrincipalKey, out var p) ? p as ClientPrincipal : null;
}
