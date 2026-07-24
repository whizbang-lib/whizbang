using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Surface tests for six records that show 0% coverage but ride hot paths
/// (claim_work, the work pump, partition rebalancing, completion flushing,
/// rewind-on-cursor-inversion). Each test pins ctor / property shape + value
/// equality so a future refactor that drops or reorders a property breaks
/// here instead of silently changing a SQL marshaller.
///
/// Targets:
///   - PartitionRecomputeResult — counts from RecomputePartitionNumbersAsync.
///   - WorkCoordinatorStatistics — observability gauge snapshot.
///   - RewindCursorInfo — rewind-detection trigger record.
///   - PendingPerspectiveEvent — slim (work_id, event_id) tuple for the
///     drain prefetch (Phase H step 7).
///   - OrphanedLifecycleEvent — orphan-event reconciliation tuple.
///   - FlushCompletionsRequest + CategoryFailures — composite flusher payload.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public class CoordinatorRecordSurfaceTests {

  [Test]
  public async Task PartitionRecomputeResult_AnyRecomputed_TrueWhenAnyCountPositiveAsync() {
    var r1 = new PartitionRecomputeResult { InboxRowsRecomputed = 5 };
    var r2 = new PartitionRecomputeResult { OutboxRowsRecomputed = 3 };
    var r3 = new PartitionRecomputeResult { ActiveStreamsRowsRecomputed = 7 };

    await Assert.That(r1.AnyRecomputed).IsTrue();
    await Assert.That(r2.AnyRecomputed).IsTrue();
    await Assert.That(r3.AnyRecomputed).IsTrue();
  }

  [Test]
  public async Task PartitionRecomputeResult_AnyRecomputed_FalseWhenAllZeroAsync() {
    var r = new PartitionRecomputeResult();

    await Assert.That(r.AnyRecomputed).IsFalse();
    await Assert.That(r.InboxRowsRecomputed).IsEqualTo(0);
    await Assert.That(r.OutboxRowsRecomputed).IsEqualTo(0);
    await Assert.That(r.ActiveStreamsRowsRecomputed).IsEqualTo(0);
  }

  [Test]
  public async Task WorkCoordinatorStatistics_DefaultsToZeroAcrossAllGaugesAsync() {
    var s = new WorkCoordinatorStatistics();

    await Assert.That(s.PendingPerspectiveEvents).IsEqualTo(0);
    await Assert.That(s.PendingOutbox).IsEqualTo(0);
    await Assert.That(s.PendingInbox).IsEqualTo(0);
    await Assert.That(s.ActiveStreams).IsEqualTo(0);
  }

  [Test]
  public async Task WorkCoordinatorStatistics_InitPropertiesRoundTripAsync() {
    var s = new WorkCoordinatorStatistics {
      PendingPerspectiveEvents = 1000,
      PendingOutbox = 50,
      PendingInbox = 25,
      ActiveStreams = 200,
    };

    await Assert.That(s.PendingPerspectiveEvents).IsEqualTo(1000);
    await Assert.That(s.PendingOutbox).IsEqualTo(50);
    await Assert.That(s.PendingInbox).IsEqualTo(25);
    await Assert.That(s.ActiveStreams).IsEqualTo(200);
  }

  [Test]
  public async Task RewindCursorInfo_PositionalCtor_RoundTripsAllValuesAsync() {
    var streamId = Guid.NewGuid();
    var lastEventId = Guid.NewGuid();
    var triggerId = Guid.NewGuid();

    var info = new RewindCursorInfo(streamId, "MyPerspective", lastEventId, triggerId);

    await Assert.That(info.StreamId).IsEqualTo(streamId);
    await Assert.That(info.PerspectiveName).IsEqualTo("MyPerspective");
    await Assert.That(info.LastEventId).IsEqualTo(lastEventId);
    await Assert.That(info.RewindTriggerEventId).IsEqualTo(triggerId);
  }

  [Test]
  public async Task RewindCursorInfo_NullsAreAllowedAsync() {
    var info = new RewindCursorInfo(Guid.NewGuid(), "Persp", LastEventId: null, RewindTriggerEventId: null);

    await Assert.That(info.LastEventId).IsNull();
    await Assert.That(info.RewindTriggerEventId).IsNull();
  }

  [Test]
  public async Task PendingPerspectiveEvent_PositionalCtor_RoundTripsAsync() {
    var workId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    var pe = new PendingPerspectiveEvent(workId, eventId);

    await Assert.That(pe.EventWorkId).IsEqualTo(workId);
    await Assert.That(pe.EventId).IsEqualTo(eventId);
  }

  [Test]
  public async Task PendingPerspectiveEvent_ValueEqualityAsync() {
    var workId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var a = new PendingPerspectiveEvent(workId, eventId);
    var b = new PendingPerspectiveEvent(workId, eventId);

    await Assert.That(a).IsEqualTo(b);
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
  }

  [Test]
  public async Task OrphanedLifecycleEvent_PositionalCtor_RoundTripsAsync() {
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = new MessageId(eventId),
      Payload = JsonSerializer.SerializeToElement(new { Name = "x" }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };

    var orphan = new OrphanedLifecycleEvent(eventId, streamId, envelope);

    await Assert.That(orphan.EventId).IsEqualTo(eventId);
    await Assert.That(orphan.StreamId).IsEqualTo(streamId);
    await Assert.That(orphan.Envelope).IsSameReferenceAs(envelope);
  }

  [Test]
  public async Task FlushCompletionsRequest_AllNullDefaultsAsync() {
    var req = new FlushCompletionsRequest();

    await Assert.That(req.OutboxIds).IsNull();
    await Assert.That(req.PerspectiveCursors).IsNull();
    await Assert.That(req.PerspectiveEventWorkIds).IsNull();
    await Assert.That(req.FailuresByCategory).IsNull();
  }

  [Test]
  public async Task FlushCompletionsRequest_AllValuesRoundTripAsync() {
    var outboxIds = new[] { Guid.NewGuid() };
    var workIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
    var failures = new[] {
      new CategoryFailures(WorkCategory.Outbox, []),
      new CategoryFailures(WorkCategory.Inbox, []),
    };

    var req = new FlushCompletionsRequest(
      OutboxIds: outboxIds,
      PerspectiveCursors: null,
      PerspectiveEventWorkIds: workIds,
      FailuresByCategory: failures);

    await Assert.That(req.OutboxIds).IsSameReferenceAs(outboxIds);
    await Assert.That(req.PerspectiveEventWorkIds).IsSameReferenceAs(workIds);
    await Assert.That(req.FailuresByCategory).IsSameReferenceAs(failures);
  }

  [Test]
  public async Task CategoryFailures_PositionalCtor_RoundTripsAsync() {
    var items = new[] {
      new MessageFailure {
        MessageId = Guid.NewGuid(),
        CompletedStatus = MessageProcessingStatus.Stored,
        Error = "boom",
      },
    };

    var cf = new CategoryFailures(WorkCategory.PerspectiveEvent, items);

    await Assert.That(cf.Category).IsEqualTo(WorkCategory.PerspectiveEvent);
    await Assert.That(cf.Items).IsSameReferenceAs(items);
  }
}
