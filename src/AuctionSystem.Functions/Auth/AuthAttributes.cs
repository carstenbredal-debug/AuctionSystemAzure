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

/// <summary>
/// Requires the x-section-password header to match the configured SECTION_PASSWORD, in addition to the
/// normal role check. Used to add an extra shared-password gate on top of Admin-role endpoints for
/// sensitive sections (Parameters, Diagnostics) that a subset of Admins shouldn't casually reach.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireSectionPasswordAttribute : Attribute { }
