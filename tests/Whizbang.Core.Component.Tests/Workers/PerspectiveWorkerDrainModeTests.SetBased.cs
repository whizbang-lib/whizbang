// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The worker's set-based path (#2246), on both of its sources. The pairs of a batch whose perspective writes set-based
/// are taken under their affinity gates and apply locks in one order, prepared together, written with one call to their
/// context's sink, and completed with their own outcomes; each completion is then reported exactly as on the per-pair
/// path. A pair the batch hands back runs the per-pair path, and a service in the per-stream mode never prepares.
/// </summary>
/// <remarks>
/// Every stream is queued before the worker starts, so its first batch holds them all; each wait is on the completion
/// reports the assertions read.
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#the-batch</docs>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.DrainGroups.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.ClaimedGroups.cs</code-under-test>
public partial class PerspectiveWorkerDrainModeTests {
  private const string SET_BASED = "Tests.SetBased.Orders";
  private const string PER_STREAM = "Tests.SetBased.Ledger";
  private const string NO_RUNNER = "Tests.SetBased.Unresolvable";
  private const string SINK_KEY = "orders-context";

  static PerspectiveWorkerDrainModeTests() {
    // A generated runner registers its perspective's write mode when its assembly loads; these tests' runners are
    // fakes, so they register the way generated ones do.
    PerspectiveWriteModes.Register(SET_BASED, null);
    PerspectiveWriteModes.Register(PER_STREAM, PerspectiveWriteMode.PerStream);
    PerspectiveWriteModes.Register(NO_RUNNER, null);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_APerStreamPerspectiveOfTheSameStreams_RunsPerPairBesideTheBatchAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { AlsoPerStream = true };
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };

