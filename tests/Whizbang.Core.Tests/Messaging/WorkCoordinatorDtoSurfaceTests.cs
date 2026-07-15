using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Property-surface and record-semantics tests for the DTOs declared alongside
/// <see cref="IWorkCoordinator"/> in IWorkCoordinator.cs. Complements
/// CoordinatorRecordSurfaceTests (which pins PartitionRecomputeResult,
/// WorkCoordinatorStatistics, and the flusher payloads) by covering the optional
/// getters on OutboxWork/InboxWork/PerspectiveWork/StreamEventData and the drain batch
/// rows, plus the compiler-generated equality/ToString/with/Deconstruct members on the
/// positional records (PurgedOrphanInboxRow, MaintenanceResult, RewindCursorInfo,
/// OrphanedLifecycleEvent, PendingPerspectiveEvent).
/// </summary>
/// <docs>messaging/work-coordination</docs>
public class WorkCoordinatorDtoSurfaceTests {

  // ========================================
  // Completion / failure records
  // ========================================

  [Test]
  public async Task MessageCompletion_StatusGetter_ReturnsInitializedFlagsAsync() {
    var messageId = (Guid)TrackedGuid.NewMedo();
    var completion = new MessageCompletion {
      MessageId = messageId,
      Status = MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored
    };

    await Assert.That(completion.MessageId).IsEqualTo(messageId);
    await Assert.That(completion.Status).IsEqualTo(MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored);
  }

  [Test]
  public async Task MessageFailure_CompletedStatusGetter_ReturnsInitializedFlagsAsync() {
    var failure = new MessageFailure {
      MessageId = (Guid)TrackedGuid.NewMedo(),
      CompletedStatus = MessageProcessingStatus.Stored,
      Error = "boom"
    };

    await Assert.That(failure.CompletedStatus).IsEqualTo(MessageProcessingStatus.Stored);
    await Assert.That(failure.Error).IsEqualTo("boom");
  }

  // ========================================
  // Work item records
  // ========================================

  [Test]
  public async Task OutboxWork_OptionalProperties_RoundTripAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    using var doc = JsonDocument.Parse("7");
    var metadata = new Dictionary<string, JsonElement> { ["outbox_completions_processed"] = doc.RootElement.Clone() };
    var work = new OutboxWork {
      MessageId = (Guid)TrackedGuid.NewMedo(),
      Destination = "orders-topic",
      Envelope = _createJsonEnvelope(),
      EnvelopeType = "EnvType, TestAssembly",
      MessageType = "MsgType, TestAssembly",
      StreamId = streamId,
      PartitionNumber = 17,
      Attempts = 3,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.Orphaned,
      Metadata = metadata
    };

