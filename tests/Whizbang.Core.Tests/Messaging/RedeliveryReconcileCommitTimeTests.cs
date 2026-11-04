// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A committed redelivery bundle records its answer when it commits (#2033): the buckets it answered stop being
/// requested, and a bundle that changed nothing marks its buckets right away instead of after the maintainer's
/// idle-gated reconcile. "Changed nothing" is judged from what the store held at commit, because by the time the
/// deferred reconcile runs the bundle's own children are stored and would read as already present.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryReconciliation.cs</code-under-test>
/// <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
public class RedeliveryReconcileCommitTimeTests {
  private const string EVENT_TYPE = "Contracts.OrderPlaced, Contracts";
  private static readonly DateTimeOffset _now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

  private static (RedeliveryComposite Bundle, List<InboxMessage> Children, Guid Stream) _bundle(Guid origin, int events) {
    var stream = TrackedGuid.New().Value;
    var bundle = new RedeliveryComposite {
      StreamId = stream,
      OriginServiceId = origin,
      InnerEventIds = [.. Enumerable.Range(0, events).Select(_ => TrackedGuid.New().Value)],
      InnerTypeNames = [.. Enumerable.Range(0, events).Select(_ => EVENT_TYPE)],
      InnerPayloads = [.. Enumerable.Range(0, events).Select(_ => JsonElement.Parse("{}"))],
      InnerCommitSequences = [.. Enumerable.Range(0, events).Select(i => (long?)(i + 1))],
    };
    List<InboxMessage> children = [.. bundle.InnerEventIds.Select((id, i) => new InboxMessage {
      MessageId = id,
      HandlerName = "OrderPlacedHandler",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = new MessageId(id),
        Payload = bundle.InnerPayloads[i],
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Inbox },
      },
      EnvelopeType = "MessageEnvelope",
      StreamId = stream,
      IsEvent = true,
      MessageType = EVENT_TYPE,
      SourceServiceId = origin,
      SourceCommitSequence = i + 1,
    })];
    return (bundle, children, stream);
  }

  private static ServiceProvider _services(
      InMemoryIntegrityDeliveryCoordinator coordinator, IIntegrityRepairLedger? ledger = null, bool withDeliveries = true,
      StreamIntegrityMetrics? metrics = null) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    if (withDeliveries) {
      services.AddSingleton<IntegrityDeliveryRegistry>();
    }
    if (ledger is not null) {
      services.AddSingleton(ledger);
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    return services.BuildServiceProvider();
  }

  private static IntegrityBundleAnswer _answer(
      int children, int present, int absent = 0, int refused = 0, int adoptable = 0,
      IReadOnlyList<IntegrityRepairLedger.DivergenceKey>? refusedMarks = null,
      IReadOnlyList<IntegrityRepairLedger.DivergenceKey>? unchangedMarks = null) =>
    new(children, present, absent, refused, adoptable, refusedMarks ?? [], unchangedMarks ?? []);

  [Test]
  public async Task Reconcile_RecordsTheAnswerAtCommit_AndReportsTheBucketsItMarked_BeforeDeferringTheRestAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children, stream) = _bundle(origin, 2);
    var unchanged = new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, stream);
    var refused = new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", EVENT_TYPE, TrackedGuid.New().Value);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator {
      BundleAnswer = _answer(2, present: 1, refused: 1, refusedMarks: [refused], unchangedMarks: [unchanged]),
    };
    using var probe = new IntegrityMetricsProbe();
    await using var services = _services(coordinator, metrics: probe.Metrics);
    var logger = new CapturingLogger();

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, logger, _now, "orders-service");

    await Assert.That(result.Deferred).IsTrue().Because("adoption and digest moves still wait for the maintainer");
    await Assert.That(result.MarkedUnhealable).IsEqualTo(2);
    var (recordedOrigin, recordedIds, recordedSequences, recordedAt) = coordinator.BundleAnswers.Single();
    await Assert.That(recordedOrigin).IsEqualTo(origin);
    await Assert.That(recordedIds).IsEquivalentTo(bundle.InnerEventIds);
    await Assert.That(recordedSequences).IsEquivalentTo(new long?[] { 1, 2 });
    await Assert.That(recordedAt).IsEqualTo(_now);
    await Assert.That(probe.Total(probe.Metrics.RepairsUnhealable)).IsEqualTo(2);
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(2)
      .Because("each newly marked bucket is reported once, with the reason it was marked");
    await Assert.That(coordinator.Pending.Single().Payload).Contains("\"allPresentAtCommit\":true");
  }

  [Test]
  public async Task TheDrain_ChildrenAbsentAtCommit_AreNotJudgedUnchanged_ThoughStoredWhenTheReconcileRunsAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children, stream) = _bundle(origin, 2);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator {
      BundleAnswer = _answer(2, present: 1, absent: 1),
      // By the time the drain reaches it, the child the bundle brought is stored too.
      AdoptionAnswer = [new RedeliveredBucketPresence(stream, null, EVENT_TYPE, Present: 2, Adopted: 0)],
    };
    var ledger = _ledgerTracking(origin, stream);
    await using var services = _services(coordinator, ledger);
    _ = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, NullLogger.Instance, _now, "orders-service");

    await RedeliveryReconciler.HandleDeferredAsync(coordinator.Pending.Single().Payload, services, CancellationToken.None);

    await Assert.That(coordinator.Adoptions.Count).IsEqualTo(1);
    await Assert.That(await ledger.TryBeginRepairAsync(_key(origin, stream), _now, TimeSpan.FromSeconds(1), 3)).IsTrue()
      .Because("the bundle stored an event, so repair worked; the bucket must not be marked as one it cannot change");
  }

  [Test]
  public async Task TheDrain_AllPresentAtCommit_AndNothingAdopted_MarksTheBucketAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children, stream) = _bundle(origin, 1);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator {
      // Present at commit but adoptable, so the commit could not decide; the reconcile then adopted nothing.
      BundleAnswer = _answer(1, present: 1, adoptable: 1),
      AdoptionAnswer = [new RedeliveredBucketPresence(stream, null, EVENT_TYPE, Present: 1, Adopted: 0, RefusedForeignOrigin: 1)],
    };
    var ledger = _ledgerTracking(origin, stream);
    await using var services = _services(coordinator, ledger);
    _ = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, NullLogger.Instance, _now, "orders-service");

    await RedeliveryReconciler.HandleDeferredAsync(coordinator.Pending.Single().Payload, services, CancellationToken.None);

    await Assert.That(await ledger.TryBeginRepairAsync(_key(origin, stream), _now, TimeSpan.FromSeconds(1), 3)).IsFalse()
      .Because("every event was already here at commit and the reconcile adopted none: asking again changes nothing");
  }

  [Test]
  public async Task TheDrain_APayloadRecordedWithoutTheCommitAnswer_JudgesByPresenceAsBeforeAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var coordinator = new InMemoryIntegrityDeliveryCoordinator {
      AdoptionAnswer = [new RedeliveredBucketPresence(stream, null, EVENT_TYPE, Present: 1, Adopted: 0)],
    };
    var ledger = _ledgerTracking(origin, stream);
    await using var services = _services(coordinator, ledger);
    var payload = $$"""{"originServiceId":"{{origin}}","originServiceName":null,"eventIds":["{{eventId}}"],"commitSequences":[4]}""";

    await RedeliveryReconciler.HandleDeferredAsync(payload, services, CancellationToken.None);

    await Assert.That(await ledger.TryBeginRepairAsync(_key(origin, stream), _now, TimeSpan.FromSeconds(1), 3)).IsFalse()
      .Because("a reconcile recorded by an earlier version carries no commit answer, and keeps the earlier rule");
  }

  [Test]
  public async Task Reconcile_AStoreThatCannotRecordTheAnswer_DefersWithoutOneAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children, _) = _bundle(origin, 1);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator { BundleAnswer = null };
    await using var services = _services(coordinator);

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, NullLogger.Instance, _now, "orders-service");

    await Assert.That(result.Deferred).IsTrue();
    await Assert.That(result.MarkedUnhealable).IsEqualTo(0);
    await Assert.That(coordinator.Pending.Single().Payload).Contains("\"allPresentAtCommit\":null");
  }

  [Test]
  public async Task Reconcile_RecordingTheAnswerFails_IsLogged_AndTheReconcileIsStillDeferredAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children, _) = _bundle(origin, 1);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator { BundleAnswerFailure = new InvalidOperationException("db down") };
    await using var services = _services(coordinator);
    var logger = new CapturingLogger();

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, logger, _now, "orders-service");

    await Assert.That(result.Deferred).IsTrue();
    await Assert.That(logger.Entries.Single(e => e.Level == LogLevel.Warning).Exception?.Message).IsEqualTo("db down");
  }

  [Test]
  public async Task Reconcile_CanceledWhileRecordingTheAnswer_PropagatesAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children, _) = _bundle(origin, 1);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator { BundleAnswerFailure = new OperationCanceledException() };
    await using var services = _services(coordinator);

    await Assert.That(async () => await RedeliveryReconciler.ReconcileAsync(bundle, children, services, NullLogger.Instance, _now))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task Reconcile_Inline_AddsTheCommitMarksToTheReconcilesAsync() {
    var origin = TrackedGuid.New().Value;
    var (bundle, children, stream) = _bundle(origin, 1);
    var key = _key(origin, stream);
    var coordinator = new InMemoryIntegrityDeliveryCoordinator {
      Supported = false,
      BundleAnswer = _answer(1, present: 1, unchangedMarks: [key]),
      AdoptionAnswer = [new RedeliveredBucketPresence(stream, null, EVENT_TYPE, Present: 1, Adopted: 0)],
    };
    await using var services = _services(coordinator, withDeliveries: false);

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, NullLogger.Instance, _now);

    await Assert.That(result.Deferred).IsFalse();
    await Assert.That(result.MarkedUnhealable).IsEqualTo(1).Because("the store marked it at commit; no ledger is registered to mark it again");
  }

  [Test]
  public async Task Reconcile_MarksTheReconciledBucketsAnswered_OnTheLedger_SoTheirPendingAnswerClearsAsync() {
    // The durable store records the answer when the children commit; a ledger that only the reconcile reaches (the
    // in-memory fallback) learns it here.
    var origin = TrackedGuid.New().Value;
    var (bundle, children, stream) = _bundle(origin, 1);
    var ledger = _ledgerTracking(origin, stream);
    _ = await ledger.TryBeginRepairAsync(_key(origin, stream), _now, TimeSpan.FromSeconds(1), 3);
    _ = await ledger.MarkAnswerPendingAsync([_key(origin, stream)], null, null, _now, TimeSpan.FromHours(3));
    var coordinator = new InMemoryIntegrityDeliveryCoordinator {
      Supported = false,
      AdoptionAnswer = [new RedeliveredBucketPresence(stream, null, EVENT_TYPE, Present: 1, Adopted: 1)],
    };
    await using var services = _services(coordinator, ledger, withDeliveries: false);

    _ = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, NullLogger.Instance, _now);

    await Assert.That(await ledger.TryBeginRepairAsync(_key(origin, stream), _now.AddSeconds(1), TimeSpan.FromSeconds(1), 3)).IsTrue()
      .Because("the answer landed, so the answer timeout no longer holds the bucket back");
  }

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin, Guid stream) => new(origin, null, EVENT_TYPE, stream);

  private static IntegrityRepairLedger _ledgerTracking(Guid origin, Guid stream) {
    var ledger = new IntegrityRepairLedger();
    _ = ledger.TryBeginReport(_key(origin, stream), 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    return ledger;
  }

  private sealed class CapturingLogger : ILogger {
    public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, exception));
  }
}
