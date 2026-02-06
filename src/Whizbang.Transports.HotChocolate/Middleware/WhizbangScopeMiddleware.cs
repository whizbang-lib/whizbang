using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Transports.HotChocolate.Middleware;

/// <summary>
/// ASP.NET Core middleware that extracts scope from HTTP context and sets it in the scope context accessor.
/// Supports extraction from JWT claims and custom headers.
/// </summary>
/// <docs>v0.1.0/graphql/scoping#middleware</docs>
/// <tests>Whizbang.Transports.HotChocolate.Tests/Integration/ScopedQueryTests.cs</tests>
/// <example>
/// // In Program.cs or Startup.cs
/// app.UseWhizbangScope();
/// app.MapGraphQL();
///
/// // Or with custom options
/// app.UseWhizbangScope(options => {
///     options.TenantIdClaimType = "tenant_id";
///     options.TenantIdHeaderName = "X-Tenant-Id";
/// });
/// </example>
public class WhizbangScopeMiddleware {
  private readonly RequestDelegate _next;
  private readonly WhizbangScopeOptions _options;

  public WhizbangScopeMiddleware(RequestDelegate next, WhizbangScopeOptions? options = null) {
    _next = next;
    _options = options ?? new WhizbangScopeOptions();
  }

  public async Task InvokeAsync(HttpContext context, IScopeContextAccessor scopeContextAccessor) {
    // Build scope from claims and headers
    var scope = _buildScope(context);
    var roles = _extractRoles(context);
    var permissions = _extractPermissions(context);
    var principals = _extractPrincipals(context);
    var claims = _extractClaims(context);

    // Set the scope context for this request
    scopeContextAccessor.Current = new RequestScopeContext {
      Scope = scope,
      Roles = roles,
      Permissions = permissions,
      SecurityPrincipals = principals,
      Claims = claims
    };

    await _next(context);
  }

  private PerspectiveScope _buildScope(HttpContext context) {
    var tenantId = _extractValue(context, _options.TenantIdClaimType, _options.TenantIdHeaderName);
    var userId = _extractValue(context, _options.UserIdClaimType, _options.UserIdHeaderName);
    var orgId = _extractValue(context, _options.OrganizationIdClaimType, _options.OrganizationIdHeaderName);
    var customerId = _extractValue(context, _options.CustomerIdClaimType, _options.CustomerIdHeaderName);

    var extensions = new List<ScopeExtension>();
    foreach (var (claimType, extensionKey) in _options.ExtensionClaimMappings) {
      var value = context.User?.FindFirst(claimType)?.Value;
      if (!string.IsNullOrEmpty(value)) {
        extensions.Add(new ScopeExtension { Key = extensionKey, Value = value });
      }
    }

    foreach (var (headerName, extensionKey) in _options.ExtensionHeaderMappings) {
      if (context.Request.Headers.TryGetValue(headerName, out var headerValue) &&
          !string.IsNullOrEmpty(headerValue)) {
        extensions.Add(new ScopeExtension { Key = extensionKey, Value = headerValue! });
      }
    }

    return new PerspectiveScope {
      TenantId = tenantId,
      UserId = userId,
      OrganizationId = orgId,
      CustomerId = customerId,
      Extensions = extensions
    };
  }

  private static string? _extractValue(HttpContext context, string claimType, string headerName) {
    // First try claims
    var claimValue = context.User?.FindFirst(claimType)?.Value;
    if (!string.IsNullOrEmpty(claimValue)) {
      return claimValue;
    }

    // Then try headers
    if (context.Request.Headers.TryGetValue(headerName, out var headerValue) &&
        !string.IsNullOrEmpty(headerValue)) {
      return headerValue!;
    }

    return null;
  }

  private HashSet<string> _extractRoles(HttpContext context) {
    var roles = new HashSet<string>();

    var rolesClaim = context.User?.FindAll(_options.RolesClaimType);
    if (rolesClaim != null) {
      foreach (var claim in rolesClaim) {
        if (!string.IsNullOrEmpty(claim.Value)) {
          roles.Add(claim.Value);
        }
      }
    }

    return roles;
  }

  private HashSet<Permission> _extractPermissions(HttpContext context) {
    var permissions = new HashSet<Permission>();

    var permClaims = context.User?.FindAll(_options.PermissionsClaimType);
    if (permClaims != null) {
      foreach (var claim in permClaims) {
        if (!string.IsNullOrEmpty(claim.Value)) {
          // Permission has an implicit conversion from string
          permissions.Add(new Permission(claim.Value));
        }
      }
    }

    return permissions;
  }

