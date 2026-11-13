// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Registry;

namespace Whizbang.Core.Tests.ServiceScope;

/// <summary>
/// #2283: a service assembly that loads after a host is anchored is classified when its facts register, by the same
/// rule as at anchor time. A host computed its scope once, from the assemblies already loaded, so a later-loaded
/// assembly was in neither the included nor the excluded set and counted as the host's own: another service's receptors
/// and consumed types counted as this host's.
/// </summary>
/// <code-under-test>src/Whizbang.Core/ServiceAssemblyScope.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/WhizbangServiceAssemblies.cs</code-under-test>
[NotInParallel(["WhizbangBackgroundServiceTests", "ReceptorRegistryQueryAggregation"])]
public class LateLoadedServiceAssemblyTests {
  private const string BILLING_EVENT = "Billing.InvoiceIssued";
  private const string ORDERS_EVENT = "Orders.OrderPlaced";

  [Before(Test)]
  public void ResetRegistry() {
    AssemblyRegistry<ReceptorRegistryContribution>.ClearForTesting();
    WhizbangReceptorRegistryQuery.ClearCacheForTesting();
  }

  [After(Test)]
  public void TeardownRegistry() => ResetRegistry();

  private static ServiceCollection _anchoredOn(string assembly) {
    var services = new ServiceCollection();
    services.AddWhizbangServiceAnchor(assembly);
    return services;
  }

  [Test]
  public async Task AServiceRootThatRegistersAfterTheHostIsAnchored_IsExcludedAsync() {
    using var _ = WhizbangServiceAssemblies.ReplaceForTesting(
      new ServiceAssemblyFacts("Shared.Contracts", false, []),
      new ServiceAssemblyFacts("Orders", true, ["Shared.Contracts"]));
    var scope = ServiceAssemblyScope.For(_anchoredOn("Orders"));

    WhizbangServiceAssemblies.Register("Billing", true, ["Shared.Contracts"]);

    await Assert.That(scope.Includes("Billing")).IsFalse()
      .Because("another service's root is another service's whenever it loads; anchor time is not a boundary");
    await Assert.That(scope.Includes("Orders")).IsTrue();
    await Assert.That(scope.Includes("Shared.Contracts")).IsTrue()
      .Because("the host's own assemblies stay its own when a service built on them loads later");
  }

  [Test]
  public async Task AnAssemblyOnlyALateRootIsBuiltOn_IsExcludedWithItAsync() {
    using var _ = WhizbangServiceAssemblies.ReplaceForTesting(new ServiceAssemblyFacts("Orders", true, []));
    var scope = ServiceAssemblyScope.For(_anchoredOn("Orders"));

    WhizbangServiceAssemblies.Register("Billing.Domain", false, []);
    WhizbangServiceAssemblies.Register("Billing", true, ["Billing.Domain"]);

    await Assert.That(scope.Includes("Billing")).IsFalse();
    await Assert.That(scope.Includes("Billing.Domain")).IsFalse()
      .Because("an assembly only another service is built on is that service's, as at anchor time");
  }

  [Test]
  public async Task ALateAssemblyTheAnchorIsBuiltOn_AndALateHelperNoServiceIsBuiltOn_StayAsync() {
    using var _ = WhizbangServiceAssemblies.ReplaceForTesting(new ServiceAssemblyFacts("Orders", true, ["Orders.Domain"]));
    var scope = ServiceAssemblyScope.For(_anchoredOn("Orders"));

    WhizbangServiceAssemblies.Register("Orders.Domain", false, []);
    WhizbangServiceAssemblies.Register("Diagnostics.Helpers", false, []);

    await Assert.That(scope.Includes("Orders.Domain")).IsTrue()
      .Because("an assembly the anchor is built on is the host's own, whenever it loads");
    await Assert.That(scope.Includes("Diagnostics.Helpers")).IsTrue()
      .Because("an assembly no other service is built on is not known to be another service's");
  }

  [Test]
  public async Task AnUnscopedHost_KeepsALateServiceRootAsync() {
    using var _ = WhizbangServiceAssemblies.ReplaceForTesting(new ServiceAssemblyFacts("Orders", true, []));
    var scope = ServiceAssemblyScope.For(new ServiceCollection());

    WhizbangServiceAssemblies.Register("Billing", true, []);

    await Assert.That(scope.Includes("Billing")).IsTrue()
      .Because("a host that names no service keeps every assembly, as before");
  }

  [Test]
  public async Task TheHostsScope_DoesNotCountALateServicesReceptorsOrConsumedTypesAsync() {
    using var _ = WhizbangServiceAssemblies.ReplaceForTesting(new ServiceAssemblyFacts("Orders", true, []));
    var services = _anchoredOn("Orders");
    services.AddServiceAssemblyScope();
    await using var provider = services.BuildServiceProvider();
    var query = new WhizbangReceptorRegistryQueryAdapter(
      NullReceptorRegistry.Instance, provider.GetRequiredService<ServiceAssemblyScope>());
    _contribute("Orders", ORDERS_EVENT);
    await Assert.That(query.HasAnyConsumer(ORDERS_EVENT)).IsTrue();

    // A second service's code loads after the host started: its facts register, then its receptors contribute.
    WhizbangServiceAssemblies.Register("Billing", true, []);
    _contribute("Billing", BILLING_EVENT);

    await Assert.That(query.HasAnyConsumer(BILLING_EVENT)).IsFalse()
      .Because("the host would otherwise run another service's receptor in place of routing the message");
    await Assert.That(query.HasInboxHandler(BILLING_EVENT)).IsFalse();
    await Assert.That(query.HasReceptors(LifecycleStage.PostInboxInline, BILLING_EVENT)).IsFalse();
    await Assert.That(query.GetConsumedMessages().Select(m => m.TypeName)).IsEquivalentTo([ORDERS_EVENT])
      .Because("another service's consumed types are not this host's");
  }

  private static void _contribute(string assembly, string type) =>
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AssemblyName = assembly,
      AnyConsumerTypes = [type],
      InboxHandlerTypes = [type],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>> { [LifecycleStage.PostInboxInline] = [type] },
      HandledMessages = [new HandledMessageInfo(type, "contracts", Whizbang.Core.Routing.MessageKind.Event)],
      ConsumedMessages = [new ConsumedMessageInfo(type, true)],
    });
}
