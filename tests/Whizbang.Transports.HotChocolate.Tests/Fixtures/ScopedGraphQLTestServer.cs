using HotChocolate;
using HotChocolate.Data;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Transports.HotChocolate.Tests.Fixtures;

/// <summary>
/// Query type for scoped integration tests.
/// Includes scope-aware queries and a currentScope resolver.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "HotChocolate requires instance methods for GraphQL resolvers")]
public class ScopedQuery {
  /// <summary>
  /// Query orders with scope filtering.
  /// </summary>
  [UsePaging(DefaultPageSize = 10, MaxPageSize = 100, IncludeTotalCount = true)]
  [UseProjection]
  [UseFiltering]
  [UseSorting]
  public IQueryable<PerspectiveRow<OrderReadModel>> GetOrders(
      [Service] IScopedOrderLens lens) {
    return lens.Query;
  }

  /// <summary>
  /// Returns the current scope context for testing.
  /// </summary>
  public CurrentScopeResult GetCurrentScope(
      [Service] IScopeContextAccessor accessor) {
    var context = accessor.Current;
    return new CurrentScopeResult {
      TenantId = context?.Scope.TenantId,
      UserId = context?.Scope.UserId,
      OrganizationId = context?.Scope.OrganizationId,
      CustomerId = context?.Scope.CustomerId
    };
  }
}

/// <summary>
/// Result type for currentScope query.
/// </summary>
public record CurrentScopeResult {
  public string? TenantId { get; init; }
  public string? UserId { get; init; }
  public string? OrganizationId { get; init; }
  public string? CustomerId { get; init; }
}

/// <summary>
/// Lens interface for scoped order queries.
/// </summary>
public interface IScopedOrderLens : ILensQuery<OrderReadModel> { }

/// <summary>
/// Scoped lens implementation that filters by the current scope context.
/// </summary>
public class ScopedTestOrderLens : IScopedOrderLens {
  private readonly List<PerspectiveRow<OrderReadModel>> _data;
  private readonly IScopeContextAccessor _scopeContextAccessor;

  public ScopedTestOrderLens(IScopeContextAccessor scopeContextAccessor) {
    _data = [];
    _scopeContextAccessor = scopeContextAccessor;
  }

  public IQueryable<PerspectiveRow<OrderReadModel>> Query {
    get {
      var context = _scopeContextAccessor.Current;
      if (context == null) {
        return _data.AsQueryable();
      }

      var query = _data.AsQueryable();
      var scope = context.Scope;

      // Filter by TenantId if specified
      if (!string.IsNullOrEmpty(scope.TenantId)) {
        query = query.Where(r => r.Scope.TenantId == scope.TenantId);
      }

      // Filter by UserId if specified
      if (!string.IsNullOrEmpty(scope.UserId)) {
        query = query.Where(r => r.Scope.UserId == scope.UserId);
      }

      // Filter by OrganizationId if specified
      if (!string.IsNullOrEmpty(scope.OrganizationId)) {
        query = query.Where(r => r.Scope.OrganizationId == scope.OrganizationId);
      }

      // Filter by CustomerId if specified
      if (!string.IsNullOrEmpty(scope.CustomerId)) {
        query = query.Where(r => r.Scope.CustomerId == scope.CustomerId);
      }

      // Filter by AllowedPrincipals using overlap
      if (context.SecurityPrincipals.Count > 0) {
        query = query.Where(r =>
            r.Scope.AllowedPrincipals == null ||
            r.Scope.AllowedPrincipals.Count == 0 ||
            r.Scope.AllowedPrincipals.Any(p => context.SecurityPrincipals.Contains(p)));
      }

      return query;
    }
  }

  public Task<OrderReadModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
    var row = _data.FirstOrDefault(r => r.Id == id);
    return Task.FromResult(row?.Data);
  }

  public void AddData(IEnumerable<PerspectiveRow<OrderReadModel>> rows) {
    _data.AddRange(rows);
  }
}

/// <summary>
/// Test scope context accessor for in-memory testing.
/// </summary>
public class TestScopeContextAccessor : IScopeContextAccessor {
  private readonly AsyncLocal<IScopeContext?> _current = new();

  public IScopeContext? Current {
    get => _current.Value;
    set => _current.Value = value;
  }
}

/// <summary>
/// Test scope context implementation.
/// </summary>
public class TestScopeContext : IScopeContext {
  public PerspectiveScope Scope { get; init; } = new();
  public IReadOnlySet<string> Roles { get; init; } = new HashSet<string>();
  public IReadOnlySet<Permission> Permissions { get; init; } = new HashSet<Permission>();
  public IReadOnlySet<SecurityPrincipalId> SecurityPrincipals { get; init; } = new HashSet<SecurityPrincipalId>();
  public IReadOnlyDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();

