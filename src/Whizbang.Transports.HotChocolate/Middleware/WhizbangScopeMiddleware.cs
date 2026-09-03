using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Transports.HotChocolate.Middleware;

/// <summary>
/// ASP.NET Core middleware that extracts scope from HTTP context and sets it in the scope context accessor.
/// Supports extraction from JWT claims and custom headers.
/// </summary>
/// <docs>apis/graphql/scoping#middleware</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Integration.Tests/ScopedQueryTests.cs</tests>
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

    // Adopt an inbound X-Correlation-ID (the client's token) into the ambient accessor so the HotChocolate
    // resolver's dispatch uses it instead of minting a fresh trace-aligned root. This is seeded HERE — in the
    // scope seam — because this is the middleware whose ambient state provably reaches the resolver; a separate
    // outermost correlation middleware's AsyncLocal does not survive to the GraphQL execution.
    _seedInboundCorrelation(context);

    await _next(context);
  }

  private void _seedInboundCorrelation(HttpContext context) {
    if (context.Request.Headers.TryGetValue(_options.CorrelationIdHeaderName, out var header) &&
        !string.IsNullOrEmpty(header) &&
        Guid.TryParse(header!, out var correlationGuid)) {
      // FromExternal accepts any 128-bit token (a client v4 UUID, a W3C trace-id) without UUIDv7 validation —
      // a correlation id can originate outside the system.
      InboundCorrelationAccessor.Current = CorrelationId.FromExternal(correlationGuid);
    }
  }

  private PerspectiveScope _buildScope(HttpContext context) {
    var tenantId = _extractValueWithFallback(context, _options.TenantIdClaimTypes, _options.TenantIdHeaderName);
    var userId = _extractValueWithFallback(context, _options.UserIdClaimTypes, _options.UserIdHeaderName);
    var orgId = _extractValueWithFallback(context, _options.OrganizationIdClaimTypes, _options.OrganizationIdHeaderName);
    var customerId = _extractValueWithFallback(context, _options.CustomerIdClaimTypes, _options.CustomerIdHeaderName);

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

  private HashSet<Permission> _extractPermissions(HttpContext context) =>
    [.. _extractMultiValuedClaim(
      context, _options.PermissionsClaimTypes, _options.PermissionsAggregation)
        .Select(value => new Permission(value))];

  /// <summary>
  /// Extracts a multi-valued claim across one or more configured claim names. Honors the
  /// configured <see cref="ClaimAggregation"/> strategy: <c>FirstMatch</c> (first claim
  /// name that yields any values wins), or <c>Aggregate</c> (union across all configured
  /// claim names, deduplicated).
  /// </summary>
  private static IEnumerable<string> _extractMultiValuedClaim(
      HttpContext context, List<string> claimTypes, ClaimAggregation aggregation) {
    if (context.User is null || claimTypes.Count == 0) {
      yield break;
    }

    if (aggregation == ClaimAggregation.FirstMatch) {
      foreach (var claimType in claimTypes) {
        var values = context.User.FindAll(claimType)
          .Select(c => c.Value)
          .Where(v => !string.IsNullOrEmpty(v))
          .ToList();
        if (values.Count > 0) {
          foreach (var v in values) {
            yield return v;
          }
          yield break;
        }
      }
      yield break;
    }

    // Aggregate: union across all claim types, deduplicated.
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var claimType in claimTypes) {
      foreach (var claim in context.User.FindAll(claimType)) {
        if (!string.IsNullOrEmpty(claim.Value) && seen.Add(claim.Value)) {
          yield return claim.Value;
        }
      }
    }
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

    // Add group principals (multi-valued, honors GroupsAggregation)
    foreach (var value in _extractMultiValuedClaim(
        context, _options.GroupsClaimTypes, _options.GroupsAggregation)) {
      principals.Add(SecurityPrincipalId.Group(value));
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
  /// Primary claim type for tenant ID. Default: "tenant_id". Backwards-compatible
  /// shim over <see cref="TenantIdClaimTypes"/> — getting reads the first entry,
  /// setting replaces the list with a single value.
  /// </summary>
  public string TenantIdClaimType {
    get => TenantIdClaimTypes.FirstOrDefault() ?? "tenant_id";
    set => TenantIdClaimTypes = [value];
  }

  /// <summary>
  /// Claim types for tenant ID, tried in order until a non-empty value is found.
  /// Default: ["tenant_id"]. Use this to support multiple identity providers that
  /// emit the same logical claim under different names.
  /// </summary>
  public List<string> TenantIdClaimTypes { get; set; } = ["tenant_id"];

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
  /// Primary claim type for organization ID. Default: "org_id". Backwards-compatible
  /// shim over <see cref="OrganizationIdClaimTypes"/>.
  /// </summary>
  public string OrganizationIdClaimType {
    get => OrganizationIdClaimTypes.FirstOrDefault() ?? "org_id";
    set => OrganizationIdClaimTypes = [value];
  }

  /// <summary>
  /// Claim types for organization ID, tried in order until a non-empty value is found.
  /// </summary>
  public List<string> OrganizationIdClaimTypes { get; set; } = ["org_id"];

  /// <summary>
  /// Header name for organization ID. Default: "X-Organization-Id".
  /// </summary>
  public string OrganizationIdHeaderName { get; set; } = "X-Organization-Id";

  /// <summary>
  /// Primary claim type for customer ID. Default: "customer_id". Backwards-compatible
  /// shim over <see cref="CustomerIdClaimTypes"/>.
  /// </summary>
  public string CustomerIdClaimType {
    get => CustomerIdClaimTypes.FirstOrDefault() ?? "customer_id";
    set => CustomerIdClaimTypes = [value];
  }

  /// <summary>
  /// Claim types for customer ID, tried in order until a non-empty value is found.
  /// </summary>
  public List<string> CustomerIdClaimTypes { get; set; } = ["customer_id"];

  /// <summary>
  /// Header name for customer ID. Default: "X-Customer-Id".
  /// </summary>
  public string CustomerIdHeaderName { get; set; } = "X-Customer-Id";

  /// <summary>
  /// Header name for the inbound correlation id. Default: "X-Correlation-ID". When present and parseable as a
  /// Guid/UUID, the scope middleware adopts it as the ambient inbound correlation so a client-supplied token
  /// flows through the whole message graph (the HotChocolate resolver's dispatch adopts it instead of minting a
  /// fresh trace-aligned root). HTTP header names are case-insensitive.
  /// </summary>
  public string CorrelationIdHeaderName { get; set; } = "X-Correlation-ID";

  /// <summary>
  /// Claim type for roles. Default: ClaimTypes.Role.
  /// </summary>
  public string RolesClaimType { get; set; } = ClaimTypes.Role;

  /// <summary>
  /// Primary claim type for permissions. Default: "permissions". Backwards-compatible
  /// shim over <see cref="PermissionsClaimTypes"/>.
  /// </summary>
  public string PermissionsClaimType {
    get => PermissionsClaimTypes.FirstOrDefault() ?? "permissions";
    set => PermissionsClaimTypes = [value];
  }

  /// <summary>
  /// Claim types for permissions. Behavior when multiple are configured is governed by
  /// <see cref="PermissionsAggregation"/> — first-match (default) or aggregate.
  /// </summary>
  public List<string> PermissionsClaimTypes { get; set; } = ["permissions"];

  /// <summary>
  /// Aggregation strategy across <see cref="PermissionsClaimTypes"/>.
  /// Default: <see cref="ClaimAggregation.FirstMatch"/> (matches legacy single-source behavior).
  /// </summary>
  public ClaimAggregation PermissionsAggregation { get; set; } = ClaimAggregation.FirstMatch;

  /// <summary>
  /// Primary claim type for groups. Default: "groups". Backwards-compatible shim over
  /// <see cref="GroupsClaimTypes"/>.
  /// </summary>
  public string GroupsClaimType {
    get => GroupsClaimTypes.FirstOrDefault() ?? "groups";
    set => GroupsClaimTypes = [value];
  }

  /// <summary>
  /// Claim types for groups. Behavior when multiple are configured is governed by
  /// <see cref="GroupsAggregation"/>.
  /// </summary>
  public List<string> GroupsClaimTypes { get; set; } = ["groups"];

  /// <summary>
  /// Aggregation strategy across <see cref="GroupsClaimTypes"/>.
  /// Default: <see cref="ClaimAggregation.FirstMatch"/>.
  /// </summary>
  public ClaimAggregation GroupsAggregation { get; set; } = ClaimAggregation.FirstMatch;

  /// <summary>
  /// Custom claim type to extension key mappings.
  /// </summary>
  public Dictionary<string, string> ExtensionClaimMappings { get; } = [];

  /// <summary>
  /// Custom header name to extension key mappings.
  /// </summary>
  public Dictionary<string, string> ExtensionHeaderMappings { get; } = [];
}
