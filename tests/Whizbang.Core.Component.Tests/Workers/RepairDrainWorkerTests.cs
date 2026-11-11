// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The paced repair drain's tick, deterministically: token pacing gates the claim budget,
/// claimed rows dispatch grouped per (origin, tenant, type, window), pre-stamp rows ask for whole history, and unlearned origins are never claimed:
/// no attempt budget burns on a request that could not leave the process.
/// </summary>
/// <docs>proposals/paced-repair-drain</docs>
/// <remarks>
/// Serialized alongside the other worker timing tests. The ExecuteAsync tests below start a real
/// BackgroundService and drive its loop through many drain iterations in quick succession; run in
/// parallel that saturates the host, and the doorbell liveness tests next door explicitly depend
/// on an unsaturated host to distinguish a doorbell-driven claim from a poll-driven one. Leaving
/// this class parallel made ClaimWorkerDoorbellLivenessTests fail reproducibly.
/// </remarks>
[NotInParallel(Order = 106)]
public class RepairDrainWorkerTests {
  // A fixed instant: these tests pass "now" explicitly, so it never needs the real clock.
  private static readonly DateTimeOffset _fixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);


  [Test]
  public async Task DrainTick_DispatchesGroupedPerTypeAndWindow_NeverWideningAStreamPastItsOwnWindowAsync() {
    // #2034: the union of a group's windows re-shipped every stream over the widest range any row of the group had.
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: true);
    var s1 = TrackedGuid.New().Value;
    var s2 = TrackedGuid.New().Value;
    var s3 = TrackedGuid.New().Value;
    var s4 = TrackedGuid.New().Value;
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", s1, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", s2, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", s3, 200, 600),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeB", s4, 300, 700),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(3)
      .Because("four rows in three (origin, tenant, type, window) groups → three directed requests");
    await Assert.That(transport.Published.All(p =>
        Whizbang.Core.Transports.ControlMessageTtl.FromMetadata(p.Destination.Metadata) == TimeSpan.FromMinutes(60))).IsTrue()
      .Because("#2078: a paced repair request expires at the broker");
    var typeA = transport.Published
      .Select(p => _deserializeRedelivery(p.Envelope))
      .Where(r => r.EventTypes!.Contains("Contracts.TypeA"))
      .ToList();
    var shared = typeA.Single(r => r.StreamIds!.Count == 2);
    await Assert.That(shared.StreamIds).IsEquivalentTo([s1, s2])
      .Because("rows that disagreed over the same window share one request");
    await Assert.That(shared.FromCommitSequence).IsEqualTo(99L)
      .Because("the group's window, exclusive floor, exactly the burst path's conversion");
    await Assert.That(shared.ToCommitSequence).IsEqualTo(499L)
      .Because("inclusive ceiling of the group's own window, not the widest in the type");
    var own = typeA.Single(r => r.StreamIds!.Count == 1);
    await Assert.That(own.FromCommitSequence).IsEqualTo(199L);
    await Assert.That(own.ToCommitSequence).IsEqualTo(599L);
  }

  [Test]
  public async Task DrainTick_ARowThatNamesItsMissingEvent_AsksForThatEventOnly_ApartFromWholeStreamRowsAsync() {
    // #2103: a bucket missing exactly one event records that event's id; its request names it, so the owner ships
    // one event instead of the stream. Rows that name none still ask for their streams over the window, separately,
    // because an id list narrows a request to those ids.
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: true);
    var named = TrackedGuid.New().Value;
    var missing = TrackedGuid.New().Value;
    var whole = TrackedGuid.New().Value;
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", named, 100, 500, missing),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", whole, 100, 500),
    ]);

    await worker.DrainTickAsync(1.0, _fixedNow, CancellationToken.None);

    var requests = transport.Published.ConvertAll(p => _deserializeRedelivery(p.Envelope));
    await Assert.That(requests.Count).IsEqualTo(2);
    var one = requests.Single(r => r.EventIds is { Count: > 0 });
    await Assert.That(one.EventIds).IsEquivalentTo([missing]);
    await Assert.That(one.StreamIds).IsEquivalentTo([named])
      .Because("the stream and window stay on the request, so an owner that cannot name the event answers the range");
    await Assert.That(one.FromCommitSequence).IsEqualTo(99L);
    var rest = requests.Single(r => r.EventIds is null);
    await Assert.That(rest.StreamIds).IsEquivalentTo([whole]);
  }

  [Test]
  public async Task DrainTick_MarksTheDispatchedBucketsAnswerPending_AndWithdrawsAGroupWhoseSendFailedAsync() {
    // #2029: a bucket whose request left is not claimed again while its answer is on the way. #2223: every group is
    // recorded before it is sent, and a group whose send failed never asked, so its record is withdrawn.
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
      RepairAnswerTimeoutMinutes = 45,
    }, origin, learnTopic: true);
    transport.FailFirst = 1;
    var failed = TrackedGuid.New().Value;
    var sent = TrackedGuid.New().Value;
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", failed, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeB", sent, 300, 700),
    ]);
    var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    await worker.DrainTickAsync(1.0, now, CancellationToken.None);

    await Assert.That(coordinator.PendingMarks.Count).IsEqualTo(2)
      .Because("each group is recorded as awaiting its answer before its request is sent");
    await Assert.That(coordinator.Withdrawals.Count).IsEqualTo(1);
    var withdrawal = coordinator.Withdrawals[0];
    await Assert.That(withdrawal.Keys.Select(k => k.StreamId)).IsEquivalentTo([failed])
      .Because("only the group whose send failed is taken back");
    await Assert.That(withdrawal.Origin).IsEqualTo(origin);
    await Assert.That(withdrawal.Now).IsEqualTo(now);
    await Assert.That(withdrawal.Timeout).IsEqualTo(TimeSpan.FromMinutes(45))
      .Because("the withdrawal names the record the mark made, so it cannot take back a later one");
    await Assert.That(coordinator.PendingKeys.Select(k => k.StreamId)).IsEquivalentTo([sent]);
    var mark = coordinator.PendingMarks.Single(m => m.Keys[0].StreamId == sent);
    await Assert.That(mark.Keys.Select(k => k.StreamId)).IsEquivalentTo([sent]);
    await Assert.That(mark.Keys[0].TenantScope).IsEqualTo("tenant-a");
    await Assert.That(mark.Keys[0].EventType).IsEqualTo("Contracts.TypeB");
    await Assert.That(mark.Origin).IsEqualTo(origin);
    await Assert.That(mark.Now).IsEqualTo(now);
    await Assert.That(mark.Timeout).IsEqualTo(TimeSpan.FromMinutes(45));
  }

  [Test]
  public async Task DrainTick_NothingSent_LeavesNothingPendingAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: true);
    transport.FailFirst = 1;
    coordinator.Eligible.Add(new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500));

    await worker.DrainTickAsync(1.0, _fixedNow, CancellationToken.None);

    await Assert.That(coordinator.PendingKeys).IsEmpty()
      .Because("the one request recorded before its send was withdrawn when the send failed");
    await Assert.That(coordinator.Withdrawals.Count).IsEqualTo(1);
  }

  [Test]
  public async Task DrainTick_RecordsTheAnswerPending_BeforeTheRequestLeaves_SoAnAnswerThatOutrunsThePublishIsKeptAsync() {
    // #2223: the origin may answer before the publish call returns (a fast origin, or this host's continuation delayed
    // under load). An answer clears the bucket's wait; a mark written after it would put the wait back, and the bucket
    // would count against the outstanding-answer cap until the answer timeout. The fake answers inside the publish.
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: true);
    var stream = TrackedGuid.New().Value;
    coordinator.Eligible.Add(new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", stream, 100, 500));
    var marksWhenPublished = -1;
    transport.OnPublish = () => {
      marksWhenPublished = coordinator.PendingMarks.Count;
      coordinator.Answer(stream);
    };

    await worker.DrainTickAsync(1.0, _fixedNow, CancellationToken.None);

    await Assert.That(marksWhenPublished).IsEqualTo(1)
      .Because("the bucket is recorded as awaiting its answer before the request can reach the origin");
    await Assert.That(coordinator.PendingKeys).IsEmpty()
      .Because("the answer that landed inside the publish stands; nothing after it records the bucket as waiting again");
    await Assert.That(coordinator.Withdrawals).IsEmpty();
  }

  [Test]
  public async Task DrainTick_OneFailedGroupSend_StillDispatchesTheRemainingGroupsAsync() {
    // Observed live: one throttled broker send threw out of the tick and killed every LATER
    // group's dispatch — their claimed rows had already stamped an attempt, so the failure
    // burned backoff budget across buckets that never even reached the wire.
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: true);
    transport.FailFirst = 1;
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeB", TrackedGuid.New().Value, 300, 700),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("the second group still dispatches after the first group's send fails — a per-group " +
               "failure costs that group's attempt, never the whole tick.");
  }

  [Test]
  public async Task DrainTick_ASendCanceledByShutdown_StopsTheTickAsync() {
    // The companion to OneFailedGroupSend_StillDispatchesTheRemainingGroups, and the opposite
    // answer. A throttled send costs THAT group's attempt and no more, because the later groups
    // already burned backoff budget on their claims and deserve their shot at the wire. A
    // canceled send is a stopping host: the later groups' claims are already stamped, so their
    // budget is spent either way, and continuing only puts more traffic on a broker the process
    // is disconnecting from.
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: true);
    transport.FailFirst = 1;
    transport.FailFirstWith = new OperationCanceledException();
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeB", TrackedGuid.New().Value, 300, 700),
    ]);

    await Assert.That(async () =>
        await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("shutdown ends the tick rather than being absorbed as one group's bad luck");
    await Assert.That(transport.Published).IsEmpty()
      .Because("the second group never reaches the wire — the ladder re-offers both after backoff");
  }

  [Test]
  public async Task DrainTick_ReportOnlyMode_NeverClaimsOrDispatchesAsync() {
    // RepairMode.ReportOnly is the operator's explicit opt-DOWN from auto-repair — and the drain
    // is a repair dispatcher. A ReportOnly service whose drain kept claiming and sending
    // redelivery requests would repair anyway (and burn transport quota doing it), turning the
    // opt-down into a dead knob at exactly the moment an operator reaches for it.
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairDrainRatePerSecond = 10,
      RepairMode = IntegrityRepairMode.ReportOnly,
    }, origin, learnTopic: true);
    coordinator.Eligible.Add(
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 1, 10));

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(coordinator.ClaimCalls.Count).IsEqualTo(0)
      .Because("ReportOnly must not even claim — a claim stamps an attempt and burns the row's backoff");
    await Assert.That(transport.Published.Count).IsEqualTo(0)
      .Because("ReportOnly never dispatches a repair request");
  }

  [Test]
  public async Task DrainTick_TokensGateTheClaimBudget_AndSpendOnClaimAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, _, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 2,
    }, origin, learnTopic: true);
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 1, 10),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 1, 10),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 1, 10),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);
    await Assert.That(coordinator.ClaimCalls.Count).IsEqualTo(1);
    await Assert.That(coordinator.ClaimCalls[0].Limit).IsEqualTo(2)
      .Because("one second at two rows per second buys exactly two claims");

    await worker.DrainTickAsync(0.0, DateTimeOffset.UtcNow, CancellationToken.None);
    await Assert.That(coordinator.ClaimCalls.Count).IsEqualTo(1)
      .Because("the tokens were spent on the claim — zero elapsed time refills nothing");
  }

  [Test]
  public async Task DrainTick_UnlearnedOrigins_AreNeverClaimedAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: false);
    coordinator.Eligible.Add(
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 1, 10));

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(coordinator.ClaimCalls).IsEmpty()
      .Because("no learned request topic → nothing could be sent → the claim must not burn attempts");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task DrainTick_PreStampRows_AskForWholeHistory_WithoutWideningTheirWindowedNeighborsAsync() {
    // Before #2034 a pre-stamp row widened its whole group, windowed rows included, to whole history.
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: true);
    var windowed = TrackedGuid.New().Value;
    var preStamp = TrackedGuid.New().Value;
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", windowed, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", preStamp, null, null),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    var requests = transport.Published.ConvertAll(p => _deserializeRedelivery(p.Envelope));
    var whole = requests.Single(r => r.StreamIds!.Contains(preStamp));
    await Assert.That(whole.FromCommitSequence).IsNull()
      .Because("a pre-stamp row's compared range is unknown: whole history, the legacy semantics");
    await Assert.That(whole.ToCommitSequence).IsNull();
    var bounded = requests.Single(r => r.StreamIds!.Contains(windowed));
    await Assert.That(bounded.FromCommitSequence).IsEqualTo(99L).Because("its neighbor's unknown range does not widen it");
    await Assert.That(bounded.ToCommitSequence).IsEqualTo(499L);
  }


  // ── ExecuteAsync: the paced loop itself ───────────────────────────────────

  private static (RepairDrainWorker Worker, DrainCoordinator Coordinator) _buildForLoop(
      StreamIntegrityOptions options, TimeProvider clock) {
    var coordinator = new DrainCoordinator();
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    // The drain only claims for origins whose request topic it has learned; without one the
    // tick is inert by design, and the loop would look broken when it is merely idle.
    tracker.RecordCheckpoint(Guid.NewGuid(), "origin-svc", clock.GetUtcNow(), "origin.requests");
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(tracker);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("drainer-svc"));
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var worker = new RepairDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(options),
      NullLogger<RepairDrainWorker>.Instance,
      clock);
    return (worker, coordinator);
  }

  [Test]
  public async Task ExecuteAsync_WhenDisabled_ParksInsteadOfExitingAsync() {
    // A BackgroundService that returns from ExecuteAsync reads to the host as a crashed worker.
    // Parking keeps a deliberately disabled drain distinguishable from one that fell over --
    // which matters here because the drain being off is a supported configuration.
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var (worker, _) = _buildForLoop(
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, RepairDrainEnabled = false },
      clock);

    await worker.StartAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask is not null).IsTrue();
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("a disabled drain stays parked rather than completing, which would look like a crash");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task ExecuteAsync_WithANonPositiveRate_ParksAsync(double rate) {
    // A rate of zero or below cannot pace anything, so it is treated as disabled rather than
    // spinning a loop that can never grant a token.
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var (worker, _) = _buildForLoop(
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, RepairDrainEnabled = true, RepairDrainRatePerSecond = rate },
      clock);

    await worker.StartAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse();

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_TicksOnceASecondOfVirtualTimeAsync() {
    // The pace is the point of this worker: it exists to bleed repair requests out slowly rather
    // than flood a recovering system. Driving it on a fake clock proves the cadence without a
    // real delay, so the test cannot flake on a loaded machine.
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var (worker, coordinator) = _buildForLoop(
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, RepairDrainEnabled = true, RepairDrainRatePerSecond = 1 },
      clock);

    await worker.StartAsync(CancellationToken.None);

    // Advance virtual time until the loop's interval elapses, waiting on the claim itself
    // rather than on wall-clock. The short pause between advances is pacing, not the
    // assertion -- a tight spin here starves the thread pool and destabilises tests running
    // alongside this one.
    for (var attempt = 0; attempt < 50 && !coordinator.FirstClaim.Task.IsCompleted; attempt++) {
      clock.Advance(TimeSpan.FromSeconds(1));
      await Task.Delay(10);
    }

    await Assert.That(async () => await coordinator.FirstClaim.Task.WaitAsync(TimeSpan.FromSeconds(5)))
      .ThrowsNothing()
      .Because("the loop has to reach the drain tick once its interval elapses");

    await worker.StopAsync(CancellationToken.None);
  }

  // ── #2184: the outstanding-answer cap ──────────────────────────────────

  [Test]
  public async Task DrainTick_AtTheOutstandingAnswerCap_ClaimsNothing_AndSaysSoOnceAsync() {
    // Answers can land hours after their requests, so the request rate alone does not bound the load a burst of late
    // answers puts on this service's store; the count of buckets still awaiting an answer does.
    var origin = TrackedGuid.New().Value;
    var logger = new FakeLogger<RepairDrainWorker>();
    var (metrics, holdCount, listener) = _metrics();
    using var listening = listener;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
      MaxOutstandingRepairAnswers = 500,
    }, origin, learnTopic: true, logger: logger, metrics: metrics);
    coordinator.AnswerPending = 500;
    coordinator.Eligible.Add(new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500));

    await worker.DrainTickAsync(1.0, _fixedNow, CancellationToken.None);
    await worker.DrainTickAsync(1.0, _fixedNow.AddSeconds(1), CancellationToken.None);

    await Assert.That(coordinator.ClaimCalls).IsEmpty()
      .Because("with the cap's worth of answers on the way, the drain claims no row, so no attempt is stamped");
    await Assert.That(transport.Published).IsEmpty();
    await Assert.That(coordinator.CountCalls).IsEquivalentTo([(_fixedNow, 500), (_fixedNow.AddSeconds(1), 500)])
      .Because("the count is asked at the tick's own instant and needs to read no further than the cap");
    var holds = logger.Collector.GetSnapshot().Where(r => r.Message.Contains("holding", StringComparison.Ordinal)).ToList();
    await Assert.That(holds.Count).IsEqualTo(1).Because("the hold is logged when it starts, not on every tick it lasts");
    await Assert.That(holds[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(holds[0].Message).Contains("500");
    await Assert.That(holdCount()).IsEqualTo(1L).Because("one hold, counted once");
  }

  [Test]
  public async Task DrainTick_UnderTheCap_ClaimsNoMoreThanTheRoomLeftAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, _, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
      MaxOutstandingRepairAnswers = 500,
    }, origin, learnTopic: true);
    coordinator.AnswerPending = 498;

    await worker.DrainTickAsync(1.0, _fixedNow, CancellationToken.None);

    await Assert.That(coordinator.ClaimCalls.Single().Limit).IsEqualTo(2)
      .Because("the tokens afford ten rows, but only two more answers fit under the cap");
  }

  [Test]
  public async Task DrainTick_WhenThePendingCountFallsUnderTheCap_ResumesAndSaysSoOnceAsync() {
    var origin = TrackedGuid.New().Value;
    var logger = new FakeLogger<RepairDrainWorker>();
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
      MaxOutstandingRepairAnswers = 5,
    }, origin, learnTopic: true, logger: logger);
    coordinator.AnswerPending = 5;
    await worker.DrainTickAsync(1.0, _fixedNow, CancellationToken.None);
    coordinator.AnswerPending = 3;
    coordinator.Eligible.Add(new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500));

    await worker.DrainTickAsync(1.0, _fixedNow.AddSeconds(1), CancellationToken.None);
    await worker.DrainTickAsync(1.0, _fixedNow.AddSeconds(2), CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(1).Because("answers landed, so there is room to ask again");
    var resumed = logger.Collector.GetSnapshot().Where(r => r.Message.Contains("resumed", StringComparison.Ordinal)).ToList();
    await Assert.That(resumed.Count).IsEqualTo(1).Because("the release is logged once, when the hold ends");
    await Assert.That(resumed[0].Level).IsEqualTo(LogLevel.Information);
  }

  [Test]
  public async Task DrainTick_WithTheCapOff_NeverCountsTheAnswersAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, _, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
      MaxOutstandingRepairAnswers = 0,
    }, origin, learnTopic: true);
    coordinator.AnswerPending = 1_000_000;

    await worker.DrainTickAsync(1.0, _fixedNow, CancellationToken.None);

    await Assert.That(coordinator.CountCalls).IsEmpty().Because("0 turns the cap off, and an off cap costs no query");
    await Assert.That(coordinator.ClaimCalls.Single().Limit).IsEqualTo(10);
  }

  [Test]
  public async Task DrainTick_AtTheCap_OnAHostWithoutMetrics_StillHoldsAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, _, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
      MaxOutstandingRepairAnswers = 1,
    }, origin, learnTopic: true);
    coordinator.AnswerPending = 1;

    await worker.DrainTickAsync(1.0, _fixedNow, CancellationToken.None);

    await Assert.That(coordinator.ClaimCalls).IsEmpty();
  }

  [Test]
  public async Task ExecuteAsync_ACountThatCannotBeRead_ClaimsNothing_LogsIt_AndResumesOnceItCanAsync() {
    // The cap fails closed: a pending-answer count that cannot be read fails the tick, so nothing is claimed (no attempt
    // stamped, nothing sent) until the count reads again. Driven through the real loop on a clock that signals each time
    // the loop arms its one-second wait, so every step waits on the transition it asserts.
    var origin = TrackedGuid.New().Value;
    var logger = new FakeLogger<RepairDrainWorker>();
    var clock = new ArmSignalingClock(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
      MaxOutstandingRepairAnswers = 500,
    }, origin, learnTopic: true, logger: logger, clock: clock);
    coordinator.CountFailure = new InvalidOperationException("the ledger cannot be read");
    coordinator.Eligible.Add(new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500));
    var guard = TimeSpan.FromSeconds(30);

    await worker.StartAsync(CancellationToken.None);
    try {
      await clock.Armed.WaitAsync(guard);           // the loop waits out its first second
      clock.Advance(TimeSpan.FromSeconds(1));
      await coordinator.Counted.WaitAsync(guard);   // the tick asked, and the count threw
      await clock.Armed.WaitAsync(guard);           // the failed tick is over and the loop waits again

      await Assert.That(coordinator.ClaimCalls).IsEmpty()
        .Because("a cap that cannot be measured never opens: no row is claimed, so no attempt is spent");
      await Assert.That(transport.Published).IsEmpty();
      var failed = logger.Collector.GetSnapshot().Where(r => r.Message.Contains("tick failed", StringComparison.Ordinal)).ToList();
      await Assert.That(failed.Count).IsEqualTo(1).Because("the failed tick is logged with its cause");
      await Assert.That(failed[0].Level).IsEqualTo(LogLevel.Warning);
      await Assert.That(failed[0].Exception).IsTypeOf<InvalidOperationException>();

      coordinator.CountFailure = null;
      clock.Advance(TimeSpan.FromSeconds(1));
      await coordinator.FirstClaim.Task.WaitAsync(guard);   // the next tick reads the count and claims
      await clock.Armed.WaitAsync(guard);                   // and finishes

      await Assert.That(coordinator.ClaimCalls.Count).IsEqualTo(1).Because("once the count reads, the drain resumes");
      await Assert.That(transport.Published.Count).IsEqualTo(1);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  /// <summary>A fake clock that releases <see cref="Armed"/> each time a timer is created, which is each time the drain's
  /// loop starts waiting out its interval.</summary>
  private sealed class ArmSignalingClock(DateTimeOffset start) : TimeProvider {
    private readonly FakeTimeProvider _inner = new(start);
    public SemaphoreSlim Armed { get; } = new(0);
    public void Advance(TimeSpan by) => _inner.Advance(by);
    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
    public override long GetTimestamp() => _inner.GetTimestamp();
    public override long TimestampFrequency => _inner.TimestampFrequency;
    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      var timer = _inner.CreateTimer(callback, state, dueTime, period);
      Armed.Release();
      return timer;
    }
  }

  [Test]
  public async Task Options_MaxOutstandingRepairAnswers_DefaultsTo500Async() {
    await Assert.That(new StreamIntegrityOptions().MaxOutstandingRepairAnswers).IsEqualTo(500)
      .Because("at the default 5 rows a second, 500 answers is 100 seconds of requests: answers that land in under that "
             + "never meet the cap, and answers that lag are paced to the rate they land at");
  }

  private static (StreamIntegrityMetrics Metrics, Func<long> Holds, IDisposable Listener) _metrics() {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
    long holds = 0;
    // This instance's counter only, read on demand: a passive counter reports its cumulative count when observed.
    var listener = new System.Diagnostics.Metrics.MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument, metrics.RepairDrainHolds.Instrument)) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Exchange(ref holds, value));
    listener.Start();
    return (metrics, () => {
      listener.RecordObservableInstruments();
      return Interlocked.Read(ref holds);
    }, listener);
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static (RepairDrainWorker Worker, DrainCoordinator Coordinator, CaptureTransport Transport, IntegrityGapTracker Tracker) _build(
      StreamIntegrityOptions options, Guid origin, bool learnTopic, ILogger<RepairDrainWorker>? logger = null,
      StreamIntegrityMetrics? metrics = null, TimeProvider? clock = null) {
    var coordinator = new DrainCoordinator();
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    if (learnTopic) {
      tracker.RecordCheckpoint(origin, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    } else {
      tracker.RecordCheckpoint(origin, "origin-svc", DateTimeOffset.UtcNow, requestTopic: null);
    }
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(tracker);
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("drainer-svc"));
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var worker = new RepairDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(options),
      logger ?? NullLogger<RepairDrainWorker>.Instance,
      clock);
    return (worker, coordinator, transport, tracker);
  }

  private static RequestRedeliveryCommand _deserializeRedelivery(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestRedeliveryCommand)))!;
  }

  private sealed class DrainCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<IntegrityRepairDrainItem> Eligible { get; } = [];
    public List<(IReadOnlyList<Guid> Origins, int Limit)> ClaimCalls { get; } = [];
    /// <summary>Completes on the first claim so a test can wait on the effect, not on a clock.</summary>
    public TaskCompletionSource FirstClaim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyList<IntegrityRepairDrainItem>> IntegrityClaimRepairDrainAsync(
        IReadOnlyList<Guid> originIds, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
        int limit, CancellationToken cancellationToken = default) {
      ClaimCalls.Add((originIds, limit));
      FirstClaim.TrySetResult();
      var take = Eligible.Where(e => originIds.Contains(e.OriginServiceId)).Take(limit).ToList();
      foreach (var item in take) {
        Eligible.Remove(item);
      }
      return Task.FromResult<IReadOnlyList<IntegrityRepairDrainItem>>(take);
    }

    public List<(Guid Origin, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> Keys, DateTimeOffset Now, TimeSpan Timeout)> PendingMarks { get; } = [];

    /// <summary>The buckets awaiting an answer, as the ledger would count them (#2184).</summary>
    public int AnswerPending { get; set; }
    public List<(DateTimeOffset Now, int Limit)> CountCalls { get; } = [];

    /// <summary>When set, the count throws it, as a store that cannot be read would.</summary>
    public Exception? CountFailure { get; set; }
    /// <summary>Released on every count, so a test waits on the count itself.</summary>
    public SemaphoreSlim Counted { get; } = new(0);

    public Task<int> IntegrityCountAnswerPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) {
      CountCalls.Add((now, limit));
      Counted.Release();
      if (CountFailure is { } failure) {
        return Task.FromException<int>(failure);
      }
      return Task.FromResult(Math.Min(AnswerPending, limit));
    }

    public Task<int> IntegrityMarkAnswerPendingAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, long? windowFrom, long? windowUntil,
        DateTimeOffset now, TimeSpan timeout, CancellationToken cancellationToken = default) {
      PendingMarks.Add((originServiceId, keys, now, timeout));
      PendingKeys.AddRange(keys);
      return Task.FromResult(keys.Count);
    }

    /// <summary>The buckets awaiting an answer, as the ledger would hold them: marked, and neither answered nor withdrawn.</summary>
    public List<IntegrityRepairLedger.DivergenceKey> PendingKeys { get; } = [];

    public List<(Guid Origin, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> Keys, DateTimeOffset Now, TimeSpan Timeout)> Withdrawals { get; } = [];

    /// <summary>An answer landing for <paramref name="streamId"/>: its bucket stops waiting.</summary>
    public void Answer(Guid streamId) => PendingKeys.RemoveAll(k => k.StreamId == streamId);

    public Task<int> IntegrityWithdrawAnswerPendingAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, DateTimeOffset now, TimeSpan timeout,
        CancellationToken cancellationToken = default) {
      Withdrawals.Add((originServiceId, keys, now, timeout));
      return Task.FromResult(PendingKeys.RemoveAll(keys.Contains));
    }
  }

  private sealed class CaptureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public int FailFirst { get; set; }
    /// <summary>Thrown in place of the throttle timeout, for the cancellation contract.</summary>
    public Exception? FailFirstWith { get; set; }
    public int Attempts { get; private set; }
    /// <summary>Runs inside each publish, before it succeeds or fails: where an answer that outruns the publish lands.</summary>
    public Action? OnPublish { get; set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      OnPublish?.Invoke();
      lock (Published) {
        Attempts++;
        if (Attempts <= FailFirst) {
          throw FailFirstWith ?? new TimeoutException("SendMessageAsync timed out (simulated broker throttle)");
        }
        Published.Add((envelope, destination, envelopeType));
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class InstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New().Value;
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
