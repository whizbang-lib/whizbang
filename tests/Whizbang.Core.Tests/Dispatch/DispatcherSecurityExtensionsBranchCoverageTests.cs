// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatch;

/// <summary>
/// Branch coverage for <see cref="DispatcherSecurityExtensions.AsSystem"/> and
/// <see cref="DispatcherSecurityExtensions.RunAs"/> when the ambient scope context is an
/// implementation that reports no scope at all. Both read the user and tenant through the scope,
/// so such a context must read as "no user, no tenant", not fail with a null dereference.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/DispatcherSecurityExtensions.cs</code-under-test>
[Category("Security")]
[Category("Dispatcher")]
[NotInParallel]
public class DispatcherSecurityExtensionsBranchCoverageTests {

  [Test]
  public async Task AsSystem_AmbientContextWithoutScope_HasNoActualPrincipalAndNoTenantToKeepAsync() {
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    await using var provider = _provider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var previous = ScopeContextAccessor.CurrentContext;
    ScopeContextAccessor.CurrentContext = new ScopelessContext();
    try {
      await Assert.That(() => dispatcher.AsSystem().KeepTenant()).Throws<InvalidOperationException>()
        .Because("a context with no scope has no ambient tenant, so there is nothing to keep");

      await dispatcher.AsSystem().ForAllTenants().SendAsync(new DispatcherSecurityBuilderTestCommand("payload"));
    } finally {
      ScopeContextAccessor.CurrentContext = previous;
    }

    var captured = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(captured).IsNotNull();
    await Assert.That(captured!.ActualPrincipal).IsNull()
      .Because("with no scope there is no originating user to record as the actual principal");
    await Assert.That(captured.EffectivePrincipal).IsEqualTo("SYSTEM");
  }

  [Test]
  public async Task RunAs_AmbientContextWithoutScope_HasNoActualPrincipalAndNoTenantToKeepAsync() {
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    await using var provider = _provider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var previous = ScopeContextAccessor.CurrentContext;
    ScopeContextAccessor.CurrentContext = new ScopelessContext();
    try {
      await Assert.That(() => dispatcher.RunAs("target-user").KeepTenant()).Throws<InvalidOperationException>()
        .Because("a context with no scope has no ambient tenant, so there is nothing to keep");

      await dispatcher.RunAs("target-user").ForAllTenants().SendAsync(new DispatcherSecurityBuilderTestCommand("payload"));
    } finally {
      ScopeContextAccessor.CurrentContext = previous;
    }

    var captured = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(captured).IsNotNull();
    await Assert.That(captured!.ActualPrincipal).IsNull()
      .Because("with no scope there is no originating user to record as the actual principal");
    await Assert.That(captured.EffectivePrincipal).IsEqualTo("target-user");
  }

  private static ServiceProvider _provider() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    services.AddSingleton<ITraceStore>(new InMemoryTraceStore());
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }

  /// <summary>An <see cref="IScopeContext"/> implementation that reports no scope.</summary>
  private sealed class ScopelessContext : IScopeContext {
    public PerspectiveScope Scope => null!;
    public IReadOnlySet<string> Roles => new HashSet<string>();
    public IReadOnlySet<Permission> Permissions => new HashSet<Permission>();
    public IReadOnlySet<SecurityPrincipalId> SecurityPrincipals => new HashSet<SecurityPrincipalId>();
    public IReadOnlyDictionary<string, string> Claims => new Dictionary<string, string>();
    public string? ActualPrincipal => null;
    public string? EffectivePrincipal => null;
    public SecurityContextType ContextType => SecurityContextType.User;
    public bool HasPermission(Permission permission) => false;
    public bool HasAnyPermission(params Permission[] permissions) => false;
    public bool HasAllPermissions(params Permission[] permissions) => false;
    public bool HasRole(string roleName) => false;
    public bool HasAnyRole(params string[] roleNames) => false;
    public bool IsMemberOfAny(params SecurityPrincipalId[] principals) => false;
    public bool IsMemberOfAll(params SecurityPrincipalId[] principals) => false;
  }
}
