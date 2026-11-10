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

namespace Whizbang.Core.Tests.Generated;

/// <summary>
/// The consumer registry answers which collective event types a [CollectiveApplyFor] handler on this service applies
/// (#2145), unioned across every loaded assembly's contribution. The startup association writer and the perspective
/// drain both read this one answer, so they cannot disagree. An answer the registry cannot give (no contribution, or a
/// contribution an older generator built) is unknown, never empty: an unknown answer gates nothing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Generated/WhizbangReceptorRegistryQuery.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/WhizbangReceptorRegistryQueryAdapter.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IConsumerRegistry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ConsumedMessageTypes.cs</code-under-test>
[NotInParallel("ReceptorRegistryQueryAggregation")]
public class WhizbangReceptorRegistryQueryHandledCollectiveTests {

  [Before(Test)]
  public void ResetRegistry() {
    AssemblyRegistry<ReceptorRegistryContribution>.ClearForTesting();
    WhizbangReceptorRegistryQuery.ClearCacheForTesting();
  }

  [After(Test)]
  public void TeardownRegistry() => ResetRegistry();

  [Test]
  public async Task TwoAssemblies_HandledCollectiveEventTypes_AreTheUnion_DeduplicatedAndSortedAsync() {
    // A host whose collective handlers live in two assemblies handles the types of both.
    AssemblyRegistry<ReceptorRegistryContribution>.Register(_contribution("Jobs.Archive, Jobs", "Shared.Purge, Shared"));
    AssemblyRegistry<ReceptorRegistryContribution>.Register(_contribution("Shared.Purge, Shared", "Billing.Close, Billing"));

    var handled = WhizbangReceptorRegistryQuery.GetHandledCollectiveEventTypes();

    await Assert.That(handled).IsNotNull();
    await Assert.That(handled).IsEquivalentTo(
      ["Billing.Close, Billing", "Jobs.Archive, Jobs", "Shared.Purge, Shared"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("every assembly's handlers count, a type two assemblies handle is listed once, and the order is deterministic");
  }

  [Test]
  public async Task AnAssemblyWithNoHandlers_ContributesAnEmptyList_AndTheAnswerIsStillKnownAsync() {
    AssemblyRegistry<ReceptorRegistryContribution>.Register(_contribution("Jobs.Archive, Jobs"));
    AssemblyRegistry<ReceptorRegistryContribution>.Register(_contribution());

    await Assert.That(WhizbangReceptorRegistryQuery.GetHandledCollectiveEventTypes()).IsEquivalentTo(["Jobs.Archive, Jobs"]);
  }

  [Test]
  public async Task AContributionThatPredatesTheList_MakesTheAnswerUnknownAsync() {
    // An assembly built by a generator that did not emit the list may hold handlers nobody can see. Gating on the
    // others' answer would drop that assembly's work, so the whole answer is unknown.
    AssemblyRegistry<ReceptorRegistryContribution>.Register(_contribution("Jobs.Archive, Jobs"));
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AnyConsumerTypes = [],
      InboxHandlerTypes = [],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>>(),
    });

    await Assert.That(WhizbangReceptorRegistryQuery.GetHandledCollectiveEventTypes()).IsNull();
  }

  [Test]
  public async Task NoContributionAtAll_IsUnknown_NotEmptyAsync() {
    await Assert.That(WhizbangReceptorRegistryQuery.GetHandledCollectiveEventTypes()).IsNull()
      .Because("a registry nothing has populated knows nothing, and must not read as 'no handler here'");
  }

  [Test]
  public async Task AContributionRegisteredAfterTheFirstQuery_IsCountedAsync() {
    AssemblyRegistry<ReceptorRegistryContribution>.Register(_contribution("Jobs.Archive, Jobs"));
    _ = WhizbangReceptorRegistryQuery.GetHandledCollectiveEventTypes();
    AssemblyRegistry<ReceptorRegistryContribution>.Register(_contribution("Billing.Close, Billing"));

    await Assert.That(WhizbangReceptorRegistryQuery.GetHandledCollectiveEventTypes())
      .IsEquivalentTo(["Billing.Close, Billing", "Jobs.Archive, Jobs"]);
  }

  [Test]
  public async Task Adapter_GetHandledCollectiveEventTypes_ReadsTheGeneratedRegistryAsync() {
    AssemblyRegistry<ReceptorRegistryContribution>.Register(_contribution("Jobs.Archive, Jobs"));
#pragma warning disable CA1859 // asked through the registry interface, the way every reader asks it
    IConsumerRegistry adapter = new WhizbangReceptorRegistryQueryAdapter(NullReceptorRegistry.Instance);
#pragma warning restore CA1859

    await Assert.That(adapter.GetHandledCollectiveEventTypes()).IsEquivalentTo(["Jobs.Archive, Jobs"]);
  }

  [Test]
  public async Task ARegistryThatPredatesTheQuestion_AnswersUnknownAsync() {
    IConsumerRegistry custom = new PredicateOnlyRegistry();

    await Assert.That(custom.GetHandledCollectiveEventTypes()).IsNull();
  }

  [Test]
  public async Task ConsumedMessageTypes_HandledCollectiveEventTypes_ReadsTheRegisteredRegistryAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IConsumerRegistry>(new FixedCollectiveRegistry(["Jobs.Archive, Jobs"]));
    await using var provider = services.BuildServiceProvider();

    await Assert.That(ConsumedMessageTypes.HandledCollectiveEventTypes(provider)).IsEquivalentTo(["Jobs.Archive, Jobs"]);
  }

  [Test]
  public async Task ConsumedMessageTypes_HandledCollectiveEventTypes_WithNoRegistry_IsUnknownAsync() {
    await using var provider = new ServiceCollection().BuildServiceProvider();

    await Assert.That(ConsumedMessageTypes.HandledCollectiveEventTypes(provider)).IsNull();
    await Assert.That(ConsumedMessageTypes.HandledCollectiveEventTypes(null)).IsNull()
      .Because("a caller without a service provider cannot ask, so it knows nothing");
  }

  private static ReceptorRegistryContribution _contribution(params string[] handledCollective) => new() {
    AnyConsumerTypes = [],
    InboxHandlerTypes = [],
    StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>>(),
    HandledCollectiveEventTypes = handledCollective,
  };

  private sealed class PredicateOnlyRegistry : IConsumerRegistry {
    public bool HasAnyConsumer(string messageType) => false;
  }

  private sealed class FixedCollectiveRegistry(IReadOnlyList<string> handled) : IConsumerRegistry {
    public bool HasAnyConsumer(string messageType) => false;
    public IReadOnlyList<string>? GetHandledCollectiveEventTypes() => handled;
  }
}
