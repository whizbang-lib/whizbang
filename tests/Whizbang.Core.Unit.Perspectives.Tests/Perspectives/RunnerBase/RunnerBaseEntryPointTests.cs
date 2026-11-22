// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives.RunnerBase;

/// <summary>
/// The runner base's entry points (#2469): what a runner reports about itself, the events it selects for a set-based
/// batch, a rebuild's routing of re-keyed events, and the rebuild from the latest snapshot.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRunnerBase.cs</code-under-test>
public class RunnerBaseEntryPointTests {
  [Test]
  public async Task PerspectiveType_IsThePerspectiveTheRunnerFoldsAsync() {
    var harness = new RbHarness();

    await Assert.That(harness.Runner.PerspectiveType).IsEqualTo(typeof(RbPerspective));
  }

  [Test]
  public async Task SelectEvents_KeepsOnlyTheTypesThePerspectiveFolds_InTheirOrderAsync() {
    var harness = new RbHarness();
    var first = Rb.Envelope(harness.Marked("a"));
    var ignored = Rb.Envelope(new RbIgnored { StreamId = harness.StreamId });
    var other = Rb.Envelope(new RbOther { StreamId = harness.StreamId });

    var selected = harness.Runner.SelectEvents([first, ignored, other]);

    await Assert.That(selected).IsEquivalentTo([first, other]);
  }

  [Test]
  public async Task ReadEventsAsync_ReadsTheStreamAfterTheCursorAsync() {
    var harness = new RbHarness();
    var first = harness.Add(harness.Marked("a"));
    var second = harness.Add(harness.Marked("b"));

    var read = await harness.Runner.ReadEventsAsync(harness.StreamId, first.MessageId.Value);

    await Assert.That(read).IsEquivalentTo([second]);
  }

  [Test]
  public async Task ReadRebuildEventsAsync_ReadsEveryStreamFromItsStart_WithOneReadOfTheTypesThePerspectiveFoldsAsync() {
    var harness = new RbHarness();
    harness.Events.ReadsInBatches = true;
    var other = TrackedGuid.New().Value;
    var empty = TrackedGuid.New().Value;
    var first = harness.Add(harness.Marked("a"));
    _ = harness.Add(new RbIgnored { StreamId = harness.StreamId });
    var second = harness.Add(harness.Marked("b"));
    var others = harness.Events.Add(other, new RbOther { StreamId = other });

    var read = await ((IPerspectiveRunner)harness.Runner).ReadRebuildEventsAsync([harness.StreamId, other, empty]);

    await Assert.That(harness.Events.BatchReads).Count().IsEqualTo(1);
    await Assert.That(harness.Events.StreamReads).IsEmpty();
    await Assert.That(read![harness.StreamId]).IsEquivalentTo([first, second]);
    await Assert.That(read[other]).IsEquivalentTo([others]);
    await Assert.That(read[empty]).IsEmpty();
  }

  [Test]
  public async Task ReadRebuildEventsAsync_OverAStoreThatReadsNoStreamsInBatches_ReturnsNullAsync() {
    var harness = new RbHarness();

    await Assert.That(await ((IPerspectiveRunner)harness.Runner).ReadRebuildEventsAsync([harness.StreamId])).IsNull();
  }

  [Test]
  public async Task RunRebuildWithEventsAsync_FoldsTheEventsItIsGiven_AndReadsNothingOfTheStreamAsync() {
    var harness = new RbHarness();
    var first = harness.Add(harness.Marked("a"));
    var second = harness.Add(harness.Marked("b"));

    var completions = await Rb.RunAsRebuildAsync(() =>
      ((IPerspectiveRunner)harness.Runner).RunRebuildWithEventsAsync(harness.StreamId, RbHarness.NAME, [first, second]));

    await Assert.That(harness.Events.StreamReads).IsEmpty();
    await Assert.That(completions).Count().IsEqualTo(1);
    await Assert.That(completions[0].LastEventId).IsEqualTo(second.MessageId.Value);
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("ab");
  }

