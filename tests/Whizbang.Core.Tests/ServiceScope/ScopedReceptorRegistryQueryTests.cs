// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Registry;

namespace Whizbang.Core.Tests.ServiceScope;

/// <summary>
/// A host's receptor registry query counts only its own service's generated receptors (#2243): another service's
/// receptor in the same process never makes this host consume, subscribe to or fire for its message.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Generated/WhizbangReceptorRegistryQuery.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/WhizbangReceptorRegistryQueryAdapter.cs</code-under-test>
[NotInParallel("ReceptorRegistryQueryAggregation")]
public class ScopedReceptorRegistryQueryTests {
  private const string ORDERS_EVENT = "Orders.OrderPlaced";
  private const string BILLING_EVENT = "Billing.InvoiceIssued";

  private static readonly ServiceAssemblyScope _orders = ServiceAssemblyScope.Compute(["Orders"], new Dictionary<string, ServiceAssemblyFacts> {
    ["Orders"] = new("Orders", true, []),
    ["Billing"] = new("Billing", true, []),
  });

  [Before(Test)]
  public void ResetRegistry() {
    AssemblyRegistry<ReceptorRegistryContribution>.ClearForTesting();
    WhizbangReceptorRegistryQuery.ClearCacheForTesting();
  }

  [After(Test)]
  public void TeardownRegistry() => ResetRegistry();

  private static void _contribute(string? assembly, string type) =>
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AssemblyName = assembly,
      AnyConsumerTypes = [type],
      InboxHandlerTypes = [type],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>> { [LifecycleStage.PostInboxInline] = [type] },
      HandledMessages = [new HandledMessageInfo(type, "contracts", Whizbang.Core.Routing.MessageKind.Event)],
      ConsumedMessages = [new ConsumedMessageInfo(type, true)],
    });

  [Test]
  public async Task AScopedHost_AnswersForItsOwnServicesReceptorsOnlyAsync() {
    _contribute("Orders", ORDERS_EVENT);
    _contribute("Billing", BILLING_EVENT);
    var query = new WhizbangReceptorRegistryQueryAdapter(NullReceptorRegistry.Instance, _orders);

    await Assert.That(query.HasAnyConsumer(ORDERS_EVENT)).IsTrue();
    await Assert.That(query.HasAnyConsumer(BILLING_EVENT)).IsFalse()
      .Because("another service's receptor must not make this host consume the message");
    await Assert.That(query.HasInboxHandler(BILLING_EVENT)).IsFalse();
    await Assert.That(query.HasReceptors(LifecycleStage.PostInboxInline, BILLING_EVENT)).IsFalse();
    await Assert.That(query.HasReceptors(LifecycleStage.PostInboxInline, ORDERS_EVENT)).IsTrue();
    await Assert.That(query.GetHandledMessages().Select(m => m.MessageTypeName)).IsEquivalentTo([ORDERS_EVENT]);
    await Assert.That(query.GetConsumedMessages().Select(m => m.TypeName)).IsEquivalentTo([ORDERS_EVENT]);
    await Assert.That(query.FindConsumedMessage(BILLING_EVENT)).IsNull();
    await Assert.That(query.FindConsumedMessage(ORDERS_EVENT)).IsNotNull();
  }

  [Test]
  public async Task AnUnscopedHost_AndAContributionWithNoAssembly_CountAsBeforeAsync() {
    _contribute("Billing", BILLING_EVENT);
    _contribute(null, ORDERS_EVENT);
    var unscoped = new WhizbangReceptorRegistryQueryAdapter(NullReceptorRegistry.Instance, ServiceAssemblyScope.Unscoped);
    var scoped = new WhizbangReceptorRegistryQueryAdapter(NullReceptorRegistry.Instance, _orders);

    await Assert.That(unscoped.HasAnyConsumer(BILLING_EVENT)).IsTrue();
    await Assert.That(scoped.HasAnyConsumer(ORDERS_EVENT)).IsTrue()
      .Because("a contribution built without an assembly name is not known to be another service's");
  }

  [Test]
  public async Task AContributionArrivingLater_IsSeenByTheScopedViewAsync() {
    var query = new ScopedReceptorRegistryQuery(_orders);
    await Assert.That(query.HasAnyConsumer(ORDERS_EVENT)).IsFalse();

    _contribute("Orders", ORDERS_EVENT);

    await Assert.That(query.HasAnyConsumer(ORDERS_EVENT)).IsTrue()
      .Because("the view is rebuilt when the registry's contribution count changes, as the process-wide one is");
    await Assert.That(query.HasAnyConsumer(ORDERS_EVENT)).IsTrue();
  }

  [Test]
  public async Task AScopedHost_AnswersForItsOwnServicesCollectiveHandlersOnlyAsync() {
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AssemblyName = "Billing",
      AnyConsumerTypes = [],
      InboxHandlerTypes = [],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>>(),
      HandledCollectiveEventTypes = ["Billing.InvoicesRenamed, Billing"],
    });
    var query = new WhizbangReceptorRegistryQueryAdapter(NullReceptorRegistry.Instance, _orders);

    await Assert.That(query.GetHandledCollectiveEventTypes()).IsNull()
      .Because("with none of its own service's contributions loaded, the host cannot know its handlers (#2145)");

    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AssemblyName = "Orders",
      AnyConsumerTypes = [],
      InboxHandlerTypes = [],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>>(),
      HandledCollectiveEventTypes = ["Orders.OrdersRenamed, Orders"],
    });

    await Assert.That(query.GetHandledCollectiveEventTypes()).IsEquivalentTo(["Orders.OrdersRenamed, Orders"])
      .Because("another service's collective handlers are not this host's");
  }
}
