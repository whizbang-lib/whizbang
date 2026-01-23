using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for ScopedLensFactory with real PostgreSQL.
/// Tests the complete flow: scope context → factory → filtered lens.
/// </summary>
/// <remarks>
/// These tests verify:
/// 1. ScopedLensFactory correctly resolves lenses from DI
/// 2. Scope filters are applied via IFilterableLens.ApplyFilter
/// 3. Permission checks work with emitted events
///
/// For full database-level filtering (WHERE clauses based on scope),
/// EF Core lens implementations would need to implement IFilterableLens
/// and translate ScopeFilterInfo to EF Core query filters.
/// </remarks>
/// <tests>Whizbang.Core.Tests/Lenses/ScopedLensFactoryImplTests.cs</tests>
[Category("Integration")]
public class ScopedLensFactoryIntegrationTests : EFCoreTestBase {
  private readonly Uuid7IdProvider _idProvider = new Uuid7IdProvider();

  // === Factory Resolution Tests ===

  [Test]
  public async Task ScopedLensFactory_WithDI_ResolvesLensCorrectlyAsync() {
    // Arrange
    var services = new ServiceCollection();
    var accessor = new ScopeContextAccessor();

    services.AddSingleton<IScopeContextAccessor>(accessor);
    services.AddSingleton<ISystemEventEmitter, TestSystemEventEmitter>();
    services.AddSingleton<LensOptions>();
    services.AddScoped<IOrderLensQuery, OrderLensQuery>();

    var provider = services.BuildServiceProvider();
    var factory = new ScopedLensFactory(
      provider,
      accessor,
      provider.GetRequiredService<LensOptions>(),
      provider.GetRequiredService<ISystemEventEmitter>());

    // Set scope context
    accessor.Current = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var lens = factory.GetTenantLens<IOrderLensQuery>();

    // Assert
    await Assert.That(lens).IsNotNull();
    await Assert.That(lens.AppliedFilter!.Value.TenantId).IsEqualTo("tenant-123");
  }

  [Test]
  public async Task ScopedLensFactory_WithPermissionCheck_DeniesUnauthorizedAccessAsync() {
    // Arrange
    var services = new ServiceCollection();
    var accessor = new ScopeContextAccessor();
    var eventEmitter = new TestSystemEventEmitter();

    services.AddSingleton<IScopeContextAccessor>(accessor);
    services.AddSingleton<ISystemEventEmitter>(eventEmitter);
    services.AddSingleton<LensOptions>();
    services.AddScoped<IOrderLensQuery, OrderLensQuery>();

    var provider = services.BuildServiceProvider();
    var factory = new ScopedLensFactory(
      provider,
      accessor,
      provider.GetRequiredService<LensOptions>(),
      eventEmitter);

    // Set scope context with limited permissions
    accessor.Current = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> { Permission.Read("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act & Assert
    await Assert.That(() => factory.GetLens<IOrderLensQuery>(
      ScopeFilter.Tenant,
      Permission.Delete("orders")))
      .Throws<AccessDeniedException>();
  }

  [Test]
  public async Task ScopedLensFactory_WithPrincipalFilter_PopulatesSecurityPrincipalsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var accessor = new ScopeContextAccessor();

    services.AddSingleton<IScopeContextAccessor>(accessor);
    services.AddSingleton<ISystemEventEmitter, TestSystemEventEmitter>();
    services.AddSingleton<LensOptions>();
    services.AddScoped<IOrderLensQuery, OrderLensQuery>();

    var provider = services.BuildServiceProvider();
    var factory = new ScopedLensFactory(
      provider,
      accessor,
      provider.GetRequiredService<LensOptions>(),
      provider.GetRequiredService<ISystemEventEmitter>());

    // Set scope context with security principals
    accessor.Current = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("user-456"),
        SecurityPrincipalId.Group("sales-team"),
        SecurityPrincipalId.Group("east-coast")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act
    var lens = factory.GetPrincipalLens<IOrderLensQuery>();

    // Assert
    await Assert.That(lens.AppliedFilter!.Value.SecurityPrincipals.Count).IsEqualTo(3);
    await Assert.That(lens.AppliedFilter!.Value.Filters.HasFlag(ScopeFilter.Principal)).IsTrue();
    await Assert.That(lens.AppliedFilter!.Value.Filters.HasFlag(ScopeFilter.Tenant)).IsTrue();
  }

  [Test]
  public async Task ScopedLensFactory_GetMyOrSharedLens_UsesOrLogicForUserAndPrincipalAsync() {
    // Arrange
    var services = new ServiceCollection();
    var accessor = new ScopeContextAccessor();

    services.AddSingleton<IScopeContextAccessor>(accessor);
    services.AddSingleton<ISystemEventEmitter, TestSystemEventEmitter>();
    services.AddSingleton<LensOptions>();
    services.AddScoped<IOrderLensQuery, OrderLensQuery>();

    var provider = services.BuildServiceProvider();
    var factory = new ScopedLensFactory(
      provider,
      accessor,
      provider.GetRequiredService<LensOptions>(),
      provider.GetRequiredService<ISystemEventEmitter>());

    // Set scope context with user and principal
    accessor.Current = new ScopeContext {
      Scope = new PerspectiveScope {
        TenantId = "tenant-123",
        UserId = "user-456"
      },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("user-456"),
        SecurityPrincipalId.Group("shared-team")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act
    var lens = factory.GetMyOrSharedLens<IOrderLensQuery>();

    // Assert - Should use OR logic between User and Principal
    await Assert.That(lens.AppliedFilter!.Value.UseOrLogicForUserAndPrincipal).IsTrue();
    await Assert.That(lens.AppliedFilter!.Value.UserId).IsEqualTo("user-456");
    await Assert.That(lens.AppliedFilter!.Value.SecurityPrincipals.Count).IsEqualTo(2);
  }

  // === Database-Backed Integration Tests ===

  [Test]
  public async Task ScopedLensFactory_GetTenantLens_WithRealDatabase_FiltersCorrectlyAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var (provider, factory, contextAccessor) = _createDatabaseBackedServices(context);

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-2");

    contextAccessor.Current = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var lens = factory.GetTenantLens<IDatabaseOrderLensQuery>();
    var result = await lens.Query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(order1Id);
  }

  [Test]
  public async Task ScopedLensFactory_GetPrincipalLens_WithRealDatabase_FiltersCorrectlyAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var (provider, factory, contextAccessor) = _createDatabaseBackedServices(context);

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();

    await _seedOrderAsync(context, order1Id, 100m,
      tenantId: "tenant-1",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("sales") });