  [Test]
  public async Task RunRebuildWithEventsAsync_FoldsTheRow_UnderTheApplyLock_AsTheLiveApplyDoesAsync() {
    var coordinator = new RbCoordinator();
    var harness = new RbHarness { Coordinator = coordinator };
    var first = harness.Add(harness.Marked("a"));

    _ = await Rb.RunAsRebuildAsync(() =>
      ((IPerspectiveRunner)harness.Runner).RunRebuildWithEventsAsync(harness.StreamId, RbHarness.NAME, [first]));

    await Assert.That(coordinator.Acquired).IsEquivalentTo([(harness.StreamId, RbHarness.NAME)]);
    await Assert.That(coordinator.Released).IsEqualTo(1);
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a");
  }

  [Test]
  public async Task RunRebuildWithEventsAsync_OfNoEvents_ReportsOneUntouchedCompletionAsync() {
    var harness = new RbHarness();

    var completions = await ((IPerspectiveRunner)harness.Runner).RunRebuildWithEventsAsync(harness.StreamId, RbHarness.NAME, []);

    await Assert.That(completions).Count().IsEqualTo(1);
    await Assert.That(completions[0].LastEventId).IsEqualTo(Guid.Empty);
    await Assert.That(harness.Events.StreamReads).IsEmpty();
  }

  [Test]
  public async Task RunRebuildAsync_OfAnEmptyStream_ReportsOneUntouchedCompletionAsync() {
    var harness = new RbHarness();

    var completions = await harness.Runner.RunRebuildAsync(harness.StreamId, RbHarness.NAME);

    await Assert.That(completions).Count().IsEqualTo(1)
      .Because("the rebuilder expects one completion per stream, even when no row was folded");
    await Assert.That(completions[0].Status).IsEqualTo(PerspectiveProcessingStatus.None);
    await Assert.That(completions[0].LastEventId).IsEqualTo(Guid.Empty);
    await Assert.That(completions[0].RowChanged).IsFalse();
    await Assert.That(completions[0].StreamId).IsEqualTo(harness.StreamId);
  }

  [Test]
  public async Task RunRebuildAsync_ARekeyedEventOnItsOwn_FoldsTheRowItWasMovedToAsync() {
    var harness = new RbHarness();
    var target = TrackedGuid.New().Value;
    var stays = harness.Add(harness.Marked("a"));
    var moved = harness.Add(harness.Marked("b"));
    EventRekeys.Record(moved.Payload, target);
    var targetOwn = harness.Events.Add(target, new RbMarked { StreamId = target, Mark = "t" });

    var completions = await harness.Runner.RunRebuildAsync(harness.StreamId, RbHarness.NAME);

    await Assert.That(completions.Select(c => c.StreamId)).IsEquivalentTo([harness.StreamId, target]);
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a");
    await Assert.That(harness.Rows.Row(target).Marks.Order()).IsEquivalentTo(['b', 't'])
      .Because("a re-keyed row folds its own stream and every source moved onto it");
    await Assert.That(stays.MessageId).IsNotEqualTo(targetOwn.MessageId);
  }

  [Test]
  public async Task RunRebuildAsync_InsideARebuildRun_LeavesARekeyedRowToTheRunsLedgerAsync() {
    var harness = new RbHarness();
    var target = TrackedGuid.New().Value;
    var moved = harness.Add(harness.Marked("b"));
    EventRekeys.Record(moved.Payload, target);
    var ledger = new RebuildRekeyLedger();

    IReadOnlyList<PerspectiveCursorCompletion> completions;
    using (RebuildRekeyLedger.Begin(ledger)) {
      completions = await harness.Runner.RunRebuildAsync(harness.StreamId, RbHarness.NAME);
    }

    await Assert.That(completions).Count().IsEqualTo(1)
      .Because("every event left for the ledger, so the stream reports its one untouched completion");
    await Assert.That(completions[0].Status).IsEqualTo(PerspectiveProcessingStatus.None);
    await Assert.That(ledger.TakeTouched().Select(t => t.Row)).IsEquivalentTo([target])
      .Because("the run folds the re-keyed row once it has read every source");
    await Assert.That(harness.Rows.Has(target)).IsFalse();
  }

