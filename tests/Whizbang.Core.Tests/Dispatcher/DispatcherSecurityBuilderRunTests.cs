// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// <see cref="DispatcherSecurityBuilder.RunAsync{TResult}"/>: background work that reads as well as
/// publishes runs in a chosen tenant's context, then leaves the caller's context as it found it.
/// </summary>
/// <remarks>
/// A maintenance sweep has no request of its own. Its reads go through tenant-scoped lenses, which
/// refuse to run without an ambient tenant; publishing in the tenant is not enough when the reads that
/// decide whether to publish come first.
/// </remarks>
[NotInParallel("ScopeContextAccessor")]
public class DispatcherSecurityBuilderRunTests {

  [Before(Test)]
  public Task ResetAsync() {
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
    return Task.CompletedTask;
  }

  private static IDispatcher _dispatcher() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }

  [Test]
  public async Task RunAsync_ForTenant_WorkSeesThatTenantAsTheSystemAsync() {
    var dispatcher = _dispatcher();
    var accessor = new ScopeContextAccessor();

    var seen = await dispatcher.AsSystem().ForTenant("tenant-a")
      .RunAsync(_ => Task.FromResult(accessor.Current?.Scope));

    await Assert.That(seen?.TenantId).IsEqualTo("tenant-a")
      .Because("a tenant-scoped lens reads the ambient scope through the accessor; it must see the tenant the work runs for");
    await Assert.That(seen?.UserId).IsEqualTo("SYSTEM");
  }

  [Test]
  public async Task RunAsync_ExplicitContextTakesPrecedenceOverTheInitiatingContextAsync() {
    var dispatcher = _dispatcher();
    var accessor = new ScopeContextAccessor();
    ScopeContextAccessor.CurrentInitiatingContext = new MessageContext {
      ScopeContext = new ScopeContext {
        Scope = new PerspectiveScope { TenantId = "tenant-other" },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>(),
      },
    };

    var seen = await dispatcher.AsSystem().ForTenant("tenant-a")
      .RunAsync(_ => Task.FromResult(accessor.Current?.Scope?.TenantId));

    await Assert.That(seen).IsEqualTo("tenant-a")
      .Because("the accessor reads the initiating context first; left in place it would run the work in the wrong tenant");
  }

  [Test]
  public async Task RunAsync_PassesTheCancellationTokenToTheWorkAsync() {
    var dispatcher = _dispatcher();
    using var cts = new CancellationTokenSource();

    var received = await dispatcher.AsSystem().ForTenant("tenant-a")
      .RunAsync(ct => Task.FromResult(ct), cts.Token);

    await Assert.That(received).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task RunAsync_AfterTheWork_TheCallersContextIsRestoredAsync() {
    var dispatcher = _dispatcher();

    await dispatcher.AsSystem().ForTenant("tenant-a").RunAsync(_ => Task.FromResult(0));

    await Assert.That(ScopeContextAccessor.CurrentContext).IsNull()
      .Because("the explicit context is for this work only; leaking it would run the worker's next item as that tenant");
  }

  [Test]
  public async Task RunAsync_WorkThrows_TheCallersContextIsStillRestoredAsync() {
    var dispatcher = _dispatcher();

    await Assert.That(async () => await dispatcher.AsSystem().ForTenant("tenant-a")
      .RunAsync<int>(_ => throw new InvalidOperationException("boom")))
      .Throws<InvalidOperationException>();

    await Assert.That(ScopeContextAccessor.CurrentContext).IsNull();
  }

  [Test]
  public async Task RunAsync_NullWork_ThrowsAsync() {
    var dispatcher = _dispatcher();

    await Assert.That(async () => await dispatcher.AsSystem().ForTenant("tenant-a").RunAsync<int>(null!))
      .Throws<ArgumentNullException>();
  }
}