    await Assert.That(work.PartitionNumber).IsEqualTo(17);
    await Assert.That(work.Flags).IsEqualTo(WorkBatchOptions.Orphaned);
    var workMetadata = work.Metadata ?? new Dictionary<string, JsonElement>();
    await Assert.That(workMetadata.ContainsKey("outbox_completions_processed")).IsTrue();
    await Assert.That(work.StreamId).IsEqualTo(streamId);
    await Assert.That(work.Attempts).IsEqualTo(3);
    await Assert.That(work.Status).IsEqualTo(MessageProcessingStatus.Stored);
  }

  [Test]
  public async Task InboxWork_OptionalProperties_RoundTripAsync() {
    using var doc = JsonDocument.Parse("9");
    var metadata = new Dictionary<string, JsonElement> { ["inbox_completions_processed"] = doc.RootElement.Clone() };
    var work = new InboxWork {
      MessageId = (Guid)TrackedGuid.NewMedo(),
      Envelope = _createJsonEnvelope(),
      MessageType = "MsgType, TestAssembly",
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PartitionNumber = 23,
      Attempts = 2,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.RetryAfterFailure,
      Metadata = metadata,
      Error = "previous handler failure"
    };

    await Assert.That(work.PartitionNumber).IsEqualTo(23);
    await Assert.That(work.Flags).IsEqualTo(WorkBatchOptions.RetryAfterFailure);
    var workMetadata = work.Metadata ?? new Dictionary<string, JsonElement>();
    await Assert.That(workMetadata.ContainsKey("inbox_completions_processed")).IsTrue();
    await Assert.That(work.Error).IsEqualTo("previous handler failure");
  }

  [Test]
  public async Task PerspectiveCursorCompletion_ProcessedEventIds_DefaultsEmptyAndRoundTripsAsync() {
    var processedId = (Guid)TrackedGuid.NewMedo();
    var withDefaults = new PerspectiveCursorCompletion {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PerspectiveName = "OrderPerspective",
      LastEventId = (Guid)TrackedGuid.NewMedo(),
      Status = PerspectiveProcessingStatus.Completed
    };
    var withIds = withDefaults with { ProcessedEventIds = [processedId], EventsProcessed = 1 };

    await Assert.That(withDefaults.ProcessedEventIds.Length).IsEqualTo(0);
    await Assert.That(withIds.ProcessedEventIds.Length).IsEqualTo(1);
    await Assert.That(withIds.ProcessedEventIds[0]).IsEqualTo(processedId);
    await Assert.That(withIds.EventsProcessed).IsEqualTo(1);
  }

  [Test]
  public async Task PerspectiveCursorFailure_AllProperties_RoundTripAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var lastEventId = (Guid)TrackedGuid.NewMedo();
    var processedId = (Guid)TrackedGuid.NewMedo();
    var failure = new PerspectiveCursorFailure {
      StreamId = streamId,
      PerspectiveName = "OrderPerspective",
      LastEventId = lastEventId,
      Status = PerspectiveProcessingStatus.Failed,
      Error = "apply blew up",
      ProcessedEventIds = [processedId]
    };

    await Assert.That(failure.StreamId).IsEqualTo(streamId);
    await Assert.That(failure.PerspectiveName).IsEqualTo("OrderPerspective");
    await Assert.That(failure.LastEventId).IsEqualTo(lastEventId);
    await Assert.That(failure.Status).IsEqualTo(PerspectiveProcessingStatus.Failed);
    await Assert.That(failure.Error).IsEqualTo("apply blew up");
    await Assert.That(failure.ProcessedEventIds.Length).IsEqualTo(1);
    await Assert.That(failure.ProcessedEventIds[0]).IsEqualTo(processedId);
  }

  [Test]
  public async Task PerspectiveWork_AllProperties_RoundTripAsync() {
    var lastProcessed = (Guid)TrackedGuid.NewMedo();
    using var doc = JsonDocument.Parse("3");
    var metadata = new Dictionary<string, JsonElement> { ["perspective_completions_processed"] = doc.RootElement.Clone() };
    var work = new PerspectiveWork {
      WorkId = (Guid)TrackedGuid.NewMedo(),
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PerspectiveName = "OrderPerspective",
      LastProcessedEventId = lastProcessed,
      Status = PerspectiveProcessingStatus.CatchingUp,
      PartitionNumber = 11,
      Flags = WorkBatchOptions.NewlyStored,
      Metadata = metadata
    };

    await Assert.That(work.LastProcessedEventId).IsEqualTo(lastProcessed);
    await Assert.That(work.Status).IsEqualTo(PerspectiveProcessingStatus.CatchingUp);
    await Assert.That(work.PartitionNumber).IsEqualTo(11);
    await Assert.That(work.Flags).IsEqualTo(WorkBatchOptions.NewlyStored);
    var workMetadata = work.Metadata ?? new Dictionary<string, JsonElement>();
    await Assert.That(workMetadata.ContainsKey("perspective_completions_processed")).IsTrue();
  }

  [Test]
  public async Task StreamEventData_AllProperties_RoundTripAsync() {
    var data = new StreamEventData {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      EventId = (Guid)TrackedGuid.NewMedo(),
      EventType = "MyApp.Events.OrderCreated, MyApp",
      EventData = "{\"total\":10}",
      Metadata = "{\"MessageId\":\"m\"}",
      Scope = "{\"TenantId\":\"t\"}",
      EventWorkId = (Guid)TrackedGuid.NewMedo(),
      PerspectiveName = "OrderPerspective",
      CommitSequence = 42,
      Attempts = 2
    };

    await Assert.That(data.EventType).IsEqualTo("MyApp.Events.OrderCreated, MyApp");
    await Assert.That(data.EventData).IsEqualTo("{\"total\":10}");
    await Assert.That(data.Metadata).IsEqualTo("{\"MessageId\":\"m\"}");
    await Assert.That(data.Scope).IsEqualTo("{\"TenantId\":\"t\"}");
    await Assert.That(data.PerspectiveName).IsEqualTo("OrderPerspective");
    await Assert.That(data.CommitSequence).IsEqualTo(42);
    await Assert.That(data.Attempts).IsEqualTo(2);
  }

  // ========================================
  // Drain batch rows
  // ========================================

  [Test]
  public async Task OutboxBatchRow_AllProperties_RoundTripAsync() {
    var originServiceId = (Guid)TrackedGuid.NewMedo();
    var row = new OutboxBatchRow {
      MessageId = (Guid)TrackedGuid.NewMedo(),
      StreamId = (Guid)TrackedGuid.NewMedo(),
      Destination = "orders-topic",
      MessageType = "MsgType, TestAssembly",
      EnvelopeType = "EnvType, TestAssembly",
      EventData = "{\"e\":1}",
      Metadata = "{\"m\":2}",
      Scope = "{\"TenantId\":\"t\"}",
      Status = 1,
      Attempts = 4,
      PartitionNumber = 8,
      IsEvent = true,
      CommitSequence = 100,
      OriginServiceId = originServiceId,
      OriginCommitSequence = 99,
      Error = "prior publish failure"
    };

    await Assert.That(row.Metadata).IsEqualTo("{\"m\":2}");
    await Assert.That(row.Scope).IsEqualTo("{\"TenantId\":\"t\"}");
    await Assert.That(row.IsEvent).IsTrue();
    await Assert.That(row.CommitSequence).IsEqualTo(100);
    await Assert.That(row.OriginServiceId).IsEqualTo(originServiceId);
    await Assert.That(row.OriginCommitSequence).IsEqualTo(99);
    await Assert.That(row.Error).IsEqualTo("prior publish failure");
  }

  [Test]
  public async Task InboxBatchRow_AllProperties_RoundTripAsync() {
    var row = new InboxBatchRow {
      MessageId = (Guid)TrackedGuid.NewMedo(),
      StreamId = (Guid)TrackedGuid.NewMedo(),
      HandlerName = "ServiceBusConsumer",
      MessageType = "MsgType, TestAssembly",
      EventData = "{\"e\":1}",
      Metadata = "{\"m\":2}",
      Scope = "{\"TenantId\":\"t\"}",
      Status = 1,
      Attempts = 5,
      PartitionNumber = 9,
      IsEvent = true,
      Error = "prior handler failure"
    };

    await Assert.That(row.HandlerName).IsEqualTo("ServiceBusConsumer");
    await Assert.That(row.Metadata).IsEqualTo("{\"m\":2}");
    await Assert.That(row.Scope).IsEqualTo("{\"TenantId\":\"t\"}");
    await Assert.That(row.IsEvent).IsTrue();
    await Assert.That(row.Error).IsEqualTo("prior handler failure");
  }

  // ========================================
  // Positional records — generated members
  // ========================================

  [Test]
  public async Task PurgedOrphanInboxRow_RecordSemantics_EqualityDeconstructToStringAsync() {
    var messageId = (Guid)TrackedGuid.NewMedo();
    var row = new PurgedOrphanInboxRow(messageId, "MsgType, TestAssembly", "OrderReceptor");
    var same = new PurgedOrphanInboxRow(messageId, "MsgType, TestAssembly", "OrderReceptor");
    var different = row with { HandlerName = "OtherReceptor" };
    var (deconstructedId, deconstructedType, deconstructedHandler) = row;
    var text = row.ToString();

    await Assert.That(row).IsEqualTo(same);
    await Assert.That(row.GetHashCode()).IsEqualTo(same.GetHashCode());
    await Assert.That(row == different).IsFalse();
    await Assert.That(different.HandlerName).IsEqualTo("OtherReceptor");
    await Assert.That(deconstructedId).IsEqualTo(messageId);
    await Assert.That(deconstructedType).IsEqualTo("MsgType, TestAssembly");
    await Assert.That(deconstructedHandler).IsEqualTo("OrderReceptor");
    await Assert.That(text).Contains(nameof(PurgedOrphanInboxRow));
    await Assert.That(text).Contains("OrderReceptor");
  }

  [Test]
  public async Task MaintenanceResult_RecordSemantics_EqualityDeconstructToStringAsync() {
    var result = new MaintenanceResult("purge_completed_outbox", 42, 3.5, "ok");
    var same = new MaintenanceResult("purge_completed_outbox", 42, 3.5, "ok");
    var different = result with { Status = "failed" };
    var (taskName, rowsAffected, durationMs, status) = result;
    var text = result.ToString();

    await Assert.That(result).IsEqualTo(same);
    await Assert.That(result.GetHashCode()).IsEqualTo(same.GetHashCode());
    await Assert.That(result == different).IsFalse();
    await Assert.That(different.Status).IsEqualTo("failed");
    await Assert.That(taskName).IsEqualTo("purge_completed_outbox");
    await Assert.That(rowsAffected).IsEqualTo(42);
    await Assert.That(durationMs).IsEqualTo(3.5);
    await Assert.That(status).IsEqualTo("ok");
    await Assert.That(text).Contains("purge_completed_outbox");
  }

  [Test]
  public async Task RewindCursorInfo_RecordSemantics_EqualityDeconstructToStringAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var lastEventId = (Guid)TrackedGuid.NewMedo();
    var triggerEventId = (Guid)TrackedGuid.NewMedo();
    var info = new RewindCursorInfo(streamId, "OrderPerspective", lastEventId, triggerEventId);
    var same = new RewindCursorInfo(streamId, "OrderPerspective", lastEventId, triggerEventId);
    var different = info with { LastEventId = null };
    var (deconstructedStream, deconstructedName, deconstructedLast, deconstructedTrigger) = info;
    var text = info.ToString();

    await Assert.That(info).IsEqualTo(same);
    await Assert.That(info.GetHashCode()).IsEqualTo(same.GetHashCode());
    await Assert.That(info == different).IsFalse();
    await Assert.That(different.LastEventId).IsNull();
    await Assert.That(deconstructedStream).IsEqualTo(streamId);
    await Assert.That(deconstructedName).IsEqualTo("OrderPerspective");
    await Assert.That(deconstructedLast).IsEqualTo(lastEventId);
    await Assert.That(deconstructedTrigger).IsEqualTo(triggerEventId);
    await Assert.That(text).Contains(nameof(RewindCursorInfo));
  }

  [Test]
  public async Task OrphanedLifecycleEvent_RecordSemantics_EqualityAndDeconstructAsync() {
    var eventId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var envelope = _createJsonEnvelope();
    var orphan = new OrphanedLifecycleEvent(eventId, streamId, envelope);
    var same = new OrphanedLifecycleEvent(eventId, streamId, envelope);
    var different = orphan with { EventId = (Guid)TrackedGuid.NewMedo() };
    var (deconstructedEventId, deconstructedStreamId, deconstructedEnvelope) = orphan;

    await Assert.That(orphan).IsEqualTo(same);
    await Assert.That(orphan.GetHashCode()).IsEqualTo(same.GetHashCode());
    await Assert.That(orphan == different).IsFalse();
    await Assert.That(deconstructedEventId).IsEqualTo(eventId);
    await Assert.That(deconstructedStreamId).IsEqualTo(streamId);
    await Assert.That(ReferenceEquals(deconstructedEnvelope, envelope)).IsTrue();
  }

  [Test]
  public async Task PendingPerspectiveEvent_RecordSemantics_DefaultsEqualityToStringAsync() {
    var workId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var pending = new PendingPerspectiveEvent(workId, eventId);
    var same = new PendingPerspectiveEvent(workId, eventId);
    var stamped = pending with { CommitSequence = 77 };
    var (deconstructedWorkId, deconstructedEventId, deconstructedSequence) = stamped;
    var text = stamped.ToString();

    await Assert.That(pending.CommitSequence).IsNull();
    await Assert.That(pending).IsEqualTo(same);
    await Assert.That(pending.GetHashCode()).IsEqualTo(same.GetHashCode());
    await Assert.That(pending == stamped).IsFalse();
    await Assert.That(deconstructedWorkId).IsEqualTo(workId);
    await Assert.That(deconstructedEventId).IsEqualTo(eventId);
    await Assert.That(deconstructedSequence).IsEqualTo(77);
    await Assert.That(text).Contains(nameof(PendingPerspectiveEvent));
  }

  // ========================================
  // Helpers
  // ========================================

  private static MessageEnvelope<JsonElement> _createJsonEnvelope() {
    using var doc = JsonDocument.Parse("{}");
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()),
      Payload = doc.RootElement.Clone(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

}
