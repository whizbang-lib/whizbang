using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Security;
using Whizbang.Transports.HotChocolate.Middleware;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="ScopeMiddlewareExtensions"/> and <see cref="AsyncLocalScopeContextAccessor"/>.
/// Verifies service registration and middleware pipeline configuration.
/// </summary>
/// <tests>src/Whizbang.Transports.HotChocolate/Middleware/ScopeMiddlewareExtensions.cs</tests>
public class ScopeMiddlewareExtensionsTests {
  #region AddWhizbangScope - No Configuration

  [Test]
  public async Task AddWhizbangScope_ShouldRegisterIScopeContextAccessorAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangScope();
    var provider = services.BuildServiceProvider();

    // Assert
    var accessor = provider.GetService<IScopeContextAccessor>();
    await Assert.That(accessor).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangScope_ShouldRegisterAsAsyncLocalScopeContextAccessorAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangScope();
    var provider = services.BuildServiceProvider();

    // Assert
    var accessor = provider.GetRequiredService<IScopeContextAccessor>();
    await Assert.That(accessor).IsTypeOf<AsyncLocalScopeContextAccessor>();
  }

  [Test]
  public async Task AddWhizbangScope_ShouldReturnServiceCollectionForChainingAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var result = services.AddWhizbangScope();

    // Assert
    await Assert.That(result).IsSameReferenceAs(services);
  }

  #endregion

  #region AddWhizbangScope - With Configuration

  [Test]
  public async Task AddWhizbangScope_WithConfigure_ShouldRegisterIScopeContextAccessorAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangScope(options => {
      options.TenantIdClaimType = "custom_tenant";
    });
    var provider = services.BuildServiceProvider();

    // Assert
    var accessor = provider.GetService<IScopeContextAccessor>();
    await Assert.That(accessor).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangScope_WithConfigure_ShouldRegisterOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangScope(options => {
      options.TenantIdClaimType = "my_tenant";
    });
    var provider = services.BuildServiceProvider();

    // Assert
    var options = provider.GetService<WhizbangScopeOptions>();
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.TenantIdClaimType).IsEqualTo("my_tenant");
  }

  [Test]
  public async Task AddWhizbangScope_WithConfigure_ShouldReturnServiceCollectionForChainingAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var result = services.AddWhizbangScope(options => {
      options.TenantIdClaimType = "test";
    });

    // Assert
    await Assert.That(result).IsSameReferenceAs(services);
  }

  #endregion

  #region UseWhizbangScope

  [Test]
  public async Task UseWhizbangScope_ShouldReturnApplicationBuilderForChainingAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangScope();
    var app = new ApplicationBuilder(services.BuildServiceProvider());

    // Act
    var result = app.UseWhizbangScope();

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task UseWhizbangScope_WithConfigure_ShouldReturnApplicationBuilderForChainingAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangScope();
    var app = new ApplicationBuilder(services.BuildServiceProvider());

    // Act
    var result = app.UseWhizbangScope(options => {
      options.TenantIdClaimType = "custom";
    });

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task UseWhizbangScope_WithConfigure_ShouldApplyConfigurationAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangScope();
    var provider = services.BuildServiceProvider();
    var app = new ApplicationBuilder(provider);
    var configureApplied = false;

    // Act
    app.UseWhizbangScope(options => {
      configureApplied = true;
      options.TenantIdClaimType = "custom";
    });

    // Assert
    await Assert.That(configureApplied).IsTrue();
  }

  #endregion

  #region AsyncLocalScopeContextAccessor

  [Test]
  public async Task AsyncLocalScopeContextAccessor_Current_ShouldBeNullByDefaultAsync() {
    // Arrange
    var accessor = new AsyncLocalScopeContextAccessor();

    // Assert
    await Assert.That(accessor.Current).IsNull();
  }

  [Test]
  public async Task AsyncLocalScopeContextAccessor_Current_ShouldGetAndSetValueAsync() {
    // Arrange
    var accessor = new AsyncLocalScopeContextAccessor();
    var scopeContext = _createSimpleScopeContext();

    // Act
    accessor.Current = scopeContext;

    // Assert
    await Assert.That(accessor.Current).IsSameReferenceAs(scopeContext);
  }

  [Test]
  public async Task AsyncLocalScopeContextAccessor_ShouldIsolateAcrossAsyncFlowsAsync() {
    // Arrange
    var accessor = new AsyncLocalScopeContextAccessor();
    var scopeContext1 = _createSimpleScopeContext();
    var scopeContext2 = _createSimpleScopeContext();

    // Act - set value in one async flow, verify isolation
    IScopeContext? capturedInTask = null;
    accessor.Current = scopeContext1;

    await Task.Run(() => {
      // AsyncLocal flows into child tasks but can be overwritten independently
      capturedInTask = accessor.Current;
      accessor.Current = scopeContext2;
    });

    // Assert - the parent flow should still see scopeContext1
    await Assert.That(accessor.Current).IsSameReferenceAs(scopeContext1);
    // The child saw the parent's value (AsyncLocal flows down)
    await Assert.That(capturedInTask).IsSameReferenceAs(scopeContext1);
  }

  [Test]
  public async Task AsyncLocalScopeContextAccessor_ShouldAllowSettingToNullAsync() {
    // Arrange
    var accessor = new AsyncLocalScopeContextAccessor();
    accessor.Current = _createSimpleScopeContext();

    // Act
    accessor.Current = null;

    // Assert
    await Assert.That(accessor.Current).IsNull();
  }

  #endregion

  #region Helpers

  private static RequestScopeContext _createSimpleScopeContext() {
    return new RequestScopeContext {
      Scope = new Core.Lenses.PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Core.Security.Permission>(),
      SecurityPrincipals = new HashSet<Core.Security.SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
  }

  #endregion
}
