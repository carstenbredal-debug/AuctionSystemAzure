namespace AuctionSystem.Functions.Auth;

/// <summary>Marks a function as callable without authentication (e.g. health probes).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AllowAnonymousAttribute : Attribute { }

/// <summary>
/// Requires the caller's AppUser.Role to be one of the listed roles (case-insensitive).
/// Applied on top of the default authentication requirement.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireRoleAttribute : Attribute
{
    public string[] Roles { get; }
    public RequireRoleAttribute(params string[] roles) => Roles = roles;
}
