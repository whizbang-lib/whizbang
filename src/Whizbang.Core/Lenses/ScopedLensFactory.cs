using Whizbang.Core.Messaging;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.SystemEvents.Security;

namespace Whizbang.Core.Lenses;

/// <summary>
/// Factory for creating scoped lens instances with composable filtering.
/// Resolves lens instances from DI and applies scope filters based on current context.
/// </summary>
/// <docs>fundamentals/lenses/scoped-lenses</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopedLensFactoryImplTests.cs</tests>
/// <remarks>
/// Creates a new ScopedLensFactory.
/// </remarks>
/// <param name="serviceProvider">Service provider for resolving lens instances.</param>
/// <param name="scopeContextAccessor">Accessor for current scope context.</param>
/// <param name="lensOptions">Lens configuration options.</param>
/// <param name="eventEmitter">System event emitter for security events.</param>
public sealed class ScopedLensFactory(
    IServiceProvider serviceProvider,
    IScopeContextAccessor scopeContextAccessor,
    LensOptions lensOptions,
    ISystemEventEmitter eventEmitter) : IScopedLensFactory {
  private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
  private readonly IScopeContextAccessor _scopeContextAccessor = scopeContextAccessor ?? throw new ArgumentNullException(nameof(scopeContextAccessor));
  private readonly LensOptions _lensOptions = lensOptions ?? throw new ArgumentNullException(nameof(lensOptions));
  private readonly ISystemEventEmitter _eventEmitter = eventEmitter ?? throw new ArgumentNullException(nameof(eventEmitter));

  // === Legacy API (string-based scope names) ===

  /// <inheritdoc/>
  public TLens GetLens<TLens>(string scopeName) where TLens : ILensQuery {
    ArgumentNullException.ThrowIfNull(scopeName);

    var scopeDefinition = _lensOptions.GetScope(scopeName)
      ?? throw new ArgumentException($"Scope '{scopeName}' is not defined. Define it using LensOptions.DefineScope().", nameof(scopeName));

    // Convert string-based scope to ScopeFilters flags
    var filters = _convertScopeDefinitionToFilter(scopeDefinition);
    return GetLens<TLens>(filters);
  }

  // === Primary API: Composable flags ===

  /// <inheritdoc/>
  public TLens GetLens<TLens>(ScopeFilters filters) where TLens : ILensQuery {
    var filterInfo = _buildFilterInfo(filters);
    return _resolveLensWithFilter<TLens>(filterInfo);
  }

  /// <inheritdoc/>
  public TLens GetLens<TLens>(ScopeFilters filters, Permission requiredPermission) where TLens : ILensQuery {
    _checkPermission(requiredPermission, typeof(TLens).Name);
    return GetLens<TLens>(filters);
  }

  /// <inheritdoc/>
  public TLens GetLens<TLens>(ScopeFilters filters, params Permission[] anyOfPermissions) where TLens : ILensQuery {
    ArgumentNullException.ThrowIfNull(anyOfPermissions);

    if (anyOfPermissions.Length == 0) {
      throw new ArgumentException("At least one permission must be specified.", nameof(anyOfPermissions));
    }

    _checkAnyPermission(anyOfPermissions, typeof(TLens).Name);
    return GetLens<TLens>(filters);
  }

  // === Convenience methods for common patterns ===

  /// <inheritdoc/>
  public TLens GetGlobalLens<TLens>() where TLens : ILensQuery =>
    GetLens<TLens>(ScopeFilters.None);

  /// <inheritdoc/>
  public TLens GetTenantLens<TLens>() where TLens : ILensQuery =>
    GetLens<TLens>(ScopeFilters.Tenant);

  /// <inheritdoc/>
  public TLens GetUserLens<TLens>() where TLens : ILensQuery =>
    GetLens<TLens>(ScopeFilters.Tenant | ScopeFilters.User);

  /// <inheritdoc/>
  public TLens GetOrganizationLens<TLens>() where TLens : ILensQuery =>
    GetLens<TLens>(ScopeFilters.Tenant | ScopeFilters.Organization);

  /// <inheritdoc/>
  public TLens GetCustomerLens<TLens>() where TLens : ILensQuery =>
    GetLens<TLens>(ScopeFilters.Tenant | ScopeFilters.Customer);

  /// <inheritdoc/>
  public TLens GetPrincipalLens<TLens>() where TLens : ILensQuery =>
    GetLens<TLens>(ScopeFilters.Tenant | ScopeFilters.Principal);

  /// <inheritdoc/>
  public TLens GetMyOrSharedLens<TLens>() where TLens : ILensQuery =>
    GetLens<TLens>(ScopeFilters.Tenant | ScopeFilters.User | ScopeFilters.Principal);

  // === Event Store Query Methods ===

  /// <inheritdoc/>
  public IEventStoreQuery GetEventStoreQuery(ScopeFilters filters) {
    var filterInfo = _buildFilterInfo(filters);
    return _resolveEventStoreQueryWithFilter(filterInfo);
  }

  /// <inheritdoc/>
  public IEventStoreQuery GetEventStoreQuery(ScopeFilters filters, Permission requiredPermission) {
    _checkPermission(requiredPermission, "EventStoreQuery");
    return GetEventStoreQuery(filters);
  }

  /// <inheritdoc/>
  public IEventStoreQuery GetGlobalEventStoreQuery() =>
    GetEventStoreQuery(ScopeFilters.None);

  /// <inheritdoc/>
  public IEventStoreQuery GetTenantEventStoreQuery() =>
    GetEventStoreQuery(ScopeFilters.Tenant);

  /// <inheritdoc/>
  public IEventStoreQuery GetUserEventStoreQuery() =>
    GetEventStoreQuery(ScopeFilters.Tenant | ScopeFilters.User);

  // === Private Helper Methods ===

  private ScopeFilterInfo _buildFilterInfo(ScopeFilters filters) {
    if (filters == ScopeFilters.None) {
      return new ScopeFilterInfo {
        Filters = ScopeFilters.None,
        SecurityPrincipals = new HashSet<SecurityPrincipalId>()
      };
    }

    var context = _scopeContextAccessor.Current
      ?? throw new InvalidOperationException(
        "No scope context available. Ensure IScopeContextAccessor.Current is set before accessing scoped lenses.");

    return ScopeFilterBuilder.Build(filters, context);
  }

  private TLens _resolveLensWithFilter<TLens>(ScopeFilterInfo filterInfo) where TLens : ILensQuery {
    var lens = _serviceProvider.GetService(typeof(TLens))
      ?? throw new InvalidOperationException(
        $"Lens type '{typeof(TLens).Name}' is not registered in the service provider.");

    // If the lens supports filter application, apply it
    if (lens is IFilterableLens filterable) {
      filterable.ApplyFilter(filterInfo);
    }

    return (TLens)lens;
  }

  private IEventStoreQuery _resolveEventStoreQueryWithFilter(ScopeFilterInfo filterInfo) {
    var query = _serviceProvider.GetService(typeof(IFilterableEventStoreQuery))
      ?? throw new InvalidOperationException(
        "IFilterableEventStoreQuery is not registered in the service provider. " +
        "Ensure Whizbang infrastructure is properly configured.");

    // Apply the filter to the query
    var filterable = (IFilterableEventStoreQuery)query;
    filterable.ApplyFilter(filterInfo);

    return filterable;
  }

  private void _checkPermission(Permission requiredPermission, string resourceType) {
    var context = _scopeContextAccessor.Current;
    if (context?.HasPermission(requiredPermission) != true) {
      _emitAccessDenied(requiredPermission, resourceType, context);
      throw new AccessDeniedException(
        requiredPermission,
        resourceType,
        resourceId: null,
        AccessDenialReason.InsufficientPermission);
    }
  }

  private void _checkAnyPermission(Permission[] permissions, string resourceType) {
    var context = _scopeContextAccessor.Current;
    if (context?.HasAnyPermission(permissions) != true) {
      // Report the first permission as the "required" one
      _emitAccessDenied(permissions[0], resourceType, context);
      throw new AccessDeniedException(
        permissions[0],
        resourceType,
        resourceId: null,
        AccessDenialReason.InsufficientPermission);
    }
  }

  private void _emitAccessDenied(Permission requiredPermission, string resourceType, IScopeContext? context) {
    // Fire-and-forget async emission - audit events shouldn't block the main flow
    _ = _eventEmitter.EmitAsync(new AccessDenied {
      ResourceType = resourceType,
      RequiredPermission = requiredPermission,
      CallerPermissions = context?.Permissions ?? new HashSet<Permission>(),
      CallerRoles = context?.Roles ?? new HashSet<string>(),
      Scope = context?.Scope ?? new PerspectiveScope(),
      Reason = AccessDenialReason.InsufficientPermission,
      Timestamp = DateTimeOffset.UtcNow
    });
  }

  private static ScopeFilters _convertScopeDefinitionToFilter(ScopeDefinition scopeDefinition) {
    if (scopeDefinition.NoFilter) {
      return ScopeFilters.None;
    }

    var filters = ScopeFilters.None;

    // Map property names to filter flags
    if (!string.IsNullOrEmpty(scopeDefinition.FilterPropertyName)) {
      filters |= scopeDefinition.FilterPropertyName switch {
        "TenantId" => ScopeFilters.Tenant,
        "UserId" => ScopeFilters.User,
        "OrganizationId" => ScopeFilters.Organization,
        "CustomerId" => ScopeFilters.Customer,
        _ => ScopeFilters.None
      };
    }

    // Check for interface types
    if (scopeDefinition.FilterInterfaceType is not null) {
      var interfaceName = scopeDefinition.FilterInterfaceType.Name;
      filters |= interfaceName switch {
        "ITenantScoped" => ScopeFilters.Tenant,
        "IUserScoped" => ScopeFilters.Tenant | ScopeFilters.User,
        "IOrganizationScoped" => ScopeFilters.Tenant | ScopeFilters.Organization,
        "ICustomerScoped" => ScopeFilters.Tenant | ScopeFilters.Customer,
        _ => ScopeFilters.None
      };
    }

    return filters;
  }
}

/// <summary>
/// Interface for lenses that support filter application.
/// </summary>
public interface IFilterableLens {
  /// <summary>
  /// Apply scope filter to this lens.
  /// </summary>
  /// <param name="filterInfo">The filter information to apply.</param>
  void ApplyFilter(ScopeFilterInfo filterInfo);
}
