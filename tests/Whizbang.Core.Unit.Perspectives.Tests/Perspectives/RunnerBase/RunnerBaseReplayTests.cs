// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Events.System;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives.RunnerBase;

/// <summary>
/// The rewind and its replay, as the runner base makes them (#2469): the lifecycle events it publishes, the catch-up
/// passes, the treatment of each Apply outcome in memory, intermediate snapshots, and the single write that ends it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRunnerBase.cs</code-under-test>
public class RunnerBaseReplayTests {
  [Test]
  public async Task ARewind_PublishesItsStartAndCompletion_OnOneRewindStreamAsync() {
    var harness = new RbHarness();
    var dispatcher = new RebuildEventDispatcherDouble();
    harness.Services.AddSingleton<IDispatcher>(dispatcher);
    var marked = harness.Add(harness.Marked("a"));

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, marked.MessageId.Value);

    var started = dispatcher.Published.OfType<PerspectiveRewindStarted>().Single();
    var completed = dispatcher.Published.OfType<PerspectiveRewindCompleted>().Single();
    await Assert.That(completed.StreamId).IsEqualTo(started.StreamId);
    await Assert.That(started.StreamId).IsNotEqualTo(harness.StreamId)
      .Because("the rewind's own events are written to a stream of their own, never the rewound one (#1360)");
  }

  [Test]
  public async Task ARewindWhoseLifecycleEventsCannotBePublished_StillReplaysAsync() {
    var harness = new RbHarness();
    harness.Services.AddSingleton<IDispatcher>(new RebuildEventDispatcherDouble(throwOnPublish: true));
    var marked = harness.Add(harness.Marked("a"));

    var completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, marked.MessageId.Value);

    await Assert.That(completion.RowChanged).IsTrue();
    await Assert.That(harness.Log.Count(LogLevel.Debug, "Failed to publish PerspectiveRewindStarted")).IsEqualTo(1);
    await Assert.That(harness.Log.Count(LogLevel.Debug, "Failed to publish PerspectiveRewindCompleted")).IsEqualTo(1);
  }

  [Test]
  public async Task ARewindUnderACoordinator_TakesTheApplyLockAsync() {
    var coordinator = new RbCoordinator();
    var harness = new RbHarness { Coordinator = coordinator };
    var marked = harness.Add(harness.Marked("a"));

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, marked.MessageId.Value);

    await Assert.That(coordinator.Acquired).Count().IsEqualTo(1);
    await Assert.That(coordinator.Released).IsEqualTo(1);
  }

  [Test]
  public async Task AStreamAppendedToOnEveryPass_StopsAfterTheBoundedCatchUpAsync() {
    var harness = new RbHarness();
    var first = harness.Add(harness.Marked("a"));
    harness.Events.BeforeRead = id => harness.Events.Add(id, new RbMarked { StreamId = id, Mark = "+" });

    var completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(harness.Log.Count(LogLevel.Warning, "hit MAX_REWIND_CATCH_UP_ITERATIONS")).IsEqualTo(1);
    await Assert.That(completion.EventsProcessed).IsGreaterThanOrEqualTo(100);
  }

  [Test]
  public async Task AnOverwriteOnlyStreamAppendedToOnEveryPass_StopsAfterTheBoundedCatchUpAsync() {
    var harness = new RbHarness { Traits = new RbTraits { IsOverwriteOnly = true } };
    harness.Runner.DecidesAlone = _ => true;
    var first = harness.Add(harness.Marked("a"));
    harness.Events.BeforeRead = id => harness.Events.Add(id, new RbMarked { StreamId = id, Mark = "+" });

    var completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(harness.Log.Count(LogLevel.Warning, "hit MAX_REWIND_CATCH_UP_ITERATIONS")).IsEqualTo(1);
    await Assert.That(completion.RowChanged).IsTrue();
  }

  [Test]
  public async Task AReplayEndingInAnUnconditionalPurge_AppliesOnlyThePurgeAsync() {
    var harness = new RbHarness();
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" });
    harness.Runner.Purges = e => e is RbOther;
    var folds = 0;
    harness.Runner.Apply = (model, e) => {
      folds++;
      return e is RbOther ? (null, ModelAction.Purge) : (model, ModelAction.None);
    };
    var first = harness.Add(harness.Marked("a"));
    harness.Add(new RbOther { StreamId = harness.StreamId });

    var completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(folds).IsEqualTo(1);
    await Assert.That(completion.EventsProcessed).IsEqualTo(2);
    await Assert.That(harness.Rows.Purged).IsEquivalentTo([harness.StreamId]);
  }

  [Test]
  public async Task AReplayedApplyThatThrows_IsSkipped_AndASoftDeleteKeepsTheRowAsync() {
    var harness = new RbHarness();
    harness.Runner.Apply = (model, e) => e switch {
      RbOther => throw new InvalidOperationException("bad"),
      RbScoped => (null, ModelAction.Delete),
      _ => (model! with { Marks = model.Marks + "a" }, ModelAction.Delete),
    };
    var first = harness.Add(harness.Marked("a"), tenant: "tenant-a");
    harness.Add(new RbOther { StreamId = harness.StreamId }, hops: false);
    harness.Add(new RbScoped { StreamId = harness.StreamId }, hops: false);

    var completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(completion.EventsProcessed).IsEqualTo(3);
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a");
    await Assert.That(harness.Log.Count(LogLevel.Error, "Error applying RbOther to perspective stream")).IsEqualTo(1);
    await Assert.That(harness.Rows.WrittenScopes.Single().Scope!.TenantId).IsEqualTo("tenant-a")
      .Because("a replay that recreates the row writes the scope of the events it folded");
  }

  [Test]
  public async Task AReplayFromASnapshot_WhoseEveryEventThrows_WritesTheSnapshotAtItsEventAsync() {
    var harness = new RbHarness { Snapshots = new RbSnapshots(), SnapshotOptions = new PerspectiveSnapshotOptions { Enabled = false } };
    harness.Runner.Apply = (_, _) => throw new InvalidOperationException("bad");
    var floor = harness.Add(harness.Marked("a"));
    var late = harness.Add(harness.Marked("b"));
    harness.Snapshots.Floor = new PerspectiveSnapshotFloor(floor.MessageId.Value, 1, Rb.SnapshotOf(new RbModel { Id = harness.StreamId, Marks = "s" }), null);

    var completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, late.MessageId.Value);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("s");
    await Assert.That(completion.LastEventId).IsEqualTo(late.MessageId.Value);
  }

  [Test]
  public async Task AFullReplayThatFindsNothing_ReportsNoPositionAsync() {
    var harness = new RbHarness();

    var completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, TrackedGuid.New().Value);

    await Assert.That(completion.LastEventId).IsEqualTo(Guid.Empty);
    await Assert.That(completion.ReplayedThroughCommitSequence).IsNull();
  }

  [Test]
  public async Task AReplayOfUnversionedEvents_ReportsTheFrontierAsync() {
    var harness = new RbHarness();
    var first = harness.Add(harness.Marked("a"), versioned: false);
    var second = harness.Add(harness.Marked("b"), versioned: false);

    var completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    var frontier = string.CompareOrdinal(first.MessageId.Value.ToString("D"), second.MessageId.Value.ToString("D")) > 0 ? first : second;
    await Assert.That(completion.LastEventId).IsEqualTo(frontier.MessageId.Value)
      .Because("with no versions the order is unknown, and the id frontier is the position reported");
  }

  [Test]
  public async Task AReplayInARebuild_ReportsWhetherARefusedRowWasAlreadyRightAsync() {
    var harness = new RbHarness { Traits = new RbTraits { SnapshotsBeforeWrite = true } };
    var first = harness.Add(harness.Marked("a"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, TrackedGuid.New().Value);
    harness.Rows.RefuseWrites = true;

    PerspectiveCursorCompletion? completion = null;
    await Rb.WithModeAsync(ProcessingMode.Rebuild, async () =>
      completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value));

    await Assert.That(completion!.UnchangedReason).IsEqualTo(PerspectiveRowUnchangedReason.AlreadyCorrect);
  }

  [Test]
  public async Task AReplayInARebuild_ThatRemovedTheRow_ReportsTheRefusalAsync() {
    var harness = new RbHarness();
    var first = harness.Add(harness.Marked("a"));
    harness.Rows.RefuseWrites = true;

    PerspectiveCursorCompletion? completion = null;
    await Rb.WithModeAsync(ProcessingMode.Rebuild, async () =>
      completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value));

    await Assert.That(completion!.UnchangedReason).IsEqualTo(PerspectiveRowUnchangedReason.WriteRefused)
      .Because("with no stored row to compare against, a refused write is only that");
  }

  [Test]
  public async Task AReplay_TakesIntermediateSnapshots_AtTheConfiguredIntervalAsync() {
    var snapshots = new RbSnapshots();
    var harness = new RbHarness {
      Snapshots = snapshots,
      SnapshotOptions = new PerspectiveSnapshotOptions { Enabled = true, RewindSnapshotIntervalEvents = 2, MaxSnapshotsPerStream = 3 },
    };
    var first = harness.Add(harness.Marked("a"));
    harness.Add(harness.Marked("b"));
    harness.Add(harness.Marked("c"));

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(snapshots.Created.Count).IsEqualTo(2).Because("one intermediate at the second event, one at the end");
    await Assert.That(Rb.ModelOf(snapshots.Created[0].Data).Marks).IsEqualTo("ab");
    await Assert.That(snapshots.Prunes).Contains(3);
  }

  [Test]
  public async Task AnIntermediateSnapshotThatFails_DoesNotFailTheReplayAsync() {
    var snapshots = new RbSnapshots { CreateFails = new InvalidOperationException("snapshot failed") };
    var harness = new RbHarness {
      Snapshots = snapshots,
      SnapshotOptions = new PerspectiveSnapshotOptions { Enabled = true, RewindSnapshotIntervalEvents = 1 },
    };
    var first = harness.Add(harness.Marked("a"));
    harness.Add(harness.Marked("b"));

    await Assert.That(async () => await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value))
      .Throws<InvalidOperationException>()
      .Because("the end-of-rewind snapshot is not best-effort; only the intermediate ones are");
    await Assert.That(harness.Log.Count(LogLevel.Debug, "Intermediate snapshot at event")).IsGreaterThanOrEqualTo(1);
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("ab");
  }

  [Test]
  public async Task AnEphemeralReplay_TakesNoIntermediateSnapshot_WhileTheStreamHoldsOneAsync() {
    var snapshots = new RbSnapshots { HasAny = true };
    var harness = new RbHarness {
      Traits = new RbTraits { IsEphemeral = true },
      Snapshots = snapshots,
      SnapshotOptions = new PerspectiveSnapshotOptions { Enabled = true, RewindSnapshotIntervalEvents = 1 },
    };
    var floor = harness.Add(harness.Marked("a"));
    var late = harness.Add(harness.Marked("b"));
    snapshots.Floor = new PerspectiveSnapshotFloor(floor.MessageId.Value, 1, Rb.SnapshotOf(new RbModel { Id = harness.StreamId, Marks = "a" }), null);

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, late.MessageId.Value);

    await Assert.That(snapshots.Created).IsEmpty().Because("an ephemeral perspective's one snapshot slot is taken");
  }

  // ===== Overwrite-only =====

  [Test]
  public async Task AnOverwriteOnlyRewindWhoseLastApplyThrows_ReplaysInFullAsync() {
    var harness = new RbHarness { Traits = new RbTraits { IsOverwriteOnly = true } };
    harness.Runner.DecidesAlone = _ => true;
    var calls = 0;
    harness.Runner.Apply = (model, e) => {
      calls++;
      return calls == 1 ? throw new InvalidOperationException("last alone") : (model! with { Marks = model.Marks + "x" }, ModelAction.None);
    };
    var first = harness.Add(harness.Marked("a"), tenant: "t");
    harness.Add(harness.Marked("b"), hops: false);

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(harness.Log.Count(LogLevel.Debug, "the stream is replayed in full")).IsEqualTo(1);
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("xx");
  }

  [Test]
  public async Task AnOverwriteOnlyRewind_KeepsTheScopeOfTheLastScopedEventAsync() {
    var harness = new RbHarness { Traits = new RbTraits { IsOverwriteOnly = true } };
    harness.Runner.DecidesAlone = _ => true;
    var first = harness.Add(harness.Marked("a"), hops: false);

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a");
    await Assert.That(harness.Rows.WrittenScopes.Single().Scope!.TenantId).IsNull()
      .Because("no event carried a scope, so the row is written with an empty one");
  }

  [Test]
  public async Task AnOverwriteOnlyRewind_WritesTheScopeOfTheLastEventThatCarriesOneAsync() {
    var harness = new RbHarness { Traits = new RbTraits { IsOverwriteOnly = true } };
    harness.Runner.DecidesAlone = _ => true;
    var first = harness.Add(harness.Marked("a"), tenant: "tenant-a");
    harness.Add(harness.Marked("b"), hops: false);

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("b");
    await Assert.That(harness.Rows.WrittenScopes.Single().Scope!.TenantId).IsEqualTo("tenant-a");
  }

  [Test]
  public async Task AReplayWhoseAppliesProduceNoModel_TakesNoIntermediateSnapshot_AndWritesNothingAsync() {
    var snapshots = new RbSnapshots();
    var harness = new RbHarness {
      Snapshots = snapshots,
      SnapshotOptions = new PerspectiveSnapshotOptions { Enabled = true, RewindSnapshotIntervalEvents = 1 },
    };
    harness.Runner.Apply = (_, _) => (null, ModelAction.None);
    var first = harness.Add(harness.Marked("a"));
    harness.Add(harness.Marked("b"));

    var completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(snapshots.Created).IsEmpty();
    await Assert.That(completion.UnchangedReason).IsEqualTo(PerspectiveRowUnchangedReason.NoModel);
  }

  [Test]
  public async Task AReplayThatPurgesPartWay_TakesNoIntermediateSnapshotOfThePurgedRowAsync() {
    var snapshots = new RbSnapshots();
    var harness = new RbHarness {
      Snapshots = snapshots,
      SnapshotOptions = new PerspectiveSnapshotOptions { Enabled = true, RewindSnapshotIntervalEvents = 1 },
    };
    harness.Runner.Apply = (model, e) => e is RbOther ? (null, ModelAction.Purge) : (model! with { Marks = model.Marks + "a" }, ModelAction.None);
    var first = harness.Add(new RbOther { StreamId = harness.StreamId });
    harness.Add(harness.Marked("a"));

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(snapshots.Created).IsEmpty();
    await Assert.That(harness.Rows.Purged).IsEquivalentTo([harness.StreamId]);
  }
}
