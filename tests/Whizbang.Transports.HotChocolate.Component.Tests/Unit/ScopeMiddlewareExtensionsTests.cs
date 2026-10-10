// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Security;
using Whizbang.Transports.HotChocolate.Middleware;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="ScopeMiddlewareExtensions"/>.
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
  public async Task AddWhizbangScope_ShouldRegisterAsScopeContextAccessorAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangScope();
    var provider = services.BuildServiceProvider();

    // Assert - Now uses Core's ScopeContextAccessor (with static AsyncLocal)
    var accessor = provider.GetRequiredService<IScopeContextAccessor>();
    await Assert.That(accessor).IsTypeOf<ScopeContextAccessor>();
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
    services.AddWhizbangScope(options => options.TenantIdClaimType = "custom_tenant");
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
    services.AddWhizbangScope(options => options.TenantIdClaimType = "my_tenant");
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
    var result = services.AddWhizbangScope(options => options.TenantIdClaimType = "test");

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
    var result = app.UseWhizbangScope(options => options.TenantIdClaimType = "custom");

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

  [Test]
  public async Task UseWhizbangScope_DefaultPipeline_AnonymousRequestCannotNameItsOwnIdentityAsync() {
    // Arrange: the turnkey pipeline, an endpoint that reads the scope the way a resolver would
    var services = new ServiceCollection();
    services.AddWhizbangScope();
    await using var provider = services.BuildServiceProvider();
    var app = new ApplicationBuilder(provider);
    app.UseWhizbangScope();
    IScopeContext? seen = null;
    app.Run(ctx => { seen = ctx.RequestServices.GetRequiredService<IScopeContextAccessor>().Current; return Task.CompletedTask; });
    var pipeline = app.Build();

    var context = new DefaultHttpContext { RequestServices = provider };
    context.Request.Headers["X-Tenant-Id"] = "victim-tenant";
    context.Request.Headers["X-User-Id"] = "victim-user";
    context.Request.Headers["X-Organization-Id"] = "victim-org";
    context.Request.Headers["X-Customer-Id"] = "victim-customer";

    // Act
    await pipeline(context);

    // Assert: what reaches the endpoint carries no identity the caller wrote
    await Assert.That(seen).IsNotNull();
    await Assert.That(seen!.Scope.TenantId).IsNull();
    await Assert.That(seen.Scope.UserId).IsNull();
    await Assert.That(seen.Scope.OrganizationId).IsNull();
    await Assert.That(seen.Scope.CustomerId).IsNull();
  }

  [Test]
  public async Task UseWhizbangScope_HeaderOptedIn_ReadsThatHeaderAsync() {
    // Arrange: the same pipeline, with a gateway-set header named explicitly
    var services = new ServiceCollection();
    services.AddWhizbangScope();
    await using var provider = services.BuildServiceProvider();
    var app = new ApplicationBuilder(provider);
    app.UseWhizbangScope(options => options.TenantIdHeaderName = "X-Gateway-Tenant");
    IScopeContext? seen = null;
    app.Run(ctx => { seen = ctx.RequestServices.GetRequiredService<IScopeContextAccessor>().Current; return Task.CompletedTask; });
    var pipeline = app.Build();

    var context = new DefaultHttpContext { RequestServices = provider };
    context.Request.Headers["X-Gateway-Tenant"] = "gateway-tenant";

    // Act
    await pipeline(context);

    // Assert
    await Assert.That(seen!.Scope.TenantId).IsEqualTo("gateway-tenant");
  }

  #endregion

  #region ScopeContextAccessor

  [Test]
  public async Task ScopeContextAccessor_Current_ShouldBeNullByDefaultAsync() {
    // Arrange - Clear any existing context first (static AsyncLocal)
    ScopeContextAccessor.CurrentContext = null;
    var accessor = new ScopeContextAccessor();

    // Assert
    await Assert.That(accessor.Current).IsNull();
  }

  [Test]
  public async Task ScopeContextAccessor_Current_ShouldGetAndSetValueAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var scopeContext = _createSimpleScopeContext();

    // Act
    accessor.Current = scopeContext;

    // Assert
    await Assert.That(accessor.Current).IsSameReferenceAs(scopeContext);

    // Cleanup
    accessor.Current = null;
  }

  [Test]
  public async Task ScopeContextAccessor_ShouldIsolateAcrossAsyncFlowsAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
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

    // Cleanup
    accessor.Current = null;
  }

  [Test]
  public async Task ScopeContextAccessor_ShouldAllowSettingToNullAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor {
      Current = _createSimpleScopeContext()
    };

    // Act
    accessor.Current = null;

    // Assert
    await Assert.That(accessor.Current).IsNull();
  }

  [Test]
  public async Task ScopeContextAccessor_StaticAndInstance_ShouldShareStateAsync() {
    // This test verifies that static CurrentContext and instance Current
    // share the same underlying AsyncLocal storage - critical for Dispatcher compatibility
    var accessor = new ScopeContextAccessor();
    var scopeContext = _createSimpleScopeContext();

    // Act - set via instance
    accessor.Current = scopeContext;

    // Assert - should be readable via static accessor
    await Assert.That(ScopeContextAccessor.CurrentContext).IsSameReferenceAs(scopeContext);

    // Act - set via static
    var scopeContext2 = _createSimpleScopeContext();
    ScopeContextAccessor.CurrentContext = scopeContext2;

    // Assert - should be readable via instance
    await Assert.That(accessor.Current).IsSameReferenceAs(scopeContext2);

    // Cleanup
    accessor.Current = null;
  }

  #endregion

  #region Helpers

  private static ImmutableScopeContext _createSimpleScopeContext() {
    var extraction = new SecurityExtraction {
      Scope = new Core.Lenses.PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Core.Security.Permission>(),
      SecurityPrincipals = new HashSet<Core.Security.SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    return new ImmutableScopeContext(extraction, shouldPropagate: true);
  }

  #endregion
}