  private HashSet<SecurityPrincipalId> _extractPrincipals(HttpContext context) {
    var principals = new HashSet<SecurityPrincipalId>();

    // Add user principal
    var userId = context.User?.FindFirst(_options.UserIdClaimType)?.Value;
    if (!string.IsNullOrEmpty(userId)) {
      principals.Add(SecurityPrincipalId.User(userId));
    }

    // Add group principals
    var groupClaims = context.User?.FindAll(_options.GroupsClaimType);
    if (groupClaims != null) {
      foreach (var claim in groupClaims) {
        if (!string.IsNullOrEmpty(claim.Value)) {
          principals.Add(SecurityPrincipalId.Group(claim.Value));
        }
      }
    }

    return principals;
  }

  private static Dictionary<string, string> _extractClaims(HttpContext context) {
    var claims = new Dictionary<string, string>();

    if (context.User?.Claims != null) {
      foreach (var claim in context.User.Claims) {
        // Use first value for each claim type
        claims.TryAdd(claim.Type, claim.Value);
      }
    }

    return claims;
  }
}

/// <summary>
/// Scope context implementation for HTTP requests.
/// </summary>
internal sealed class RequestScopeContext : IScopeContext {
  public required PerspectiveScope Scope { get; init; }
  public required IReadOnlySet<string> Roles { get; init; }
  public required IReadOnlySet<Permission> Permissions { get; init; }
  public required IReadOnlySet<SecurityPrincipalId> SecurityPrincipals { get; init; }
  public required IReadOnlyDictionary<string, string> Claims { get; init; }

  public bool HasPermission(Permission permission) {
    foreach (var p in Permissions) {
      if (p.Matches(permission)) {
        return true;
      }
    }
    return false;
  }

  public bool HasAnyPermission(params Permission[] permissions) =>
      permissions.Any(HasPermission);

  public bool HasAllPermissions(params Permission[] permissions) =>
      permissions.All(HasPermission);

  public bool HasRole(string roleName) => Roles.Contains(roleName);

  public bool HasAnyRole(params string[] roleNames) =>
      roleNames.Any(r => Roles.Contains(r));

  public bool IsMemberOfAny(params SecurityPrincipalId[] principals) =>
      principals.Any(p => SecurityPrincipals.Contains(p));

  public bool IsMemberOfAll(params SecurityPrincipalId[] principals) =>
      principals.All(p => SecurityPrincipals.Contains(p));
}

/// <summary>
/// Configuration options for scope extraction middleware.
/// </summary>
/// <docs>v0.1.0/graphql/scoping#options</docs>
/// <example>
/// services.Configure&lt;WhizbangScopeOptions&gt;(options => {
///     options.TenantIdClaimType = "tenant_id";
///     options.TenantIdHeaderName = "X-Tenant-Id";
///     options.ExtensionClaimMappings["region"] = "Region";
/// });
/// </example>
public class WhizbangScopeOptions {
  /// <summary>
  /// Claim type for tenant ID. Default: "tenant_id".
  /// </summary>
  public string TenantIdClaimType { get; set; } = "tenant_id";

  /// <summary>
  /// Header name for tenant ID. Default: "X-Tenant-Id".
  /// </summary>
  public string TenantIdHeaderName { get; set; } = "X-Tenant-Id";

  /// <summary>
  /// Claim type for user ID. Default: ClaimTypes.NameIdentifier.
  /// </summary>
  public string UserIdClaimType { get; set; } = ClaimTypes.NameIdentifier;

  /// <summary>
  /// Header name for user ID. Default: "X-User-Id".
  /// </summary>
  public string UserIdHeaderName { get; set; } = "X-User-Id";

  /// <summary>
  /// Claim type for organization ID. Default: "org_id".
  /// </summary>
  public string OrganizationIdClaimType { get; set; } = "org_id";

  /// <summary>
  /// Header name for organization ID. Default: "X-Organization-Id".
  /// </summary>
  public string OrganizationIdHeaderName { get; set; } = "X-Organization-Id";

  /// <summary>
  /// Claim type for customer ID. Default: "customer_id".
  /// </summary>
  public string CustomerIdClaimType { get; set; } = "customer_id";

  /// <summary>
  /// Header name for customer ID. Default: "X-Customer-Id".
  /// </summary>
  public string CustomerIdHeaderName { get; set; } = "X-Customer-Id";

  /// <summary>
  /// Claim type for roles. Default: ClaimTypes.Role.
  /// </summary>
  public string RolesClaimType { get; set; } = ClaimTypes.Role;

  /// <summary>
  /// Claim type for permissions. Default: "permissions".
  /// </summary>
  public string PermissionsClaimType { get; set; } = "permissions";

  /// <summary>
  /// Claim type for groups. Default: "groups".
  /// </summary>
  public string GroupsClaimType { get; set; } = "groups";

  /// <summary>
  /// Custom claim type to extension key mappings.
  /// </summary>
  public Dictionary<string, string> ExtensionClaimMappings { get; } = [];

  /// <summary>
  /// Custom header name to extension key mappings.
  /// </summary>
  public Dictionary<string, string> ExtensionHeaderMappings { get; } = [];
}