    await _seedOrderAsync(context, order2Id, 200m,
      tenantId: "tenant-1",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("engineering") });

    contextAccessor.Current = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("alice"),
        SecurityPrincipalId.Group("sales")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act
    var lens = factory.GetPrincipalLens<IDatabaseOrderLensQuery>();
    var result = await lens.Query.ToListAsync();

    // Assert - Only order shared with sales
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(order1Id);
  }

  [Test]
  public async Task ScopedLensFactory_GetMyOrSharedLens_WithRealDatabase_ReturnsOwnedAndSharedAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var (provider, factory, contextAccessor) = _createDatabaseBackedServices(context);

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    var order3Id = _idProvider.NewGuid();

    // Owned by alice
    await _seedOrderAsync(context, order1Id, 100m,
      tenantId: "tenant-1",
      userId: "user-alice");

    // Shared with sales-team
    await _seedOrderAsync(context, order2Id, 200m,
      tenantId: "tenant-1",
      userId: "user-bob",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("sales-team") });

    // Neither owned nor shared
    await _seedOrderAsync(context, order3Id, 300m,
      tenantId: "tenant-1",
      userId: "user-charlie",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("other-team") });

    contextAccessor.Current = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-alice" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("alice"),
        SecurityPrincipalId.Group("sales-team")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act
    var lens = factory.GetMyOrSharedLens<IDatabaseOrderLensQuery>();
    var result = await lens.Query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(2);
    var resultIds = result.Select(r => r.Id).ToHashSet();
    await Assert.That(resultIds.Contains(order1Id)).IsTrue();
    await Assert.That(resultIds.Contains(order2Id)).IsTrue();
    await Assert.That(resultIds.Contains(order3Id)).IsFalse();
  }

  [Test]
  public async Task ScopedLensFactory_GetLensWithPermission_WithRealDatabase_EnforcesPermissionAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var (provider, factory, contextAccessor) = _createDatabaseBackedServices(context);

    var orderId = _idProvider.NewGuid();
    await _seedOrderAsync(context, orderId, 100m, tenantId: "tenant-1");

    contextAccessor.Current = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> { Permission.Read("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var lens = factory.GetLens<IDatabaseOrderLensQuery>(ScopeFilter.Tenant, Permission.Read("orders"));
    var result = await lens.Query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
  }

  [Test]
  public async Task ScopedLensFactory_GetLensWithPermission_WithRealDatabase_DeniesUnauthorizedAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var (provider, factory, contextAccessor) = _createDatabaseBackedServices(context);

    contextAccessor.Current = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),  // No permissions
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act & Assert
    await Assert.That(() => factory.GetLens<IDatabaseOrderLensQuery>(ScopeFilter.Tenant, Permission.Read("orders")))
      .Throws<AccessDeniedException>();
  }

  // === Database Helper Methods ===

  private async Task _seedOrderAsync(
      DbContext context,
      Guid orderId,
      decimal amount,
      string? tenantId = null,
      string? userId = null,
      IReadOnlyList<SecurityPrincipalId>? allowedPrincipals = null) {

    var order = new Order {
      OrderId = TestOrderId.From(orderId),
      Amount = amount,
      Status = "Created"
    };

    var row = new PerspectiveRow<Order> {
      Id = orderId,
      Data = order,
      Metadata = new PerspectiveMetadata {
        EventType = "OrderCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      },
      Scope = new PerspectiveScope {
        TenantId = tenantId,
        UserId = userId,
        AllowedPrincipals = allowedPrincipals
      },
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };

    context.Set<PerspectiveRow<Order>>().Add(row);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();
  }

  private (IServiceProvider provider, IScopedLensFactory factory, ScopeContextAccessor contextAccessor)
    _createDatabaseBackedServices(DbContext context) {
    var services = new ServiceCollection();

    // Register DbContext as scoped
    services.AddScoped<DbContext>(_ => context);

    // Register database-backed lens
    services.AddScoped<IDatabaseOrderLensQuery, DatabaseOrderLensQuery>();

    // Register scope context accessor
    var contextAccessor = new ScopeContextAccessor();
    services.AddSingleton<IScopeContextAccessor>(contextAccessor);

    // Register lens options
    services.AddSingleton<LensOptions>();

    // Register system event emitter
    services.AddSingleton<ISystemEventEmitter, TestSystemEventEmitter>();

    // Build and create factory
    var provider = services.BuildServiceProvider();
    var factory = new ScopedLensFactory(
      provider,
      contextAccessor,
      provider.GetRequiredService<LensOptions>(),
      provider.GetRequiredService<ISystemEventEmitter>());

    return (provider, factory, contextAccessor);
  }

  // === Database-Backed Test Types ===

  /// <summary>
  /// Database-backed lens query interface.
  /// </summary>
  public interface IDatabaseOrderLensQuery : ILensQuery<Order> { }

  /// <summary>
  /// Database-backed lens implementation using EFCoreFilterableLensQuery.
  /// </summary>
  private sealed class DatabaseOrderLensQuery : IDatabaseOrderLensQuery, IFilterableLens {
    private readonly EFCoreFilterableLensQuery<Order> _inner;

    public DatabaseOrderLensQuery(DbContext context) {
      _inner = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");
    }

    public void ApplyFilter(ScopeFilterInfo filterInfo) {
      _inner.ApplyFilter(filterInfo);
    }

    public IQueryable<PerspectiveRow<Order>> Query => _inner.Query;

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
      return _inner.GetByIdAsync(id, cancellationToken);
    }
  }

  // === Mock Test Types ===

  /// <summary>
  /// Test lens query interface that implements IFilterableLens.
  /// </summary>
  public interface IOrderLensQuery : ILensQuery, IFilterableLens {
    ScopeFilterInfo? AppliedFilter { get; }
  }

  /// <summary>
  /// Test implementation that captures applied filter info.
  /// </summary>
  private sealed class OrderLensQuery : IOrderLensQuery {
    public ScopeFilterInfo? AppliedFilter { get; private set; }

    public void ApplyFilter(ScopeFilterInfo filterInfo) {
      AppliedFilter = filterInfo;
    }
  }

  /// <summary>
  /// Test system event emitter that captures emitted events.
  /// </summary>
  private sealed class TestSystemEventEmitter : ISystemEventEmitter {
    public List<object> EmittedEvents { get; } = [];

    public Task EmitEventAuditedAsync<TEvent>(
        Guid streamId,
        long streamPosition,
        MessageEnvelope<TEvent> envelope,
        CancellationToken cancellationToken = default) {
      EmittedEvents.Add(envelope);
      return Task.CompletedTask;
    }

    public Task EmitCommandAuditedAsync<TCommand, TResponse>(
        TCommand command,
        TResponse response,
        string receptorName,
        IMessageContext? context,
        CancellationToken cancellationToken = default) where TCommand : notnull {
      EmittedEvents.Add(command);
      return Task.CompletedTask;
    }

    public Task EmitAsync<TSystemEvent>(
        TSystemEvent systemEvent,
        CancellationToken cancellationToken = default) where TSystemEvent : ISystemEvent {
      EmittedEvents.Add(systemEvent!);
      return Task.CompletedTask;
    }

    public bool ShouldExcludeFromAudit(Type type) => false;
  }
}
