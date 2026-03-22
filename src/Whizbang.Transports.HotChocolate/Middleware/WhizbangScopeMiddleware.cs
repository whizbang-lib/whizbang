using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Transports.HotChocolate.Middleware;

/// <summary>
/// ASP.NET Core middleware that extracts scope from HTTP context and sets it in the scope context accessor.
/// Supports extraction from JWT claims and custom headers.
/// </summary>
/// <docs>apis/graphql/scoping#middleware</docs>
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
public class WhizbangScopeMiddleware(RequestDelegate next, WhizbangScopeOptions? options = null) {
  private readonly RequestDelegate _next = next;
  private readonly WhizbangScopeOptions _options = options ?? new WhizbangScopeOptions();

  public async Task InvokeAsync(HttpContext context, IScopeContextAccessor scopeContextAccessor) {
    // Build scope from claims and headers
    var scope = _buildScope(context);
    var roles = _extractRoles(context);
    var permissions = _extractPermissions(context);
    var principals = _extractPrincipals(context);
    var claims = _extractClaims(context);

    // Create SecurityExtraction with all extracted data
    var extraction = new SecurityExtraction {
      Scope = scope,
      Roles = roles,
      Permissions = permissions,
      SecurityPrincipals = principals,
      Claims = claims,
      Source = "HttpContext",
      ActualPrincipal = scope.UserId,
      EffectivePrincipal = scope.UserId,
      ContextType = SecurityContextType.User
    };

    // Wrap in ImmutableScopeContext for Dispatcher compatibility
    scopeContextAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);

    await _next(context);
  }

  private PerspectiveScope _buildScope(HttpContext context) {
    var tenantId = _extractValue(context, _options.TenantIdClaimType, _options.TenantIdHeaderName);
    var userId = _extractValueWithFallback(context, _options.UserIdClaimTypes, _options.UserIdHeaderName);
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

  /// <summary>
  /// Extracts a value trying multiple claim types in order (for fallback scenarios).
  /// Tries each claim type until one is found, then falls back to header.
  /// </summary>
  private static string? _extractValueWithFallback(HttpContext context, IEnumerable<string> claimTypes, string headerName) {
    // Try each claim type in order
    foreach (var claimType in claimTypes) {
      var claimValue = context.User?.FindFirst(claimType)?.Value;
      if (!string.IsNullOrEmpty(claimValue)) {
        return claimValue;
      }
    }

    // Then try header
    if (context.Request.Headers.TryGetValue(headerName, out var headerValue) &&
        !string.IsNullOrEmpty(headerValue)) {
      return headerValue!;
    }

    return null;
  }

  private HashSet<string> _extractRoles(HttpContext context) {
    var rolesClaim = context.User?.FindAll(_options.RolesClaimType);
    return rolesClaim?
      .Select(claim => claim.Value)
      .Where(value => !string.IsNullOrEmpty(value))
      .ToHashSet() ?? [];
  }

  private HashSet<Permission> _extractPermissions(HttpContext context) {
    var permClaims = context.User?.FindAll(_options.PermissionsClaimType);
    return permClaims?
      .Select(claim => claim.Value)
      .Where(value => !string.IsNullOrEmpty(value))
      .Select(value => new Permission(value))
      .ToHashSet() ?? [];
  }

  private HashSet<SecurityPrincipalId> _extractPrincipals(HttpContext context) {
    var principals = new HashSet<SecurityPrincipalId>();

    // Add user principal - try all claim types in order
    string? userId = null;
    foreach (var claimType in _options.UserIdClaimTypes) {
      userId = context.User?.FindFirst(claimType)?.Value;
      if (!string.IsNullOrEmpty(userId)) {
        break;
      }
    }

    if (!string.IsNullOrEmpty(userId)) {
      principals.Add(SecurityPrincipalId.User(userId));
    }

    // Add group principals
    var groupClaims = context.User?.FindAll(_options.GroupsClaimType);
    if (groupClaims != null) {
      foreach (var value in groupClaims.Select(claim => claim.Value).Where(v => !string.IsNullOrEmpty(v))) {
        principals.Add(SecurityPrincipalId.Group(value));
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
/// Configuration options for scope extraction middleware.
/// Supports fallback claim types for common identity provider variations.
/// </summary>
/// <docs>apis/graphql/scoping#options</docs>
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
  /// Claim types for user ID, tried in order until one is found.
  /// Default: ["http://schemas.microsoft.com/identity/claims/objectidentifier", "objectid", "oid", "sub", ClaimTypes.NameIdentifier].
  /// Covers Azure AD (objectidentifier, oid), standard JWT (sub), and ASP.NET (NameIdentifier).
  /// </summary>
  public List<string> UserIdClaimTypes { get; set; } = [
    "http://schemas.microsoft.com/identity/claims/objectidentifier", // Azure AD full claim
    "objectid",  // Azure AD short form
    "oid",       // Azure AD abbreviated
    "sub",       // Standard JWT
    ClaimTypes.NameIdentifier  // ASP.NET Identity
  ];

  /// <summary>
  /// Primary claim type for user ID. Gets the first claim type in <see cref="UserIdClaimTypes"/>.
  /// Setting this replaces all claim types with a single value (for backwards compatibility).
  /// </summary>
  public string UserIdClaimType {
    get => UserIdClaimTypes.FirstOrDefault() ?? ClaimTypes.NameIdentifier;
    set => UserIdClaimTypes = [value];
  }

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
