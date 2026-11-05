// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Registry;

namespace Whizbang.Core.Tests.Generated;

/// <summary>
/// The aggregating registry enumerates what the service consumes (#2058): the list form of
/// <see cref="WhizbangReceptorRegistryQuery.HasAnyConsumer"/>, merged across every loaded assembly's contribution and
/// built from the same generated data, so the list and the predicate cannot disagree.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Generated/WhizbangReceptorRegistryQuery.cs</code-under-test>
[NotInParallel("ReceptorRegistryQueryAggregation")]
public class WhizbangReceptorRegistryQueryConsumedMessagesTests {

  [Before(Test)]
  public void ResetRegistry() {
    AssemblyRegistry<ReceptorRegistryContribution>.ClearForTesting();
    WhizbangReceptorRegistryQuery.ClearCacheForTesting();
  }

  [After(Test)]
  public void TeardownRegistry() => ResetRegistry();

  [Test]
  public async Task TwoContributions_GetConsumedMessages_UnionsDeduplicatesAndSortsAsync() {
    AssemblyRegistry<ReceptorRegistryContribution>.Register(_contribution(
      new ConsumedMessageInfo("MyApp.Zeta, MyApp", IsEvent: true),
      new ConsumedMessageInfo("MyApp.Shared, MyApp", IsEvent: true)));
    AssemblyRegistry<ReceptorRegistryContribution>.Register(_contribution(
      new ConsumedMessageInfo("MyApp.Shared, MyApp", IsEvent: true),
      new ConsumedMessageInfo("MyApp.Alpha, MyApp", IsEvent: false)));

    var consumed = WhizbangReceptorRegistryQuery.GetConsumedMessages();

    await Assert.That(consumed.Select(c => c.TypeName)).IsEquivalentTo(
      ["MyApp.Alpha, MyApp", "MyApp.Shared, MyApp", "MyApp.Zeta, MyApp"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a type two assemblies consume is one consumed type, and the order is deterministic");
    await Assert.That(consumed.Single(c => c.TypeName == "MyApp.Alpha, MyApp").IsEvent).IsFalse();
  }

  [Test]
  public async Task FindConsumedMessage_FindsByTypeAndAssembly_IgnoringVersionDetailsAsync() {
    AssemblyRegistry<ReceptorRegistryContribution>.Register(_contribution(
      new ConsumedMessageInfo("MyApp.Shipped, MyApp", IsEvent: true)));

    await Assert.That(WhizbangReceptorRegistryQuery.FindConsumedMessage("MyApp.Shipped, MyApp, Version=2.0.0.0"))
      .IsEqualTo(new ConsumedMessageInfo("MyApp.Shipped, MyApp", IsEvent: true));
    await Assert.That(WhizbangReceptorRegistryQuery.FindConsumedMessage("MyApp.Other, MyApp")).IsNull();
  }

  [Test]
  public async Task Adapter_FindConsumedMessage_ReadsTheGeneratedRegistryAsync() {
    AssemblyRegistry<ReceptorRegistryContribution>.Register(_contribution(
      new ConsumedMessageInfo("MyApp.Shipped, MyApp", IsEvent: true)));
#pragma warning disable CA1859 // the lookup is asked through the registry interface, the way every reader asks it
    IConsumerRegistry adapter = new WhizbangReceptorRegistryQueryAdapter(NullReceptorRegistry.Instance);
#pragma warning restore CA1859

    await Assert.That(adapter.FindConsumedMessage("MyApp.Shipped, MyApp"))
      .IsEqualTo(new ConsumedMessageInfo("MyApp.Shipped, MyApp", IsEvent: true));
  }

  [Test]
  public async Task ThisAssemblysGeneratedRegistration_EnumeratesExactlyWhatHasAnyConsumerAcceptsAsync() {
    // This assembly's own generated registration (the module initializer already ran, and the tests in this group clear
    // the registry); Whizbang.Core exposes its own to this assembly too, so name the local one.
#pragma warning disable CS0436
    WhizbangReceptorRegistryQueryRegistration.Register();
#pragma warning restore CS0436

    var consumed = WhizbangReceptorRegistryQuery.GetConsumedMessages();
    var anyConsumerCount = WhizbangReceptorRegistryQuery.GetDiagnosticSnapshot().AnyConsumerTypes;

    await Assert.That(consumed).IsNotEmpty()
      .Because("this assembly declares receptors and perspectives, so it consumes something");
    foreach (var message in consumed) {
      await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer(message.TypeName)).IsTrue()
        .Because($"'{message.TypeName}' is enumerated as consumed, so the receive boundary must accept it");
    }
    await Assert.That(consumed.Count).IsEqualTo(anyConsumerCount)
      .Because("every type the predicate accepts is enumerable: the counts match and each enumerated type is accepted");
  }

  private static ReceptorRegistryContribution _contribution(params ConsumedMessageInfo[] consumed) => new() {
    AnyConsumerTypes = [.. consumed.Select(c => c.TypeName[..c.TypeName.IndexOf(',', StringComparison.Ordinal)])],
    InboxHandlerTypes = [],
    StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>>(),
    ConsumedMessages = consumed,
  };
}
