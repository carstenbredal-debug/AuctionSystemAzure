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
/// Requires the x-section-password-{section} header to match the configured SECTION_PASSWORD_{SECTION}
/// app setting, in addition to the normal role check. Each section (e.g. "Shipping", "Parameters",
/// "Diagnostics") has its OWN password + header, so they can be shared and rotated independently.
/// Fail-closed: an unset setting locks that section's endpoints for everyone.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireSectionPasswordAttribute : Attribute
{
    public string Section { get; }
    public RequireSectionPasswordAttribute(string section) => Section = section;
}