  [Test]
  public async Task RunRebuildFromLatestSnapshotAsync_WithNoSnapshotStore_SkipsTheStreamAsync() {
    var harness = new RbHarness();

    await Assert.That(await harness.Runner.RunRebuildFromLatestSnapshotAsync(harness.StreamId, RbHarness.NAME)).IsNull();
  }

  [Test]
  public async Task RunRebuildFromLatestSnapshotAsync_WithNoSnapshot_SkipsTheStreamAsync() {
    var harness = new RbHarness { Snapshots = new RbSnapshots() };

    await Assert.That(await harness.Runner.RunRebuildFromLatestSnapshotAsync(harness.StreamId, RbHarness.NAME)).IsNull();
  }

  [Test]
  public async Task RunRebuildFromLatestSnapshotAsync_FromASnapshotInsideTheArchive_SkipsTheStreamAsync() {
    var harness = new RbHarness { Snapshots = new RbSnapshots() };
    harness.Snapshots.Floor = new PerspectiveSnapshotFloor(TrackedGuid.New().Value, 2, Rb.SnapshotOf(new RbModel()), ArchivedThroughVersion: 5);

    await Assert.That(await harness.Runner.RunRebuildFromLatestSnapshotAsync(harness.StreamId, RbHarness.NAME)).IsNull()
      .Because("archived events lie after the snapshot, and a rebuild that reads no archive cannot fold them");
  }

  [Test]
  public async Task RunRebuildFromLatestSnapshotAsync_FromAStaleSnapshot_SkipsTheStreamAsync() {
    var harness = new RbHarness { Snapshots = new RbSnapshots() };
    harness.Snapshots.Floor = new PerspectiveSnapshotFloor(TrackedGuid.New().Value, 2, Rb.StaleSnapshot(), ArchivedThroughVersion: null);

    await Assert.That(await harness.Runner.RunRebuildFromLatestSnapshotAsync(harness.StreamId, RbHarness.NAME)).IsNull();
  }

  [Test]
  public async Task RunRebuildFromLatestSnapshotAsync_ReplaysTheHotEventsAfterTheSnapshot_UnderTheApplyLockAsync() {
    var coordinator = new RbCoordinator();
    var harness = new RbHarness { Snapshots = new RbSnapshots(), Coordinator = coordinator };
    var snapshotAt = harness.Add(harness.Marked("a"));
    harness.Add(harness.Marked("b"));
    harness.Snapshots.Floor = new PerspectiveSnapshotFloor(
      snapshotAt.MessageId.Value, 1, Rb.SnapshotOf(new RbModel { Id = harness.StreamId, Marks = "a" }), ArchivedThroughVersion: null);

    var completions = await harness.Runner.RunRebuildFromLatestSnapshotAsync(harness.StreamId, RbHarness.NAME);

    await Assert.That(completions).Count().IsEqualTo(1);
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("ab");
    await Assert.That(coordinator.Acquired).IsEquivalentTo([(harness.StreamId, RbHarness.NAME)]);
    await Assert.That(coordinator.Released).IsEqualTo(1);
  }
}

/// <summary>An apply coordinator that records each lock taken and released.</summary>
internal sealed class RbCoordinator : IPerspectiveApplyCoordinator {
  public List<(Guid StreamId, string PerspectiveName)> Acquired { get; } = [];
  public int Released { get; private set; }

  public Task<IAsyncDisposable> AcquireAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
    Acquired.Add((streamId, perspectiveName));
    return Task.FromResult<IAsyncDisposable>(new Release(this));
  }

  private sealed class Release(RbCoordinator owner) : IAsyncDisposable {
    public ValueTask DisposeAsync() {
      owner.Released++;
      return ValueTask.CompletedTask;
    }
  }
}
