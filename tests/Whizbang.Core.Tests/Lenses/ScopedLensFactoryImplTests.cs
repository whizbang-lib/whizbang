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
  // === GetLens with ScopeFilter Tests ===

  [Test]
  public async Task ScopedLensFactory_GetLens_None_ReturnsLensAsync() {
    // Arrange
    var (factory, _) = _createFactory();

    // Act
    var lens = factory.GetLens<ITestLensQuery>(ScopeFilter.None);

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
    var lens = factory.GetLens<ITestLensQuery>(ScopeFilter.Tenant);

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
    var lens = factory.GetLens<ITestLensQuery>(ScopeFilter.Tenant | ScopeFilter.User);

    // Assert
    await Assert.That(lens.AppliedFilter!.Value.TenantId).IsEqualTo("tenant-123");
    await Assert.That(lens.AppliedFilter!.Value.UserId).IsEqualTo("user-456");
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_Principal_UsesSecurityPrincipalsAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      securityPrincipals: new[] {
        SecurityPrincipalId.User("user-456"),
        SecurityPrincipalId.Group("developers")
      });
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>(ScopeFilter.Tenant | ScopeFilter.Principal);

    // Assert
    await Assert.That(lens.AppliedFilter!.Value.SecurityPrincipals.Count).IsEqualTo(2);
  }

  // === GetLens with Permission Tests ===

  [Test]
  public async Task ScopedLensFactory_GetLens_WithPermission_Granted_ReturnsLensAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      permissions: new[] { Permission.Read("orders") });
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>(ScopeFilter.Tenant, Permission.Read("orders"));

    // Assert
    await Assert.That(lens).IsNotNull();
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_WithPermission_Denied_ThrowsAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      permissions: new[] { Permission.Read("orders") });
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act & Assert
    await Assert.That(() => factory.GetLens<ITestLensQuery>(
      ScopeFilter.Tenant, Permission.Delete("orders")))
      .Throws<AccessDeniedException>();
  }

  [Test]
  public async Task ScopedLensFactory_GetLens_WithAnyPermission_OneMatch_ReturnsLensAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      permissions: new[] { Permission.Write("orders") });
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetLens<ITestLensQuery>(
      ScopeFilter.Tenant,
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
      permissions: new[] { Permission.Read("customers") });
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act & Assert
    await Assert.That(() => factory.GetLens<ITestLensQuery>(
      ScopeFilter.Tenant,
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
    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(ScopeFilter.None);
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
    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(ScopeFilter.Tenant);
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
    var expectedFilters = ScopeFilter.Tenant | ScopeFilter.User;
    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(expectedFilters);
  }

  [Test]
  public async Task ScopedLensFactory_GetPrincipalLens_UsesTenantAndPrincipalAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      securityPrincipals: new[] { SecurityPrincipalId.Group("team") });
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetPrincipalLens<ITestLensQuery>();

    // Assert
    var expectedFilters = ScopeFilter.Tenant | ScopeFilter.Principal;
    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(expectedFilters);
  }

  [Test]
  public async Task ScopedLensFactory_GetMyOrSharedLens_UsesOrLogicAsync() {
    // Arrange
    var context = _createScopeContext(
      tenantId: "tenant-123",
      userId: "user-456",
      securityPrincipals: new[] { SecurityPrincipalId.Group("team") });
    var (factory, accessor) = _createFactory();
    accessor.Current = context;

    // Act
    var lens = factory.GetMyOrSharedLens<ITestLensQuery>();

    // Assert
    await Assert.That(lens.AppliedFilter!.Value.UseOrLogicForUserAndPrincipal).IsTrue();
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

  private static ScopeContext _createScopeContext(
      string? tenantId = null,
      string? userId = null,
      Permission[]? permissions = null,
      SecurityPrincipalId[]? securityPrincipals = null) {

    return new ScopeContext {
      Scope = new PerspectiveScope {
        TenantId = tenantId,
        UserId = userId
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
}
