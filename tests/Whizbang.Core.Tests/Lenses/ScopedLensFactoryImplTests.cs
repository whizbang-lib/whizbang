using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for ScopedLensFactory implementation.
/// </summary>
/// <tests>ScopedLensFactory</tests>
[Category("Core")]
[Category("Lenses")]
public class ScopedLensFactoryImplTests {
  // === GetLens with ScopeFilters Tests ===

  [Test]
  public async Task ScopedLensFactory_GetLens_None_ReturnsLensAsync() {
    // Arrange
    var (factory, _) = _createFactory();

    // Act
    var lens = factory.GetLens<ITestLensQuery>(ScopeFilters.None);

    // Assert
    await Assert.That(lens).IsNotNull();
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_Tenant_UsesFilterBuilderAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123");
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>(ScopeFilters.Tenant);

    // Assert
    await Assert.That(lens).IsNotNull();
    await Assert.That(lens.AppliedFilter).IsNotNull();
    await Assert.That(lens.AppliedFilter!.Value.TenantId).IsEqualTo("tenant-123");
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_TenantUser_UsesBothFiltersAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123", userId: "user-456");
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>(ScopeFilters.Tenant | ScopeFilters.User);

    // Assert
    await Assert.That(lens.AppliedFilter!.Value.TenantId).IsEqualTo("tenant-123");
    await Assert.That(lens.AppliedFilter!.Value.UserId).IsEqualTo("user-456");
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_Principal_UsesSecurityPrincipalsAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      securityPrincipals: [
        SecurityPrincipalId.User("user-456"),
        SecurityPrincipalId.Group("developers")
      ]);
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>(ScopeFilters.Tenant | ScopeFilters.Principal);