  public bool HasPermission(Permission permission) => Permissions.Contains(permission);
  public bool HasAnyPermission(params Permission[] permissions) => permissions.Any(p => Permissions.Contains(p));
  public bool HasAllPermissions(params Permission[] permissions) => permissions.All(p => Permissions.Contains(p));
  public bool HasRole(string roleName) => Roles.Contains(roleName);
  public bool HasAnyRole(params string[] roleNames) => roleNames.Any(r => Roles.Contains(r));
  public bool IsMemberOfAny(params SecurityPrincipalId[] principals) => principals.Any(p => SecurityPrincipals.Contains(p));
  public bool IsMemberOfAll(params SecurityPrincipalId[] principals) => principals.All(p => SecurityPrincipals.Contains(p));
}

/// <summary>
/// GraphQL test server with scope middleware support.
/// </summary>
public sealed class ScopedGraphQLTestServer : IAsyncDisposable {
  private readonly IRequestExecutor _executor;
  private readonly ServiceProvider _serviceProvider;
  private readonly TestScopeContextAccessor _scopeContextAccessor;

  public ScopedTestOrderLens OrderLens { get; }

  private ScopedGraphQLTestServer(
      IRequestExecutor executor,
      ServiceProvider serviceProvider,
      ScopedTestOrderLens orderLens,
      TestScopeContextAccessor scopeContextAccessor) {
    _executor = executor;
    _serviceProvider = serviceProvider;
    OrderLens = orderLens;
    _scopeContextAccessor = scopeContextAccessor;
  }

  /// <summary>
  /// Creates a new scoped GraphQL test server instance.
  /// </summary>
  public static async Task<ScopedGraphQLTestServer> CreateAsync() {
    var scopeContextAccessor = new TestScopeContextAccessor();
    var orderLens = new ScopedTestOrderLens(scopeContextAccessor);

    var services = new ServiceCollection();

    // Register test dependencies
    services.AddSingleton<IScopeContextAccessor>(scopeContextAccessor);
    services.AddSingleton<IScopedOrderLens>(orderLens);

    // Configure HotChocolate
    services
        .AddGraphQLServer()
        .AddWhizbangLenses()
        .AddQueryType<ScopedQuery>();

    var serviceProvider = services.BuildServiceProvider();
    var executor = await serviceProvider.GetRequestExecutorAsync();

    return new ScopedGraphQLTestServer(
        executor,
        serviceProvider,
        orderLens,
        scopeContextAccessor);
  }

  /// <summary>
  /// Executes a GraphQL query without any scope.
  /// </summary>
  public async Task<IExecutionResult> ExecuteAsync(string query) {
    _scopeContextAccessor.Current = null;
    return await _executor.ExecuteAsync(query);
  }

  /// <summary>
  /// Executes a GraphQL query with a specific scope.
  /// </summary>
  public async Task<IExecutionResult> ExecuteWithScopeAsync(string query, PerspectiveScope scope) {
    _scopeContextAccessor.Current = new TestScopeContext { Scope = scope };
    return await _executor.ExecuteAsync(query);
  }

  /// <summary>
  /// Executes a GraphQL query with specific security principals.
  /// </summary>
  public async Task<IExecutionResult> ExecuteWithPrincipalsAsync(
      string query,
      IEnumerable<SecurityPrincipalId> principals) {
    _scopeContextAccessor.Current = new TestScopeContext {
      SecurityPrincipals = principals.ToHashSet()
    };
    return await _executor.ExecuteAsync(query);
  }

  /// <summary>
  /// Executes a GraphQL query with scope extracted from headers.
  /// This simulates what the middleware would do.
  /// </summary>
  public async Task<IExecutionResult> ExecuteWithHeadersAsync(
      string query,
      IDictionary<string, string> headers) {
    var scope = new PerspectiveScope {
      TenantId = headers.TryGetValue("X-Tenant-Id", out var tenantId) ? tenantId : null,
      UserId = headers.TryGetValue("X-User-Id", out var userId) ? userId : null,
      OrganizationId = headers.TryGetValue("X-Organization-Id", out var orgId) ? orgId : null,
      CustomerId = headers.TryGetValue("X-Customer-Id", out var customerId) ? customerId : null
    };

    _scopeContextAccessor.Current = new TestScopeContext { Scope = scope };
    return await _executor.ExecuteAsync(query);
  }

  /// <summary>
  /// Gets the schema for inspection.
  /// </summary>
  public ISchema Schema => _executor.Schema;

  public async ValueTask DisposeAsync() {
    await _serviceProvider.DisposeAsync();
  }
}