    await rig.DrainExpectingAsync(streams, expectedReports: 4, expectedFailures: 0, cancellationToken);

    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo(streams)
      .Because("the set-based perspective's rows are written by the batch");
    await Assert.That(rig.Runners.RunWithEventsCalls).IsEqualTo(2)
      .Because("the perspective that declares the per-stream mode is applied per pair, in the same drain");
    await Assert.That(rig.Coordinator.Reported.Count(c => c.PerspectiveName == PER_STREAM)).IsEqualTo(2);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_AnInversion_IsRewoundPerPair_AndTheOtherPairsAreBatchedAsync(CancellationToken cancellationToken) {
    var inverted = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { CommitSequenceAheadOf = inverted };

    await rig.DrainAsync([inverted, healthy], cancellationToken);

    await Assert.That(rig.Runners.Rewound).IsEquivalentTo([inverted])
      .Because("an event at or below the cursor rewinds its stream, as the per-pair path does");
    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo([healthy]);
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo([inverted, healthy]);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_AFinishThatThrows_FailsOnlyItsPairAsync(CancellationToken cancellationToken) {
    var broken = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { ReportThrowsFor = broken };

    await rig.DrainExpectingAsync([broken, healthy], expectedReports: 1, expectedFailures: 1, cancellationToken);

    await Assert.That(rig.Coordinator.Failed.Select(f => f.StreamId)).IsEquivalentTo([broken]);
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo([healthy]);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_ABatchThatOutlivesItsLease_FailsEveryPairOfTheBatchAsync(CancellationToken cancellationToken) {
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { OutliveLease = true };

    await rig.DrainExpectingAsync(streams, expectedReports: 0, expectedFailures: 2, cancellationToken);

    await Assert.That(rig.Coordinator.Failed.Select(f => f.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.Coordinator.Failed.All(f => f.Error!.Contains("Lease deadline exceeded", StringComparison.Ordinal))).IsTrue()
      .Because("a batch whose lease ran out is routed to the failure path, as one pair's would be, so its rows are re-issued");
    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_AReportThatOutlivesTheLease_FailsItsPairOnTheLeaseAsync(CancellationToken cancellationToken) {
    // One stream: once the batch's lease has run out, every pair still finishing meets it.
    var late = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { LeaseExpiresOnReportOf = late };

    await rig.DrainExpectingAsync([late], expectedReports: 0, expectedFailures: 1, cancellationToken);

    await Assert.That(rig.Coordinator.Failed.Single().StreamId).IsEqualTo(late);
    await Assert.That(rig.Coordinator.Failed.Single().Error).Contains("Lease deadline exceeded");
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_ARewindThatThrows_FailsOnlyItsPairAsync(CancellationToken cancellationToken) {
    var inverted = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { CommitSequenceAheadOf = inverted, RewindThrows = true };

    await rig.DrainExpectingAsync([inverted, healthy], expectedReports: 1, expectedFailures: 1, cancellationToken);

    await Assert.That(rig.Coordinator.Failed.Single().StreamId).IsEqualTo(inverted);
    await Assert.That(rig.Coordinator.Reported.Single().StreamId).IsEqualTo(healthy);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_ARewindThatOutlivesItsLease_FailsItsPairOnTheLeaseAsync(CancellationToken cancellationToken) {
    var inverted = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { CommitSequenceAheadOf = inverted, RewindOutlivesLease = true };

    await rig.DrainExpectingAsync([inverted, healthy], expectedReports: 1, expectedFailures: 1, cancellationToken);

    await Assert.That(rig.Coordinator.Failed.Single().Error).Contains("Lease deadline exceeded");
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_AWorkerStoppedMidBatch_ClearsItsStreamsInFlightMarkersAsync(CancellationToken cancellationToken) {
    var stream = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { HoldPrepare = true };

    var drain = await rig.StopDuringPrepareAsync([stream], cancellationToken);

    await Assert.That(drain.IsInFlight(stream)).IsFalse()
      .Because("a batch that never reached its later laps still clears the in-flight marker it set");
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_ARewindWhoseLockIsHeldElsewhere_IsSkipped_AndTheOtherPairsAreBatchedAsync(CancellationToken cancellationToken) {
    var rewound = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { RewindRequiredFor = rewound, RewindLockHeldElsewhere = true };

    await rig.ClaimExpectingAsync([rewound, healthy], expectedReports: 1, expectedFailures: 0, cancellationToken);

    await Assert.That(rig.Runners.Rewound).IsEmpty().Because("the rewind lock is another instance's");
    await Assert.That(rig.Coordinator.Reported.Single().StreamId).IsEqualTo(healthy);
  }

  [Test]
  public async Task IsLeaseExpiry_IsTheLeaseCanceledWhileTheWorkerRunsAsync() {
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();
    var running = CancellationToken.None;

    await Assert.That(PerspectiveWorker.IsLeaseExpiry(canceled.Token, running)).IsTrue()
      .Because("the lease ran out while the worker runs: the pair is failed and its rows re-issued");
    await Assert.That(PerspectiveWorker.IsLeaseExpiry(canceled.Token, canceled.Token)).IsFalse()
      .Because("the worker is stopping: the cancellation is let through, not recorded as a failure");
    await Assert.That(PerspectiveWorker.IsLeaseExpiry(running, running)).IsFalse()
      .Because("a cancellation from elsewhere, with the lease still live, is not an expiry");
  }

  [Test]
  [Timeout(30000)]
  public async Task AffinityGates_TakenForABatch_AreReleased_WhenTakingOneIsCanceledAsync(CancellationToken cancellationToken) {
    var worker = SetBasedRig.BareWorker();
    var stream = Guid.NewGuid();
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.That(async () => await worker.AcquireAffinityGatesInOrderAsync([(stream, SET_BASED)], "standard", canceled.Token))
      .Throws<OperationCanceledException>();

    using var taken = await worker.AcquireAffinityGatesInOrderAsync([(stream, SET_BASED)], "standard", cancellationToken);
    await Assert.That(worker.HasStreamAffinityGate(stream, SET_BASED)).IsTrue()
      .Because("the gate a canceled acquisition did not take is free to take at once");
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_ARewindTheCursorAsksFor_RunsPerPair_AndTheOtherPairsAreBatchedAsync(CancellationToken cancellationToken) {
    var rewound = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { RewindRequiredFor = rewound };

    await rig.ClaimAsync([rewound, healthy], cancellationToken);

    await Assert.That(rig.Runners.Rewound).IsEquivalentTo([rewound]);
    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo([healthy]);
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo([rewound, healthy]);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_ARunnerThatDoesNotExposeItsRead_RunsPerPairWithRunAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { ReadsNothing = true };
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };

    await rig.ClaimAsync(streams, cancellationToken);

    await Assert.That(rig.Runners.RunCalls).IsEqualTo(2);
    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(streams);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_AFailedPrepareAndAFailedFinish_FailOnlyTheirPairsAsync(CancellationToken cancellationToken) {
    var badPrepare = Guid.NewGuid();
    var badFinish = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { FailPrepareOf = badPrepare, ReportThrowsFor = badFinish };

    await rig.ClaimExpectingAsync([badPrepare, badFinish, healthy], expectedReports: 1, expectedFailures: 2, cancellationToken);

    await Assert.That(rig.Coordinator.Failed.Select(f => f.StreamId)).IsEquivalentTo([badPrepare, badFinish]);
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo([healthy]);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_APerStreamPairAndAnUnresolvableRunner_RunBesideTheBatchAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { AlsoPerStream = true, AlsoUnresolvable = true };
    var streams = new[] { Guid.NewGuid() };

    await rig.ClaimExpectingAsync(streams, expectedReports: 2, expectedFailures: 0, cancellationToken);

    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.Runners.RunCalls).IsEqualTo(1)
      .Because("the per-stream perspective's claimed pair runs RunAsync, as before set-based writes");
    await Assert.That(rig.Coordinator.Reported.Select(c => c.PerspectiveName)).IsEquivalentTo([SET_BASED, PER_STREAM])
      .Because("a perspective whose runner cannot be resolved ends without a report, as on the per-pair path");
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_PreparesEveryPair_WritesThemWithOneSinkCall_AndReportsEachCompletionAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased);
    var streams = new[] { Guid.Parse("00000000-0000-0000-0000-00000000000b"), Guid.Parse("00000000-0000-0000-0000-00000000000a") };

    await rig.DrainAsync(streams, cancellationToken);

    await Assert.That(rig.Sink.Calls).IsEqualTo(1).Because("the batch's rows are written with one call to their context's sink");
    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.Runners.Prepared.Select(p => p.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.Runners.Completed.Values).All().Satisfy(o => o.IsEqualTo(PerspectiveWriteStatus.Written));
    await Assert.That(rig.Runners.RunWithEventsCalls).IsEqualTo(0);
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(streams)
      .Because("each pair's completion is reported as the per-pair path reports it");
    await Assert.That(rig.Locks.Acquired).IsEquivalentTo(streams.Order().ToList(), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the apply locks are taken in one fixed order");
    await Assert.That(rig.GatesReleased.Select(r => r.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.StateSource.Planned!.Select(p => p.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.StateSource.Planned!.All(p => p.KnownNew)).IsTrue()
      .Because("no cursor, and each batch starts its stream: the state source plans no read for either");
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_APairHandedBack_RunsThePerPairPath_AndWritesNothingAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { HandBack = true };
    var stream = Guid.NewGuid();

    await rig.DrainAsync([stream], cancellationToken);

    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
    await Assert.That(rig.Runners.Completed.ContainsKey(stream)).IsTrue()
      .Because("the runner completes a pair it handed back, which runs it per pair under the batch's lock");
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo([stream]);
  }

  [Test]
  [Timeout(30000)]
  public async Task PerStreamMode_NeverPrepares_AndAppliesEachPairWithRunWithEventsAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.PerStream);
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };

    await rig.DrainAsync(streams, cancellationToken);

    await Assert.That(rig.Runners.Prepared).IsEmpty();
    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
    await Assert.That(rig.Runners.RunWithEventsCalls).IsEqualTo(2).Because("the per-stream mode is the path before set-based writes");
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(streams);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_APairWhoseRunnerCannotBeResolved_IsLeftOut_AndTheStreamsOtherPairRunsAsync(CancellationToken cancellationToken) {
    // A set-based pair the drain cannot begin (its runner is not resolved here) is left out of the lap's batch, as the
    // per-pair path leaves it; the stream's per-stream pair is applied and reported as usual.
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { AlsoPerStream = true, SetBasedUnresolvable = true };
    var stream = Guid.NewGuid();

    await rig.DrainExpectingAsync([stream], expectedReports: 1, expectedFailures: 0, cancellationToken);

    await Assert.That(rig.Coordinator.Reported.Select(c => c.PerspectiveName)).IsEquivalentTo([PER_STREAM]);
    await Assert.That(rig.Runners.Prepared).IsEmpty();
    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_ReadsEveryPairsCursorAndEvents_WithOneReadEachAsync(CancellationToken cancellationToken) {
    // #2246: the claimed batch reads its pairs' cursors with one statement and their streams' events with one read, and
    // hands each runner its stream's events: no pair reads its own cursor or events, before the apply or after it.
    var fresh = Guid.NewGuid();
    var resumed = Guid.NewGuid();
    var caughtUp = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) {
      BatchedReads = true,
      WithReceptors = true,
      ResumesAfterCursor = resumed,
      CaughtUp = caughtUp,
    };

    await rig.ClaimAsync([fresh, resumed, caughtUp], cancellationToken);

    await Assert.That(rig.Store.ManyReads.Count).IsEqualTo(1);
    await Assert.That(rig.Store.ManyReads[0].Single(r => r.StreamId == resumed).FromEventId)
      .IsEqualTo(rig.Coordinator.Cursors[resumed].LastEventId).Because("a stream is read after its pair's cursor");
    await Assert.That(rig.Store.ManyReads[0].Single(r => r.StreamId == fresh).FromEventId).IsNull();
    await Assert.That(rig.Coordinator.BatchCursorReads).IsEqualTo(1);
    await Assert.That(rig.Coordinator.SingleCursorReads).IsEqualTo(0);
    await Assert.That(rig.Runners.EventsRead).IsEmpty().Because("each runner selects its events from the batch's read");
    await Assert.That(rig.Runners.Selected).IsEquivalentTo([rig.EventOf(fresh), rig.EventOf(resumed)]);
    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo([fresh, resumed]);
    await Assert.That(rig.Runners.RunCalls).IsEqualTo(1)
      .Because("the caught-up pair's cursor is past its stream's event, so it has none to batch and runs per pair");
    await Assert.That(rig.Processed[fresh]).IsEquivalentTo([rig.EventOf(fresh)])
      .Because("the processed events the post-perspective stages see are the batch's read, not a second read");
    await Assert.That(rig.Processed[resumed]).IsEquivalentTo([rig.EventOf(resumed)]);
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo([fresh, resumed, caughtUp]);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_ARewindFlagWithNoTrigger_IsBatched_AndAReadThatThrows_FailsOnlyItsPairAsync(CancellationToken cancellationToken) {
    var flagged = Guid.NewGuid();
    var broken = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { RewindFlagWithoutTriggerFor = flagged, ReadThrowsFor = broken };

    await rig.ClaimExpectingAsync([flagged, broken, healthy], expectedReports: 2, expectedFailures: 1, cancellationToken);

    await Assert.That(rig.Runners.Rewound).IsEmpty().Because("a rewind needs the event that triggered it");
    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo([flagged, healthy]);
    await Assert.That(rig.Coordinator.Failed.Select(f => f.StreamId)).IsEquivalentTo([broken])
      .Because("a pair whose read throws is failed as the per-pair path fails it, and the batch goes on");
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_ABatchedReadThatFails_LeavesEachPairToItsOwnReadsAsync(CancellationToken cancellationToken) {
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { BatchedReads = true, BatchedReadFails = true };

    await rig.ClaimAsync(streams, cancellationToken);

    await Assert.That(rig.Coordinator.SingleCursorReads).IsEqualTo(2);
    await Assert.That(rig.Runners.EventsRead).IsEquivalentTo(streams);
    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo(streams)
      .Because("a failed batched read costs the batch its saving, never its pairs");
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_WithNoEventTypesOrNoEventStore_OrCursorsReadPerPair_EachPairReadsItsOwnAsync(CancellationToken cancellationToken) {
    foreach (var rig in new[] {
        new SetBasedRig(PerspectiveWriteMode.SetBased) { BatchedReads = true, NoEventTypes = true },
        new SetBasedRig(PerspectiveWriteMode.SetBased) { BatchedReads = true, NoEventStore = true },
        new SetBasedRig(PerspectiveWriteMode.SetBased) { BatchedReads = true, CursorsPerPair = true },
    }) {
      var streams = new[] { Guid.NewGuid() };

      await rig.ClaimAsync(streams, cancellationToken);

      await Assert.That(rig.Store.ManyReads).IsEmpty();
      await Assert.That(rig.Coordinator.BatchCursorReads).IsEqualTo(0);
      await Assert.That(rig.Runners.EventsRead).IsEquivalentTo(streams);
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_ReadsEachPairsEvents_AndWritesThemWithOneSinkCallAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased);
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };

    await rig.ClaimAsync(streams, cancellationToken);

    await Assert.That(rig.Sink.Calls).IsEqualTo(1);
    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.Runners.EventsRead).IsEquivalentTo(streams)
      .Because("a claimed pair's events are read with the runner's own read, which RunAsync makes");
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(streams);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_AFailedPrepare_FailsOnlyItsPairAsync(CancellationToken cancellationToken) {
    var broken = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { FailPrepareOf = broken };

    await rig.DrainExpectingAsync([broken, healthy], expectedReports: 1, expectedFailures: 1, cancellationToken);

    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo([healthy]);
    await Assert.That(rig.Coordinator.Failed.Select(f => f.StreamId)).IsEquivalentTo([broken])
      .Because("a pair whose apply threw is failed as the per-pair path fails it; its batch's other pairs are written");
    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo([healthy]);
  }

  /// <summary>A worker over fakes, with the set-based seams registered and one fake runner for <see cref="SET_BASED"/>.</summary>
  private sealed class SetBasedRig(PerspectiveWriteMode mode) {
    public bool HandBack { get; init; }
    public Guid? FailPrepareOf { get; init; }
    public Guid? ReportThrowsFor { get; init; }
    public Guid? CommitSequenceAheadOf { get; init; }
    public Guid? RewindRequiredFor { get; init; }
    public bool ReadsNothing { get; init; }
    public bool AlsoPerStream { get; init; }
    public bool AlsoUnresolvable { get; init; }
    public bool OutliveLease { get; init; }
    public Guid? LeaseExpiresOnReportOf { get; init; }
    public bool RewindThrows { get; init; }
    public bool RewindOutlivesLease { get; init; }
    public bool HoldPrepare { get; init; }
    public bool RewindLockHeldElsewhere { get; init; }
    public bool BatchedReads { get; init; }
    public bool BatchedReadFails { get; init; }
    public bool NoEventTypes { get; init; }
    public bool NoEventStore { get; init; }
    public bool WithReceptors { get; init; }
    public bool CursorsPerPair { get; init; }
    public Guid? RewindFlagWithoutTriggerFor { get; init; }
    public bool SetBasedUnresolvable { get; init; }
    public Guid? ReadThrowsFor { get; init; }
    public Guid? ResumesAfterCursor { get; init; }
    public Guid? CaughtUp { get; init; }
    public DrainModeEventStore Store { get; private set; } = null!;
    public ConcurrentDictionary<Guid, IReadOnlyList<Guid>> Processed { get; } = new();
    private Dictionary<Guid, MessageEnvelope<IEvent>> _events = [];
    public Guid EventOf(Guid streamId) => _events[streamId].MessageId.Value;
    public RecordingSink Sink { get; } = new();
    public RecordingLocks Locks { get; } = new();
    public PlanningStateSource StateSource { get; } = new();
    public SetBasedRunners Runners { get; private set; } = null!;
    public ReportingCoordinator Coordinator { get; } = new();
    public ConcurrentQueue<PerspectiveWorker.AffinityGateRelease> GatesReleased { get; } = new();

    public Task DrainAsync(Guid[] streams, CancellationToken ct) =>
      _runAsync(streams, drain: true, streams.Length, 0, ct);

    public Task DrainExpectingAsync(Guid[] streams, int expectedReports, int expectedFailures, CancellationToken ct) =>
      _runAsync(streams, drain: true, expectedReports, expectedFailures, ct);

    public Task ClaimAsync(Guid[] streams, CancellationToken ct) => _runAsync(streams, drain: false, streams.Length, 0, ct);

    public Task ClaimExpectingAsync(Guid[] streams, int expectedReports, int expectedFailures, CancellationToken ct) =>
      _runAsync(streams, drain: false, expectedReports, expectedFailures, ct);

    /// <summary>Drains the streams, stops the worker once a prepare has begun, and hands back the drain channel.</summary>
    public async Task<IPerspectiveDrainChannel> StopDuringPrepareAsync(Guid[] streams, CancellationToken ct) {
      _stopOnPrepare = true;
      await _runAsync(streams, drain: true, 0, 0, ct);
      return _drainChannel!;
    }

    private bool _stopOnPrepare;
    private IPerspectiveDrainChannel? _drainChannel;

    /// <summary>A worker over nothing, for the seams a test holds directly.</summary>
    public static PerspectiveWorker BareWorker() {
      var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
      var options = new PerspectiveWorkerOptions();
      var harness = new PerspectiveWorkerTestHarness();
      return new PerspectiveWorker(
        instanceProvider: new FakeServiceInstanceProvider(),
        scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
        options: Options.Create(options),
        tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
        completionStrategy: new InstantCompletionStrategy(logger: NullLogger<InstantCompletionStrategy>.Instance),
        eventTypeProvider: new FakeEventTypeProvider([typeof(DrainModeTestEvent)]),
        syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
        syncEventTracker: new SyncEventTracker(),
        logger: NullLogger<PerspectiveWorker>.Instance,
        snapshotStore: NullPerspectiveSnapshotStore.Instance,
        streamLocker: NullPerspectiveStreamLocker.Instance,
        streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
        streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
        processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
        workChannelWriter: new WorkChannelWriter(),
        rewindOptions: Options.Create(new PerspectiveRewindOptions()),
        perspectiveChannelWriter: harness.ChannelWriter,
        perspectiveCompletionChannel: harness.CompletionCapture,
        failureChannel: harness.FailureCapture,
        leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
        perspectiveDrainChannel: harness.DrainChannel,
        leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
        leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
        deadLetterStore: NullDeadLetterStore.Instance,
        generationProvider: new DefaultGenerationProvider(),
        perspectiveNotificationListener: new NoOpWorkNotificationListener(),
        governor: PerspectiveWorker.CreateDefaultGovernor(options));
    }

    private async Task _runAsync(Guid[] streams, bool drain, int expectedReports, int expectedFailures, CancellationToken ct) {
      // Every timestamp comes from the fake clock. A cursor sits a minute before every event, or a minute after:
      // version 7 ids made in the same millisecond carry random bits after the timestamp, so only a different
      // timestamp orders a cursor before or after an event for certain.
      var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
      var now = time.GetUtcNow();
      var behindEvents = Guid.CreateVersion7(now.AddMinutes(-1));
      var aheadOfEvents = Guid.CreateVersion7(now.AddMinutes(1));
      var events = streams.ToDictionary(s => s, s => new MessageEnvelope<IEvent> {
        MessageId = new MessageId(Guid.CreateVersion7(now)),
        Payload = new DrainModeTestEvent($"set-based-{s}"),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        IsFirstInLocalStream = true,
      });
      _events = events;
      var prepareEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      Action? onPrepare = null;
      if (OutliveLease) {
        onPrepare = () => time.Advance(TimeSpan.FromHours(1));
      }
      if (HoldPrepare) {
        onPrepare = prepareEntered.SetResult;
      }
      Runners = new SetBasedRunners(events) {
        HandBack = HandBack,
        FailPrepareOf = FailPrepareOf,
        ReadsNothing = ReadsNothing,
        AlsoPerStream = AlsoPerStream,
        OnPrepare = onPrepare,
        HoldPrepare = HoldPrepare,
        RewindThrows = RewindThrows,
        ReadThrowsFor = ReadThrowsFor,
        SetBasedUnresolvable = SetBasedUnresolvable,
        OnRewind = RewindOutlivesLease ? () => time.Advance(TimeSpan.FromHours(1)) : null,
      };
      Coordinator.Expect(expectedReports, expectedFailures);
      Coordinator.ReadsCursorsInBatches = !CursorsPerPair;
      Coordinator.ReportThrowsFor = ReportThrowsFor;
      Coordinator.LeaseExpiresOnReportOf = LeaseExpiresOnReportOf;
      Coordinator.OnLeaseExpiry = () => time.Advance(TimeSpan.FromHours(1));
      if (CommitSequenceAheadOf is { } ahead) {
        Coordinator.Cursors[ahead] = new PerspectiveCursorInfo { StreamId = ahead, PerspectiveName = SET_BASED, LastEventId = aheadOfEvents, LastCommitSequence = 100 };
      }
      if (RewindRequiredFor is { } rewind) {
        Coordinator.Cursors[rewind] = new PerspectiveCursorInfo {
          StreamId = rewind,
          PerspectiveName = SET_BASED,
          LastEventId = aheadOfEvents,
          Status = PerspectiveProcessingStatus.RewindRequired,
          RewindTriggerEventId = events[rewind].MessageId.Value,
        };
      }
      if (RewindFlagWithoutTriggerFor is { } flagged) {
        Coordinator.Cursors[flagged] = new PerspectiveCursorInfo {
          StreamId = flagged,
          PerspectiveName = SET_BASED,
          LastEventId = behindEvents,
          Status = PerspectiveProcessingStatus.RewindRequired,
        };
      }
      if (ResumesAfterCursor is { } resumes) {
        Coordinator.Cursors[resumes] = new PerspectiveCursorInfo { StreamId = resumes, PerspectiveName = SET_BASED, LastEventId = behindEvents };
      }
      if (CaughtUp is { } caughtUp) {
        Coordinator.Cursors[caughtUp] = new PerspectiveCursorInfo { StreamId = caughtUp, PerspectiveName = SET_BASED, LastEventId = aheadOfEvents };
      }
      string[] perspectives = AlsoPerStream ? [SET_BASED, PER_STREAM] : [SET_BASED];
      Coordinator.StreamEventsToReturn = [.. streams.SelectMany(s => perspectives.Select(p => new StreamEventData {
        StreamId = s,
        EventId = events[s].MessageId.Value,
        EventType = TypeNameFormatter.Format(typeof(DrainModeTestEvent)),
        EventData = JsonSerializer.Serialize(new DrainModeTestEvent("set-based")),
        Metadata = null,
        Scope = null,
        EventWorkId = Guid.NewGuid(),
        PerspectiveName = p,
        CommitSequence = s == CommitSequenceAheadOf ? 50 : null,
      }))];
      var eventStore = new DrainModeEventStore { DeserializedEventsToReturn = [.. events.Values] };
      if (BatchedReads) {
        // Every stream asked for, with its one event: the worker cuts each to the events after its pair's cursor.
        eventStore.ReadMany = asked => BatchedReadFails
          ? throw new InvalidOperationException("the batched read failed")
          : asked.ToDictionary(a => a.StreamId, a => (IReadOnlyList<MessageEnvelope<IEvent>>)[events[a.StreamId]]);
      }
      Store = eventStore;
      Runners.Selects = BatchedReads;
      var eventTypeProvider = new FakeEventTypeProvider(NoEventTypes ? [] : [typeof(DrainModeTestEvent)]);
      var instanceProvider = new FakeServiceInstanceProvider();
      var harness = new PerspectiveWorkerTestHarness();

      var services = new ServiceCollection();
      services.TryAddWhizbangDefaults();
      services.AddSingleton<IWorkCoordinator>(Coordinator);
      services.AddSingleton<IPerspectiveRunnerRegistry>(Runners);
      services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
      if (!NoEventStore) {
        services.AddSingleton<IEventStore>(eventStore);
      }
      if (WithReceptors) {
        services.AddSingleton<IReceptorInvoker>(new NoReceptors());
      }
      services.AddSingleton<IEventTypeProvider>(eventTypeProvider);
      services.AddSingleton<IPerspectiveApplyCoordinator>(Locks);
      services.AddSingleton<IPerspectiveStateSource>(StateSource);
      services.AddKeyedSingleton<IPerspectiveWriteSink>(SINK_KEY, Sink);
      services.AddLogging();
      var provider = services.BuildServiceProvider();

      var options = new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 50,
        MaxConcurrentDrainConsumers = 1,
        WriteMode = mode,
        // Every stream is queued before the worker starts, so the drain's coalescing window has nothing to wait for;
        // closed at once, it never waits on the test's fake clock.
        DrainBatcher = new SlidingWindowBatcherOptions { SlidingWindow = TimeSpan.Zero, MaxWait = TimeSpan.Zero, MaxSize = 1000 },
      };
      var worker = new PerspectiveWorker(
        instanceProvider: instanceProvider,
        scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
        options: Options.Create(options),
        tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
        completionStrategy: new InstantCompletionStrategy(logger: NullLogger<InstantCompletionStrategy>.Instance),
        eventTypeProvider: eventTypeProvider,
        syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
        syncEventTracker: new SyncEventTracker(),
        logger: NullLogger<PerspectiveWorker>.Instance,
        snapshotStore: NullPerspectiveSnapshotStore.Instance,
        streamLocker: RewindLockHeldElsewhere ? new HeldElsewhereLocker() : NullPerspectiveStreamLocker.Instance,
        streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
        streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
        processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
        workChannelWriter: new WorkChannelWriter(),
        rewindOptions: Options.Create(new PerspectiveRewindOptions()),
        perspectiveChannelWriter: harness.ChannelWriter,
        perspectiveCompletionChannel: harness.CompletionCapture,
        failureChannel: harness.FailureCapture,
        leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
        perspectiveDrainChannel: harness.DrainChannel,
        leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
        leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
        deadLetterStore: NullDeadLetterStore.Instance,
        generationProvider: new DefaultGenerationProvider(),
        perspectiveNotificationListener: new NoOpWorkNotificationListener(),
        governor: PerspectiveWorker.CreateDefaultGovernor(options),
        timeProvider: time);
      _drainChannel = harness.DrainChannel;
      worker.OnStreamAffinityGateReleased += GatesReleased.Enqueue;
      worker.OnPerspectiveEventProcessed += processed => Processed[processed.StreamId] = processed.EventIds;

      // Queued before the worker starts, so its first batch holds every stream.
      foreach (var stream in streams) {
        if (drain) {
          await harness.EnqueueDrainStreamAsync(stream, ct);
        } else {
          foreach (var perspective in perspectives.Concat(AlsoUnresolvable ? [NO_RUNNER] : Array.Empty<string>())) {
            await harness.EnqueueWorkAsync(new PerspectiveWork { WorkId = Guid.NewGuid(), StreamId = stream, PerspectiveName = perspective }, ct);
          }
        }
      }
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      await worker.StartAsync(cts.Token);
      await (_stopOnPrepare ? prepareEntered.Task : Coordinator.AllReported).WaitAsync(ct);
      await cts.CancelAsync();
      await worker.ExecuteTask!.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      await provider.DisposeAsync();
    }
  }

  /// <summary>The fake runner of <see cref="SET_BASED"/>: it prepares a write for its context's sink, or hands the pair back.</summary>
  private sealed class SetBasedRunners(Dictionary<Guid, MessageEnvelope<IEvent>> events) : IPerspectiveRunnerRegistry {
    public bool HandBack { get; init; }
    public Guid? FailPrepareOf { get; init; }
    public bool ReadsNothing { get; init; }
    public bool AlsoPerStream { get; init; }
    public Action? OnPrepare { get; init; }
    public bool HoldPrepare { get; init; }
    public bool RewindThrows { get; init; }
    public Action? OnRewind { get; init; }
    public Guid? ReadThrowsFor { get; init; }
    public ConcurrentQueue<Guid> Rewound { get; } = new();
    private int _runCalls;
    public int RunCalls => Volatile.Read(ref _runCalls);
    public ConcurrentQueue<PreparedPerspectiveApply> Prepared { get; } = new();
    public ConcurrentDictionary<Guid, PerspectiveWriteStatus?> Completed { get; } = new();
    public ConcurrentQueue<Guid> EventsRead { get; } = new();
    public bool Selects { get; set; }
    public ConcurrentQueue<Guid> Selected { get; } = new();
    private int _runWithEvents;
    public int RunWithEventsCalls => Volatile.Read(ref _runWithEvents);

    public bool SetBasedUnresolvable { get; init; }

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      (perspectiveName == PER_STREAM || (perspectiveName == SET_BASED && !SetBasedUnresolvable)) ? new Runner(this, perspectiveName) : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => AlsoPerStream
      ? [_info(SET_BASED), _info(PER_STREAM)]
      : [_info(SET_BASED)];

    private static PerspectiveRegistrationInfo _info(string name) =>
      new(name, "global::" + name, "global::Tests.SetBased.Order", [TypeNameFormatter.Format(typeof(DrainModeTestEvent))]);

    public IReadOnlyList<Type> GetEventTypes() => [typeof(DrainModeTestEvent)];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();

    private PerspectiveCursorCompletion _completion(Guid streamId, string perspectiveName = SET_BASED) => new() {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = events[streamId].MessageId.Value,
      Status = PerspectiveProcessingStatus.Completed,
      RowChanged = true,
      ProcessedEventIds = [events[streamId].MessageId.Value],
    };

    private sealed class PreparedPair(Guid streamId, IReadOnlyList<MessageEnvelope<IEvent>> events, PendingPerspectiveWrite? write, PerspectivePerPairReason reason)
      : PreparedPerspectiveApply(streamId, SET_BASED, null, events, write, reason);

    private sealed class Runner(SetBasedRunners owner, string name) : IPerspectiveRunner {
      public Type PerspectiveType => typeof(Runner);

      public Task<IReadOnlyList<MessageEnvelope<IEvent>>?> ReadEventsAsync(Guid streamId, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) {
        if (streamId == owner.ReadThrowsFor) {
          throw new InvalidOperationException("the read threw");
        }
        owner.EventsRead.Enqueue(streamId);
        return Task.FromResult<IReadOnlyList<MessageEnvelope<IEvent>>?>(owner.ReadsNothing ? null : [owner._eventsOf(streamId)]);
      }

      public IReadOnlyList<MessageEnvelope<IEvent>>? SelectEvents(IReadOnlyList<MessageEnvelope<IEvent>> streamEvents) {
        if (!owner.Selects) {
          return null;
        }
        foreach (var envelope in streamEvents) {
          owner.Selected.Enqueue(envelope.MessageId.Value);
        }
        return streamEvents;
      }

      public Task<PreparedPerspectiveApply> PrepareAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
          IReadOnlyList<MessageEnvelope<IEvent>> events, IPerspectiveBatchState? batchState, CancellationToken cancellationToken = default) {
        owner.OnPrepare?.Invoke();
        if (owner.HoldPrepare) {
          return _heldAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (streamId == owner.FailPrepareOf) {
          throw new InvalidOperationException("the fold threw");
        }
        PreparedPerspectiveApply prepared = owner.HandBack
          ? new PreparedPair(streamId, events, null, PerspectivePerPairReason.RewindBelowPosition)
          : new PreparedPair(streamId, events, PendingPerspectiveWrite.Purge("wh_per_orders", streamId).ForSink(SINK_KEY), PerspectivePerPairReason.None);
        owner.Prepared.Enqueue(prepared);
        return Task.FromResult(prepared);
      }

      public Task<PerspectiveCursorCompletion> CompleteAsync(PreparedPerspectiveApply prepared, PerspectiveWriteOutcome? outcome, CancellationToken cancellationToken = default) {
        owner.Completed[prepared.StreamId] = outcome?.Status;
        return Task.FromResult(owner._completion(prepared.StreamId));
      }

      public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) {
        Interlocked.Increment(ref owner._runCalls);
        return Task.FromResult(owner._completion(streamId, name));
      }

      public Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
          IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
        Interlocked.Increment(ref owner._runWithEvents);
        return Task.FromResult(owner._completion(streamId, name));
      }

      private static async Task<PreparedPerspectiveApply> _heldAsync(CancellationToken cancellationToken) {
        await Task.Delay(Timeout.Infinite, cancellationToken); // review-checks:allow: waits on cancellation only, never on time; the worker's stop is the signal
        throw new InvalidOperationException("a held prepare ends only when the worker stops");
      }

      public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) {
        if (owner.RewindThrows) {
          throw new InvalidOperationException("the rewind threw");
        }
        if (owner.OnRewind is { } onRewind) {
          onRewind();
          cancellationToken.ThrowIfCancellationRequested();
        }
        owner.Rewound.Enqueue(streamId);
        return Task.FromResult(owner._completion(streamId, name));
      }

      public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    }

    private MessageEnvelope<IEvent> _eventsOf(Guid streamId) => events[streamId];
  }

  /// <summary>A coordinator that serves the drain fetch and signals once the expected reports have all arrived.</summary>
  private sealed class ReportingCoordinator : IWorkCoordinator {
    private TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _expectedReports;
    private int _expectedFailures;
    public List<StreamEventData> StreamEventsToReturn { get; set; } = [];
    public Guid? ReportThrowsFor { get; set; }
    public Guid? LeaseExpiresOnReportOf { get; set; }
    public Action? OnLeaseExpiry { get; set; }
    public ConcurrentDictionary<Guid, PerspectiveCursorInfo> Cursors { get; } = new();
    public ConcurrentQueue<PerspectiveCursorCompletion> Reported { get; } = new();
    public ConcurrentQueue<PerspectiveCursorFailure> Failed { get; } = new();
    public Task AllReported => _all.Task;

    public void Expect(int reports, int failures) {
      _expectedReports = reports;
      _expectedFailures = failures;
      _all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void _check() {
      if (Reported.Count >= _expectedReports && Failed.Count >= _expectedFailures) {
        _all.TrySetResult();
      }
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [], PerspectiveStreamIds = [] });

    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default) {
      var rows = StreamEventsToReturn.Where(r => streamIds.Contains(r.StreamId)).ToList();
      // Each row is served once: the drain's refetch finds nothing more and ends.
      StreamEventsToReturn = [.. StreamEventsToReturn.Except(rows)];
      return Task.FromResult(rows);
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      if (completion.StreamId == ReportThrowsFor) {
        throw new InvalidOperationException("the report failed");
      }
      if (completion.StreamId == LeaseExpiresOnReportOf) {
        OnLeaseExpiry!();
        cancellationToken.ThrowIfCancellationRequested();
      }
      Reported.Enqueue(completion);
      _check();
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) {
      Failed.Enqueue(failure);
      _check();
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    private int _singleCursorReads;
    private int _batchCursorReads;
    public int SingleCursorReads => Volatile.Read(ref _singleCursorReads);
    public int BatchCursorReads => Volatile.Read(ref _batchCursorReads);

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      if (perspectiveName == SET_BASED) {
        Interlocked.Increment(ref _singleCursorReads);
      }
      return Task.FromResult(perspectiveName == SET_BASED && Cursors.TryGetValue(streamId, out var cursor) ? cursor : null);
    }

    public bool ReadsCursorsInBatches { get; set; } = true;

    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(Guid[] streamIds, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _batchCursorReads);
      return Task.FromResult(Cursors.Values.Where(c => streamIds.Contains(c.StreamId)).ToList());
    }
  }

  /// <summary>A receptor invoker with no receptors: the lifecycle stages run, and the processed events are loaded.</summary>
  private sealed class NoReceptors : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  /// <summary>A stream locker whose every lock is another instance's.</summary>
  private sealed class HeldElsewhereLocker : IPerspectiveStreamLocker {
    public Task<bool> TryAcquireLockAsync(Guid streamId, string perspectiveName, Guid instanceId, string reason, CancellationToken ct = default) =>
      Task.FromResult(false);
    public Task RenewLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReleaseLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
  }

  private sealed class RecordingSink : IPerspectiveWriteSink {
    private readonly ConcurrentQueue<PendingPerspectiveWrite> _written = new();
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public IReadOnlyList<PendingPerspectiveWrite> Written => [.. _written];

    public Task<IReadOnlyList<PerspectiveWriteOutcome>> WriteAsync(IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _calls);
      foreach (var write in writes) {
        _written.Enqueue(write);
      }
      return Task.FromResult<IReadOnlyList<PerspectiveWriteOutcome>>([.. writes.Select(_ => PerspectiveWriteOutcome.Written)]);
    }
  }

  private sealed class RecordingLocks : IPerspectiveApplyCoordinator {
    private readonly ConcurrentQueue<Guid> _acquired = new();
    public IReadOnlyList<Guid> Acquired => [.. _acquired];

    public Task<IAsyncDisposable> AcquireAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      _acquired.Enqueue(streamId);
      return Task.FromResult<IAsyncDisposable>(new Nothing());
    }

    private sealed class Nothing : IAsyncDisposable {
      public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
  }

  private sealed class PlanningStateSource : IPerspectiveStateSource {
    public IReadOnlyList<PerspectiveStateRequest>? Planned { get; private set; }

    public IPerspectiveBatchState BeginBatch(IReadOnlyList<PerspectiveStateRequest> pairs) {
      Planned = pairs;
      return new BatchedPerspectiveStateSource().BeginBatch(pairs);
    }
  }
}