    // Assert
    await Assert.That(lens.AppliedFilter!.Value.SecurityPrincipals.Count).IsEqualTo(2);
  }

  // === GetLens with Permission Tests ===

  [Test]
  public async Task ScopedLensFactory_GetLens_WithPermission_Granted_ReturnsLensAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      permissions: [Permission.Read("orders")]);
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>(ScopeFilters.Tenant, Permission.Read("orders"));

    // Assert
    await Assert.That(lens).IsNotNull();
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_WithPermission_Denied_ThrowsAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      permissions: [Permission.Read("orders")]);
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act & Assert
    await Assert.That(() => factory.GetLens<ITestLensQuery>(
      ScopeFilters.Tenant, Permission.Delete("orders")))
      .Throws<AccessDeniedException>();
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_WithAnyPermission_OneMatch_ReturnsLensAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      permissions: [Permission.Write("orders")]);
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>(
      ScopeFilters.Tenant,
      Permission.Read("orders"),
      Permission.Write("orders"));

    // Assert
    await Assert.That(lens).IsNotNull();
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_WithAnyPermission_NoMatch_ThrowsAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      permissions: [Permission.Read("customers")]);
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act & Assert
    await Assert.That(() => factory.GetLens<ITestLensQuery>(
      ScopeFilters.Tenant,
      Permission.Read("orders"),
      Permission.Write("orders")))
      .Throws<AccessDeniedException>();
  }

  // === Convenience Method Tests ===

  [Test]
  public async Task ScopedLensFactory_GetGlobalLens_UsesNoneFilterAsync() {
    // Arrange
    var (factory, _) = _createFactory();

    // Act
    var lens = factory.GetGlobalLens<ITestLensQuery>();

    // Assert
    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(ScopeFilters.None);
  }

  [Test]
  public async Task ScopedLensFactory_GetTenantLens_UsesTenantFilterAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123");
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetTenantLens<ITestLensQuery>();

    // Assert
    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(ScopeFilters.Tenant);
    await Assert.That(lens.AppliedFilter!.Value.TenantId).IsEqualTo("tenant-123");
  }

  [Test]
  public async Task ScopedLensFactory_GetUserLens_UsesTenantAndUserFilterAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123", userId: "user-456");
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetUserLens<ITestLensQuery>();

    // Assert
    var expectedFilters = ScopeFilters.Tenant | ScopeFilters.User;
    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(expectedFilters);
  }

  [Test]
  public async Task ScopedLensFactory_GetPrincipalLens_UsesTenantAndPrincipalAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      securityPrincipals: [SecurityPrincipalId.Group("team")]);
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetPrincipalLens<ITestLensQuery>();

    // Assert
    var expectedFilters = ScopeFilters.Tenant | ScopeFilters.Principal;
    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(expectedFilters);
  }

  [Test]
  public async Task ScopedLensFactory_GetMyOrSharedLens_UsesOrLogicAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      userId: "user-456",
      securityPrincipals: [SecurityPrincipalId.Group("team")]);
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetMyOrSharedLens<ITestLensQuery>();

    // Assert
    await Assert.That(lens.AppliedFilter!.Value.UseOrLogicForUserAndPrincipal).IsTrue();
  }

  // === GetOrganizationLens and GetCustomerLens Tests ===

  [Test]
  public async Task ScopedLensFactory_GetOrganizationLens_UsesTenantAndOrganizationFilterAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123", organizationId: "org-456");
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetOrganizationLens<ITestLensQuery>();

    // Assert
    const ScopeFilters expectedFilters = ScopeFilters.Tenant | ScopeFilters.Organization;
    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(expectedFilters);
    await Assert.That(lens.AppliedFilter!.Value.OrganizationId).IsEqualTo("org-456");
  }

  [Test]
  public async Task ScopedLensFactory_GetCustomerLens_UsesTenantAndCustomerFilterAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123", customerId: "cust-789");
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetCustomerLens<ITestLensQuery>();

    // Assert
    const ScopeFilters expectedFilters = ScopeFilters.Tenant | ScopeFilters.Customer;
    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(expectedFilters);
    await Assert.That(lens.AppliedFilter!.Value.CustomerId).IsEqualTo("cust-789");
  }

  // === Constructor Validation Tests ===

  [Test]
  public async Task ScopedLensFactory_Constructor_NullServiceProvider_ThrowsAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var options = new LensOptions();
    var emitter = new NullSystemEventEmitter();

    // Act & Assert
    await Assert.That(() => new ScopedLensFactory(null!, accessor, options, emitter))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ScopedLensFactory_Constructor_NullScopeContextAccessor_ThrowsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var provider = services.BuildServiceProvider();
    var options = new LensOptions();
    var emitter = new NullSystemEventEmitter();

    // Act & Assert
    await Assert.That(() => new ScopedLensFactory(provider, null!, options, emitter))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ScopedLensFactory_Constructor_NullLensOptions_ThrowsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var provider = services.BuildServiceProvider();
    var accessor = new ScopeContextAccessor();
    var emitter = new NullSystemEventEmitter();

    // Act & Assert
    await Assert.That(() => new ScopedLensFactory(provider, accessor, null!, emitter))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ScopedLensFactory_Constructor_NullEventEmitter_ThrowsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var provider = services.BuildServiceProvider();
    var accessor = new ScopeContextAccessor();
    var options = new LensOptions();

    // Act & Assert
    await Assert.That(() => new ScopedLensFactory(provider, accessor, options, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // === GetLens String Scope Null Argument Test ===

  [Test]
  public async Task ScopedLensFactory_GetLens_NullScopeName_ThrowsAsync() {
    // Arrange
    var (factory, _) = _createFactory();

    // Act & Assert
    await Assert.That(() => factory.GetLens<ITestLensQuery>((string)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // === GetLens with Empty anyOfPermissions Test ===

  [Test]
  public async Task ScopedLensFactory_GetLens_WithEmptyPermissionsArray_ThrowsAsync() {
    // Arrange
    var (factory, _) = _createFactory();

    // Act & Assert
    await Assert.That(() => factory.GetLens<ITestLensQuery>(
      ScopeFilters.None, []))
      .ThrowsExactly<ArgumentException>();
  }

  // === Missing Scope Context Tests ===

  [Test]
  public async Task ScopedLensFactory_GetLens_NoScopeContext_ThrowsAsync() {
    // Arrange
    var (factory, accessor) = _createFactory();
    accessor.Current = null; // No scope context

    // Act & Assert
    await Assert.That(() => factory.GetLens<ITestLensQuery>(ScopeFilters.Tenant))
      .ThrowsExactly<InvalidOperationException>();
  }

  // === Lens Not Registered Test ===

  [Test]
  public async Task ScopedLensFactory_GetLens_UnregisteredLens_ThrowsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var accessor = new ScopeContextAccessor();
    var lensOptions = new LensOptions();
    var emitter = new NullSystemEventEmitter();
    services.AddSingleton<IScopeContextAccessor>(accessor);
    services.AddSingleton<ISystemEventEmitter>(emitter);
    services.AddSingleton(lensOptions);
    // Note: IUnregisteredLensQuery is NOT registered

    var provider = services.BuildServiceProvider();
    var factory = new ScopedLensFactory(provider, accessor, lensOptions, emitter);

    // Act & Assert
    await Assert.That(() => factory.GetLens<IUnregisteredLensQuery>(ScopeFilters.None))
      .ThrowsExactly<InvalidOperationException>();
  }

  // === Scope Definition with Interface Type Tests ===

  [Test]
  public async Task ScopedLensFactory_GetLens_StringScope_ITenantScoped_MapsCorrectlyAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123");
    var (factory, accessor) = _createFactory(options =>
      options.DefineScope("TenantScoped", scope => scope.FilterInterfaceType = typeof(ITenantScoped)));
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>("TenantScoped");

    // Assert
    await Assert.That(lens).IsNotNull();
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_StringScope_IUserScoped_MapsCorrectlyAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123", userId: "user-456");
    var (factory, accessor) = _createFactory(options =>
      options.DefineScope("UserScoped", scope => scope.FilterInterfaceType = typeof(IUserScoped)));
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>("UserScoped");

    // Assert
    await Assert.That(lens).IsNotNull();
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_StringScope_IOrganizationScoped_MapsCorrectlyAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123", organizationId: "org-456");
    var (factory, accessor) = _createFactory(options =>
      options.DefineScope("OrgScoped", scope => scope.FilterInterfaceType = typeof(IOrganizationScoped)));
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>("OrgScoped");

    // Assert
    await Assert.That(lens).IsNotNull();
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_StringScope_ICustomerScoped_MapsCorrectlyAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123", customerId: "cust-789");
    var (factory, accessor) = _createFactory(options =>
      options.DefineScope("CustomerScoped", scope => scope.FilterInterfaceType = typeof(ICustomerScoped)));
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>("CustomerScoped");

    // Assert
    await Assert.That(lens).IsNotNull();
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_StringScope_NoFilter_MapsCorrectlyAsync() {
    // Arrange
    var (factory, _) = _createFactory(options =>
      options.DefineScope("Global", scope => scope.NoFilter = true));

    // Act
    var lens = factory.GetLens<ITestLensQuery>("Global");

    // Assert
    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(ScopeFilters.None);
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_StringScope_UnknownPropertyName_MapsToNoneAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123");
    var (factory, accessor) = _createFactory(options =>
      options.DefineScope("Unknown", scope => scope.FilterPropertyName = "SomeUnknownProperty"));
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>("Unknown");

    // Assert - Unknown property maps to ScopeFilters.None
    await Assert.That(lens).IsNotNull();
  }

  // === Permission Check with No Context Tests ===

  [Test]
  public async Task ScopedLensFactory_GetLens_WithPermission_NoContext_ThrowsAccessDeniedAsync() {
    // Arrange
    var (factory, accessor) = _createFactory();
    accessor.Current = null; // No context

    // Act & Assert
    await Assert.That(() => factory.GetLens<ITestLensQuery>(
      ScopeFilters.None, Permission.Read("orders")))
      .ThrowsExactly<AccessDeniedException>();
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_WithAnyPermission_NoContext_ThrowsAccessDeniedAsync() {
    // Arrange
    var (factory, accessor) = _createFactory();
    accessor.Current = null; // No context

    // Act & Assert
    await Assert.That(() => factory.GetLens<ITestLensQuery>(
      ScopeFilters.None,
      Permission.Read("orders"),
      Permission.Write("orders")))
      .ThrowsExactly<AccessDeniedException>();
  }

  // === Legacy String-Based API Tests ===

  [Test]
  public async Task ScopedLensFactory_GetLens_StringScope_ResolvesFromOptionsAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123");
    var (factory, accessor) = _createFactory(options => {
      options.DefineScope("Tenant", scope => {
        scope.FilterPropertyName = "TenantId";
        scope.ContextKey = "TenantId";
      });
    });
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>("Tenant");

    // Assert
    await Assert.That(lens).IsNotNull();
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_UnknownScope_ThrowsAsync() {
    // Arrange
    var (factory, _) = _createFactory();

    // Act & Assert
    await Assert.That(() => factory.GetLens<ITestLensQuery>("NonExistent"))
      .Throws<ArgumentException>();
  }

  // === GetEventStoreQuery Tests ===

  [Test]
  public async Task ScopedLensFactory_GetEventStoreQuery_None_ReturnsQueryAsync() {
    // Arrange
    var (factory, _) = _createFactoryWithEventStoreQuery();

    // Act
    var query = factory.GetEventStoreQuery(ScopeFilters.None);

    // Assert
    await Assert.That(query).IsNotNull();
  }

  [Test]
  public async Task ScopedLensFactory_GetEventStoreQuery_Tenant_AppliesFilterAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123");
    var (factory, accessor) = _createFactoryWithEventStoreQuery();
    accessor.Current = context;

    // Act
    var query = factory.GetEventStoreQuery(ScopeFilters.Tenant);

    // Assert
    await Assert.That(query).IsNotNull();
    var filterable = query as TestFilterableEventStoreQuery;
    await Assert.That(filterable!.AppliedFilter!.Value.TenantId).IsEqualTo("tenant-123");
  }

  [Test]
  public async Task ScopedLensFactory_GetEventStoreQuery_WithPermission_Granted_ReturnsQueryAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      permissions: [Permission.Read("events")]);
    var (factory, accessor) = _createFactoryWithEventStoreQuery();
    accessor.Current = context;

    // Act
    var query = factory.GetEventStoreQuery(ScopeFilters.Tenant, Permission.Read("events"));

    // Assert
    await Assert.That(query).IsNotNull();
  }

  [Test]
  public async Task ScopedLensFactory_GetEventStoreQuery_WithPermission_Denied_ThrowsAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      permissions: [Permission.Read("other")]);
    var (factory, accessor) = _createFactoryWithEventStoreQuery();
    accessor.Current = context;

    // Act & Assert
    await Assert.That(() => factory.GetEventStoreQuery(ScopeFilters.Tenant, Permission.Write("events")))
      .Throws<AccessDeniedException>();
  }

  [Test]
  public async Task ScopedLensFactory_GetGlobalEventStoreQuery_UsesNoneFilterAsync() {
    // Arrange
    var (factory, _) = _createFactoryWithEventStoreQuery();

    // Act
    var query = factory.GetGlobalEventStoreQuery();

    // Assert
    var filterable = query as TestFilterableEventStoreQuery;
    await Assert.That(filterable!.AppliedFilter!.Value.Filters).IsEqualTo(ScopeFilters.None);
  }

  [Test]
  public async Task ScopedLensFactory_GetTenantEventStoreQuery_UsesTenantFilterAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123");
    var (factory, accessor) = _createFactoryWithEventStoreQuery();
    accessor.Current = context;

    // Act
    var query = factory.GetTenantEventStoreQuery();

    // Assert
    var filterable = query as TestFilterableEventStoreQuery;
    await Assert.That(filterable!.AppliedFilter!.Value.Filters).IsEqualTo(ScopeFilters.Tenant);
  }

  [Test]
  public async Task ScopedLensFactory_GetUserEventStoreQuery_UsesTenantAndUserFilterAsync() {
    // Arrange
    var context = _createScopeContext(tenantId: "tenant-123", userId: "user-456");
    var (factory, accessor) = _createFactoryWithEventStoreQuery();
    accessor.Current = context;

    // Act
    var query = factory.GetUserEventStoreQuery();

    // Assert
    var filterable = query as TestFilterableEventStoreQuery;
    await Assert.That(filterable!.AppliedFilter!.Value.Filters).IsEqualTo(ScopeFilters.Tenant | ScopeFilters.User);
  }

  [Test]
  public async Task ScopedLensFactory_GetEventStoreQuery_Unregistered_ThrowsAsync() {
    // Arrange - create factory WITHOUT registering IFilterableEventStoreQuery
    var (factory, _) = _createFactory();

    // Act & Assert
    await Assert.That(() => factory.GetEventStoreQuery(ScopeFilters.None))
      .ThrowsExactly<InvalidOperationException>();
  }

  // === Helper Methods ===

  private static (ScopedLensFactory factory, ScopeContextAccessor accessor) _createFactory(
      Action<LensOptions>? configureOptions = null) {

    var services = new ServiceCollection();
    var accessor = new ScopeContextAccessor();

    services.AddSingleton<IScopeContextAccessor>(accessor);
    services.AddSingleton<ISystemEventEmitter, NullSystemEventEmitter>();
    services.AddScoped<ITestLensQuery, TestLensQuery>();

    var lensOptions = new LensOptions();
    configureOptions?.Invoke(lensOptions);
    services.AddSingleton(lensOptions);

    var provider = services.BuildServiceProvider();
    var factory = new ScopedLensFactory(
      provider,
      accessor,
      lensOptions,
      provider.GetRequiredService<ISystemEventEmitter>());

    return (factory, accessor);
  }

  private static (ScopedLensFactory factory, ScopeContextAccessor accessor) _createFactoryWithEventStoreQuery(
      Action<LensOptions>? configureOptions = null) {

    var services = new ServiceCollection();
    var accessor = new ScopeContextAccessor();

    services.AddSingleton<IScopeContextAccessor>(accessor);
    services.AddSingleton<ISystemEventEmitter, NullSystemEventEmitter>();
    services.AddScoped<ITestLensQuery, TestLensQuery>();
    services.AddScoped<IFilterableEventStoreQuery, TestFilterableEventStoreQuery>();

    var lensOptions = new LensOptions();
    configureOptions?.Invoke(lensOptions);
    services.AddSingleton(lensOptions);

    var provider = services.BuildServiceProvider();
    var factory = new ScopedLensFactory(
      provider,
      accessor,
      lensOptions,
      provider.GetRequiredService<ISystemEventEmitter>());

    return (factory, accessor);
  }

  private static ScopeContext _createScopeContext(
      string? tenantId = null,
      string? userId = null,
      string? organizationId = null,
      string? customerId = null,
      Permission[]? permissions = null,
      SecurityPrincipalId[]? securityPrincipals = null) {

    return new ScopeContext {
      Scope = new PerspectiveScope {
        TenantId = tenantId,
        UserId = userId,
        OrganizationId = organizationId,
        CustomerId = customerId
      },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(permissions ?? []),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(securityPrincipals ?? []),
      Claims = new Dictionary<string, string>()
    };
  }

  // === Test Types ===

  public interface ITestLensQuery : ILensQuery, IFilterableLens {
    ScopeFilterInfo? AppliedFilter { get; }
  }

  private sealed class TestLensQuery : ITestLensQuery {
    public ScopeFilterInfo? AppliedFilter { get; private set; }

    public void ApplyFilter(ScopeFilterInfo filterInfo) {
      AppliedFilter = filterInfo;
    }
  }

  private sealed class NullSystemEventEmitter : ISystemEventEmitter {
    public Task EmitEventAuditedAsync<TEvent>(
        Guid streamId,
        long streamPosition,
        MessageEnvelope<TEvent> envelope,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task EmitCommandAuditedAsync<TCommand, TResponse>(
        TCommand command,
        TResponse response,
        string receptorName,
        IMessageContext? context,
        CancellationToken cancellationToken = default) where TCommand : notnull => Task.CompletedTask;

    public Task EmitAsync<TSystemEvent>(
        TSystemEvent systemEvent,
        CancellationToken cancellationToken = default) where TSystemEvent : ISystemEvent => Task.CompletedTask;

    public bool ShouldExcludeFromAudit(Type type) => false;
  }

  // Interface for testing unregistered lens scenario
  public interface IUnregisteredLensQuery : ILensQuery;

  // Scope interfaces for testing scope definition mapping
  public interface ITenantScoped;
  public interface IUserScoped;
  public interface IOrganizationScoped;
  public interface ICustomerScoped;

  // Test implementation of IFilterableEventStoreQuery for EventStoreQuery tests
  private sealed class TestFilterableEventStoreQuery : IFilterableEventStoreQuery {
    public ScopeFilterInfo? AppliedFilter { get; private set; }

    public void ApplyFilter(ScopeFilterInfo filterInfo) {
      AppliedFilter = filterInfo;
    }

    public IQueryable<EventStoreRecord> Query => Array.Empty<EventStoreRecord>().AsQueryable();

    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => Array.Empty<EventStoreRecord>().AsQueryable();

    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => Array.Empty<EventStoreRecord>().AsQueryable();
  }
}
