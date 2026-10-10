// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Registry;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Generated;

/// <summary>
/// Every outcome of <see cref="WhizbangReceptorRegistryQuery"/>'s snapshot cache, driven
/// deterministically from one thread: the first query on an empty cache builds the snapshot,
/// a query with an unchanged contribution count reuses it, and a contribution registered after
/// a query makes the next query rebuild it. The snapshot is observed through
/// <see cref="WhizbangReceptorRegistryQuery.GetHandledMessages"/>, whose list is a member of the
/// snapshot, so reference equality of two results means the same snapshot served both.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Generated/WhizbangReceptorRegistryQuery.cs</code-under-test>
[NotInParallel("ReceptorRegistryQueryAggregation")]
public class WhizbangReceptorRegistryQuerySnapshotTests {

  [Before(Test)]
  public void ResetRegistry() {
    AssemblyRegistry<ReceptorRegistryContribution>.ClearForTesting();
    WhizbangReceptorRegistryQuery.ClearCacheForTesting();
  }

  [After(Test)]
  public void TeardownRegistry() => ResetRegistry();

  [Test]
  public async Task FirstQuery_OnAnEmptyCache_BuildsTheSnapshotAsync() {
    _register("Snap.First");

    // The cache was cleared in setup, so this query must build from the registry.
    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("Snap.First, Snap")).IsTrue()
      .Because("the first query on an empty cache must build the snapshot from the registered contribution");
    var handled = WhizbangReceptorRegistryQuery.GetHandledMessages();
    await Assert.That(handled.Count).IsEqualTo(1);
    await Assert.That(handled[0].MessageTypeName).IsEqualTo("Snap.First");
  }

  [Test]
  public async Task RepeatedQuery_WithUnchangedCount_ReusesTheSnapshotAsync() {
    _register("Snap.Reused");

    var first = WhizbangReceptorRegistryQuery.GetHandledMessages();
    // Other accessors read the same snapshot and must not replace it.
    await Assert.That(WhizbangReceptorRegistryQuery.HasInboxHandler("Snap.Reused")).IsTrue();
    await Assert.That(WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PreInboxInline, "Snap.Reused")).IsTrue();
    var second = WhizbangReceptorRegistryQuery.GetHandledMessages();

    await Assert.That(ReferenceEquals(first, second)).IsTrue()
      .Because("with the contribution count unchanged, every query must reuse the published snapshot, not rebuild it");
  }

  [Test]
  public async Task ContributionRegisteredAfterFirstQuery_RebuildsTheSnapshotAsync() {
    _register("Snap.Before");
    var before = WhizbangReceptorRegistryQuery.GetHandledMessages();
    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("Snap.After")).IsFalse();

    _register("Snap.After");
    var after = WhizbangReceptorRegistryQuery.GetHandledMessages();

    await Assert.That(ReferenceEquals(before, after)).IsFalse()
      .Because("a changed contribution count must make the next query build a new snapshot");
    await Assert.That(after.Count).IsEqualTo(2);
    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("Snap.After")).IsTrue()
      .Because("the rebuilt snapshot must include the late contribution in every view");
    await Assert.That(WhizbangReceptorRegistryQuery.HasInboxHandler("Snap.After")).IsTrue();
    await Assert.That(WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PreInboxInline, "Snap.After")).IsTrue();
    await Assert.That(WhizbangReceptorRegistryQuery.GetDiagnosticSnapshot().AnyConsumerTypes).IsEqualTo(2);
  }

  [Test]
  public async Task ClearCacheForTesting_DropsThePublishedSnapshotAsync() {
    _register("Snap.Cleared");
    var before = WhizbangReceptorRegistryQuery.GetHandledMessages();

    WhizbangReceptorRegistryQuery.ClearCacheForTesting();
    var after = WhizbangReceptorRegistryQuery.GetHandledMessages();

    await Assert.That(ReferenceEquals(before, after)).IsFalse()
      .Because("clearing the cache must force the next query to build a new snapshot even at the same count");
    await Assert.That(after.Count).IsEqualTo(1);
  }

  private static void _register(string typeName) {
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AnyConsumerTypes = [typeName],
      InboxHandlerTypes = [typeName],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>> {
        [LifecycleStage.PreInboxInline] = [typeName],
      },
      HandledMessages = [new HandledMessageInfo(typeName, "snap", MessageKind.Event)],
    });
  }
}
