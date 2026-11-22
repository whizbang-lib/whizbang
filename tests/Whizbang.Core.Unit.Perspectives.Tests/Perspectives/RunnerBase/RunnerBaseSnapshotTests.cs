// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives.RunnerBase;

/// <summary>
/// The snapshots the runner base takes and releases (#2469): the one taken before a write that strips the model, the
/// ones a run decides against, the bootstraps, and the write guard every row write passes.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRunnerBase.cs</code-under-test>
public class RunnerBaseSnapshotTests {
  private static RbHarness _snapshotting(RbTraits? traits = null, int every = 1, RbSnapshots? snapshots = null) => new() {
    Traits = traits ?? new RbTraits { SnapshotsBeforeWrite = true },
    Snapshots = snapshots ?? new RbSnapshots(),
    SnapshotOptions = new PerspectiveSnapshotOptions {
      Enabled = true,
      SnapshotEveryNEvents = every,
      EphemeralSnapshotEveryNEvents = every,
      MaxSnapshotsPerStream = 4,
      EphemeralMaxSnapshotsPerStream = 1,
    },
  };

  [Test]
  public async Task ADueSnapshotOfAStrippedModel_IsTakenBeforeTheWrite_FromTheModelAsFoldedAsync() {
    var harness = _snapshotting();
    harness.Runner.Physical = m => new Dictionary<string, object?> { ["marks"] = m.Marks };
    harness.Runner.Strip = m => m with { Marks = "" };
    var marked = harness.Add(harness.Marked("a"));

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]);

    await Assert.That(Rb.ModelOf(harness.Snapshots!.Created.Single().Data).Marks).IsEqualTo("a")
      .Because("the write strips the promoted field, so the snapshot is serialized before it");
  }

  [Test]
  public async Task ADueSnapshotOfARefusedLiveWrite_IsReleased_AndTheStreamRewoundAsync() {
    var harness = _snapshotting();
    harness.Rows.RefuseWrites = true;
    var marked = harness.Add(harness.Marked("a"));

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]);

    await Assert.That(harness.Log.Count(LogLevel.Warning, "[diag.write.refused]")).IsEqualTo(1);
  }

  [Test]
  public async Task ADueSnapshotWhoseSlotIsTaken_IsReleasedUnwrittenAsync() {
    var harness = _snapshotting(new RbTraits { IsEphemeral = true, SnapshotsBeforeWrite = true }, snapshots: new RbSnapshots { HasAny = true });
    var marked = harness.Add(harness.Marked("a"));

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]);

    await Assert.That(harness.Snapshots!.Created).IsEmpty()
      .Because("an ephemeral perspective's one snapshot slot is kept by the reap-boundary snapshot");
  }

  [Test]
  public async Task APairWhoseWriteCannotBePrepared_ReleasesTheSnapshotItTookAsync() {
    var harness = _snapshotting();
    var marked = harness.Add(harness.Marked("a"));

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null);

    await Assert.That(prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.WriteNotPreparable);
    await Assert.That(harness.Snapshots!.Created).IsEmpty();
  }

  [Test]
  public async Task ARebuildPairWhoseWriteCannotBePrepared_ReleasesTheFoldItKeptForComparisonAsync() {
    var harness = _snapshotting(every: 100);
    var marked = harness.Add(harness.Marked("a"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, marked.MessageId.Value);

    PreparedPerspectiveApply? prepared = null;
    await Rb.WithModeAsync(ProcessingMode.Rebuild, async () =>
      prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null));

    await Assert.That(prepared!.PerPairReason).IsEqualTo(PerspectivePerPairReason.WriteNotPreparable);
  }

  [Test]
  public async Task AReplaySnapshotOfUnversionedEvents_IsTakenAtTheLastEventAppliedAsync() {
    var harness = _snapshotting(new RbTraits());
    var first = harness.Add(harness.Marked("a"), versioned: false);
    var second = harness.Add(harness.Marked("b"), versioned: false);

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(harness.Snapshots!.Created[^1].EventId).IsEqualTo(second.MessageId.Value);
  }

  [Test]
  public async Task AReplaySnapshotWhoseSlotIsTaken_IsReleasedUnwrittenAsync() {
    var harness = _snapshotting(new RbTraits { IsEphemeral = true, SnapshotsBeforeWrite = true }, snapshots: new RbSnapshots { HasAny = true });
    var floor = harness.Add(harness.Marked("a"));
    var late = harness.Add(harness.Marked("b"));
    harness.Snapshots!.Floor = new PerspectiveSnapshotFloor(floor.MessageId.Value, 1, Rb.SnapshotOf(new RbModel { Id = harness.StreamId, Marks = "a" }), null);

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, late.MessageId.Value);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("ab");
    await Assert.That(harness.Snapshots.Created).IsEmpty();
  }

  // ===== Bootstraps =====

  [Test]
  public async Task ABootstrap_WithAStoreButNoOptions_IsOffAsync() {
    var harness = new RbHarness { Snapshots = new RbSnapshots() };
    var marked = harness.Add(harness.Marked("a"));

    var outcome = await harness.Runner.TryBootstrapSnapshotAsync(harness.StreamId, RbHarness.NAME, marked.MessageId.Value);

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.SnapshotsOff);
  }

  [Test]
  [Arguments(false, false)]
  [Arguments(true, false)]
  [Arguments(true, true)]
  public async Task ABootstrapThroughABoundary_WithSnapshotsOff_IsOffAsync(bool store, bool options) {
    var harness = new RbHarness {
      Snapshots = store ? new RbSnapshots() : null,
      SnapshotOptions = options ? new PerspectiveSnapshotOptions { Enabled = false } : null,
    };
    var boundary = harness.Add(harness.Marked("a"));
    var cursor = harness.Add(harness.Marked("b"));

    var outcome = await harness.Runner.TryBootstrapSnapshotThroughAsync(harness.StreamId, RbHarness.NAME, cursor.MessageId.Value, boundary.MessageId.Value);

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.SnapshotsOff);
  }

  [Test]
  public async Task ABootstrapThroughABoundary_OfAStreamWhoseArchiveWasDeleted_IsRefusedAsync() {
    var harness = _snapshotting(new RbTraits());
    harness.Services.AddSingleton<IArchivedStreamExtents>(new RbExtents(ArchivedStreamExtent.PrefixDeleted));
    var boundary = harness.Add(harness.Marked("a"));
    var cursor = harness.Add(harness.Marked("b"));

    var outcome = await harness.Runner.TryBootstrapSnapshotThroughAsync(harness.StreamId, RbHarness.NAME, cursor.MessageId.Value, boundary.MessageId.Value);

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.Refused);
    await Assert.That(harness.Log.Count(LogLevel.Warning, "archived history was deleted")).IsEqualTo(1);
  }

  [Test]
  public async Task ABootstrapThroughABoundary_FromAFloorAnArchiveCovers_ReadsThroughTheArchiveAsync() {
    var harness = _snapshotting(new RbTraits());
    var floor = harness.Add(harness.Marked("a"));
    var boundary = harness.Add(harness.Marked("b"));
    var cursor = harness.Add(harness.Marked("c"));
    harness.Snapshots!.Floor = new PerspectiveSnapshotFloor(floor.MessageId.Value, 1, Rb.SnapshotOf(new RbModel { Id = harness.StreamId, Marks = "a" }), ArchivedThroughVersion: 1);

    var outcome = await harness.Runner.TryBootstrapSnapshotThroughAsync(harness.StreamId, RbHarness.NAME, cursor.MessageId.Value, boundary.MessageId.Value);

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.Written);
    await Assert.That(harness.Events.ThroughArchiveReads).IsEqualTo(1);
    await Assert.That(harness.Snapshots.Prunes).IsEquivalentTo([4]);
  }

  [Test]
  public async Task ABootstrapThroughABoundary_FromAFloorAnArchiveCoversWithNoVersion_ReadsTheHotEventsAsync() {
    var harness = _snapshotting(new RbTraits());
    var floor = harness.Add(harness.Marked("a"));
    var boundary = harness.Add(harness.Marked("b"));
    var cursor = harness.Add(harness.Marked("c"), versioned: false);
    harness.Snapshots!.Floor = new PerspectiveSnapshotFloor(floor.MessageId.Value, null, Rb.SnapshotOf(new RbModel { Id = harness.StreamId, Marks = "a" }), ArchivedThroughVersion: 1);

    var outcome = await harness.Runner.TryBootstrapSnapshotThroughAsync(harness.StreamId, RbHarness.NAME, cursor.MessageId.Value, boundary.MessageId.Value);

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.Written);
    await Assert.That(harness.Events.ThroughArchiveReads).IsEqualTo(0);
    await Assert.That(Rb.ModelOf(harness.Snapshots.Created.Single().Data).Marks).IsEqualTo("ab")
      .Because("the unversioned event after the boundary is not folded into the boundary's snapshot");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task ABootstrapThroughABoundaryNotReadWithItsVersion_IsRefusedAsync(bool boundaryRead) {
    var harness = _snapshotting(new RbTraits());
    var boundary = boundaryRead
      ? harness.Add(harness.Marked("a"), versioned: false)
      : Rb.Envelope(harness.Marked("a"));
    var cursor = harness.Add(harness.Marked("b"));

    var outcome = await harness.Runner.TryBootstrapSnapshotThroughAsync(harness.StreamId, RbHarness.NAME, cursor.MessageId.Value, boundary.MessageId.Value);

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.Refused);
    await Assert.That(harness.Log.Count(LogLevel.Warning, "the boundary event is not read with its stream version")).IsEqualTo(1);
  }

  // ===== The write guard =====

  /// <summary>
  /// Every caller of the row write passes a model an Apply produced, so the guard's refusal is asserted on the write
  /// itself (#1677, #2469): a scaffolded default is never stored.
  /// </summary>
  [Test]
  public async Task TheRowWrite_RefusesAModelNoApplyProduced_AndWritesNothingAsync() {
    var harness = new RbHarness();

    var written = await harness.Runner.SaveModelAndCheckpointAsync(
      harness.StreamId, RbHarness.NAME, new RbModel { Id = harness.StreamId }, producedByApply: false,
      new(TrackedGuid.New().Value, "type", null, DateTime.UnixEpoch), new(RowExists: false, []), CancellationToken.None);

    await Assert.That(written).IsFalse();
    await Assert.That(harness.Rows.Writes).IsEqualTo(0);
  }

  // ===== Defaults a generated runner overrides =====

  [Test]
  public async Task AnOverwriteOnlyPerspectiveThatDecidesNoRowAlone_IsReplayedInFullAsync() {
    var harness = new RbHarness { Traits = new RbTraits { IsOverwriteOnly = true } };
    var first = harness.Add(harness.Marked("a"));
    harness.Add(harness.Marked("b"));

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("ab")
      .Because("by default no event decides the row alone, so the whole stream is folded");
  }

  [Test]
  public async Task APhysicalFieldModelThatStripsNothing_WritesItsWholeDocumentAsync() {
    var harness = new RbHarness();
    harness.Runner.Physical = m => new Dictionary<string, object?> { ["marks"] = m.Marks };
    var marked = harness.Add(harness.Marked("a"));

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a");
  }

  [Test]
  public async Task ALiveApplyUnderACoordinator_TakesTheApplyLockAsync() {
    var coordinator = new RbCoordinator();
    var harness = new RbHarness { Coordinator = coordinator };
    var marked = harness.Add(harness.Marked("a"));

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]);

    await Assert.That(coordinator.Acquired).Count().IsEqualTo(1);
    await Assert.That(coordinator.Released).IsEqualTo(1);
  }

  // ===== Purged streams and failures =====

  [Test]
  public async Task APurgedStreamThatStaysPurged_ReportsNoPurgeOfItsOwnAsync() {
    var harness = new RbHarness();
    var markers = new RbMarkers();
    markers.PurgedStreams.Add(harness.StreamId);
    harness.Services.AddSingleton<IPerspectivePurgeMarkerStore>(markers);
    var bare = harness.Add(harness.Marked("a"), hops: false);
    var nulled = harness.Add(harness.Marked("b"), nullHops: true);

    var completion = await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [bare, nulled]);

    await Assert.That(completion.PurgedBy).IsNull().Because("no Apply of this batch purged; the stream already was");
    await Assert.That(completion.EventsProcessed).IsEqualTo(2);
    await Assert.That(harness.Rows.Has(harness.StreamId)).IsFalse();
  }

  [Test]
  public async Task AReplayOfAPurgedStream_SkipsItsEvents_WhateverTheyCarryAsync() {
    var harness = new RbHarness();
    var markers = new RbMarkers();
    markers.PurgedStreams.Add(harness.StreamId);
    harness.Services.AddSingleton<IPerspectivePurgeMarkerStore>(markers);
    var bare = harness.Add(harness.Marked("a"), hops: false);
    harness.Add(harness.Marked("b"), nullHops: true);

    var completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, bare.MessageId.Value);

    await Assert.That(completion.EventsProcessed).IsEqualTo(2);
    await Assert.That(harness.Rows.Has(harness.StreamId)).IsFalse();
  }

  [Test]
  public async Task AFailureBeforeAnyEventIsFolded_HasNoProgressToSave_EvenFromACursorAsync() {
    var harness = new RbHarness();
    var cursor = harness.Add(harness.Marked("a"));
    var next = harness.Add(harness.Marked("b"));
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.That(async () => await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, cursor.MessageId.Value, [next], canceled.Token))
      .Throws<OperationCanceledException>();
    await Assert.That(harness.Log.Count(LogLevel.Warning, "no progress to save")).IsEqualTo(1);
  }

  [Test]
  public async Task AFailureAfterAnApplyThatThrew_SavesTheRowAtThatEvent_WithTheTypeItHadAsync() {
    var harness = new RbHarness();
    harness.Runner.Apply = (_, _) => throw new InvalidOperationException("bad");
    harness.Rows.FlushFails = new InvalidOperationException("flush failed");
    var cursor = harness.Add(harness.Marked("a"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, cursor.MessageId.Value, eventType: null);
    harness.Rows.Versioned = false;
    var next = harness.Add(harness.Marked("b"), nullHops: true);

    await Assert.That(async () => await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, cursor.MessageId.Value, [next]))
      .Throws<InvalidOperationException>();
    await Assert.That(harness.Log.Count(LogLevel.Warning, "Saving partial progress")).IsEqualTo(1);
    await Assert.That(harness.Rows.MetadataOf(harness.StreamId).EventId).IsEqualTo(next.MessageId.Value.ToString("D"));
  }
}

/// <summary>A stream lifecycle ledger reporting one extent for every stream asked about.</summary>
internal sealed class RbExtents(ArchivedStreamExtent extent) : IArchivedStreamExtents {
  public Task<IReadOnlyDictionary<Guid, ArchivedStreamExtent>> GetExtentsAsync(IReadOnlyCollection<Guid> streamIds, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyDictionary<Guid, ArchivedStreamExtent>>(streamIds.ToDictionary(id => id, _ => extent));
}
