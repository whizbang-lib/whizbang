using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Constructor + property contract tests for the EF Core record DTOs the work
/// coordinator persists to wh_outbox / wh_inbox / wh_event_store / and friends.
/// Without these the property setters / getters never run in unit tests (only
/// the integration suite covers them indirectly via SQL roundtrips), which
/// leaves the whole class at 0% coverage.
/// </summary>
public class RecordTypesConstructionTests {

  [Test]
  public async Task OutboxRecord_FullInitialization_RoundTripsAllPropertiesAsync() {
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var record = new OutboxRecord {
      MessageId = msgId,
      MessageType = "MyApp.Events.OrderCreated",
      MessageData = new OutboxMessageData { MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()), Payload = JsonDocument.Parse("{}").RootElement, Hops = [] },
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      Destination = "topic-a",
      Attempts = 2,
      Error = "boom",
      CreatedAt = DateTimeOffset.UtcNow,
      PublishedAt = DateTime.UtcNow,
      ProcessedAt = DateTime.UtcNow,
      InstanceId = (Guid)TrackedGuid.NewMedo(),
      LeaseExpiry = DateTimeOffset.UtcNow.AddMinutes(5),
      StreamId = streamId,
      PartitionNumber = 42,
      StatusFlags = MessageProcessingStatus.Stored,
      FailureReason = MessageFailureReason.TransportException,
      ScheduledFor = DateTimeOffset.UtcNow.AddMinutes(1),
    };
    await Assert.That(record.MessageId).IsEqualTo(msgId);
    await Assert.That(record.MessageType).IsEqualTo("MyApp.Events.OrderCreated");
    await Assert.That(record.Destination).IsEqualTo("topic-a");
    await Assert.That(record.Attempts).IsEqualTo(2);
    await Assert.That(record.Error).IsEqualTo("boom");
    await Assert.That(record.StreamId).IsEqualTo(streamId);
    await Assert.That(record.PartitionNumber).IsEqualTo(42);
    await Assert.That(record.StatusFlags).IsEqualTo(MessageProcessingStatus.Stored);
    await Assert.That(record.FailureReason).IsEqualTo(MessageFailureReason.TransportException);
  }

  [Test]
  public async Task OutboxRecord_FailureReason_DefaultsToUnknownAsync() {
    var record = new OutboxRecord {
      MessageId = (Guid)TrackedGuid.NewMedo(),
      MessageType = "T",
      MessageData = new OutboxMessageData { MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()), Payload = JsonDocument.Parse("{}").RootElement, Hops = [] },
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()), Hops = [] },
    };
    await Assert.That(record.FailureReason).IsEqualTo(MessageFailureReason.Unknown);
  }

  [Test]
  public async Task InboxRecord_FullInitialization_RoundTripsAllPropertiesAsync() {
    var msgId = (Guid)TrackedGuid.NewMedo();
    var record = new InboxRecord {
      MessageId = msgId,
      HandlerName = "OrderReceptor",
      MessageType = "MyApp.Events.OrderCreated",
      MessageData = new InboxMessageData { MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()), Payload = JsonDocument.Parse("{}").RootElement, Hops = [] },
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      Attempts = 1,
      Error = "fail",
      ReceivedAt = DateTimeOffset.UtcNow,
      ProcessedAt = DateTime.UtcNow,
      InstanceId = (Guid)TrackedGuid.NewMedo(),
      LeaseExpiry = DateTimeOffset.UtcNow.AddMinutes(5),
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PartitionNumber = 7,
      StatusFlags = MessageProcessingStatus.Stored,
      FailureReason = MessageFailureReason.SerializationError,
      ScheduledFor = DateTimeOffset.UtcNow,
    };
    await Assert.That(record.MessageId).IsEqualTo(msgId);
    await Assert.That(record.HandlerName).IsEqualTo("OrderReceptor");
    await Assert.That(record.Attempts).IsEqualTo(1);
    await Assert.That(record.PartitionNumber).IsEqualTo(7);
    await Assert.That(record.FailureReason).IsEqualTo(MessageFailureReason.SerializationError);
  }

  [Test]
  public async Task ActiveStreamRecord_FullInitialization_ExposesAllFieldsAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var now = DateTime.UtcNow;
    var record = new ActiveStreamRecord {
      StreamId = streamId,
      PartitionNumber = 13,
      AssignedInstanceId = instanceId,
      LeaseExpiry = now.AddMinutes(5),
      CreatedAt = now,
      UpdatedAt = now,
    };
    await Assert.That(record.StreamId).IsEqualTo(streamId);
    await Assert.That(record.PartitionNumber).IsEqualTo(13);
    await Assert.That(record.AssignedInstanceId).IsEqualTo(instanceId);
  }

  [Test]
  public async Task MessageAssociationRecord_FullInitialization_ExposesAllFieldsAsync() {
    var id = (Guid)TrackedGuid.NewMedo();
    var record = new MessageAssociationRecord {
      Id = id,
      MessageType = "MyApp.Events.OrderCreated",
      AssociationType = "receptor",
      TargetName = "OrderReceptor",
      ServiceName = "InventoryWorker",
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    };
    await Assert.That(record.Id).IsEqualTo(id);
    await Assert.That(record.MessageType).IsEqualTo("MyApp.Events.OrderCreated");
    await Assert.That(record.AssociationType).IsEqualTo("receptor");
    await Assert.That(record.TargetName).IsEqualTo("OrderReceptor");
    await Assert.That(record.ServiceName).IsEqualTo("InventoryWorker");
  }

  [Test]
  public async Task MessageDeduplicationRecord_FullInitialization_ExposesAllFieldsAsync() {
    var msgId = (Guid)TrackedGuid.NewMedo();
    var seen = DateTimeOffset.UtcNow;
    var record = new MessageDeduplicationRecord {
      MessageId = msgId,
      FirstSeenAt = seen,
    };
    await Assert.That(record.MessageId).IsEqualTo(msgId);
    await Assert.That(record.FirstSeenAt).IsEqualTo(seen);
  }

  [Test]
  public async Task MessageScope_AllInits_RoundTripAsync() {
    var scope = new MessageScope {
      TenantId = "tenant-1",
      UserId = "user-42",
      PartitionKey = "pk-9",
    };
    await Assert.That(scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(scope.UserId).IsEqualTo("user-42");
    await Assert.That(scope.PartitionKey).IsEqualTo("pk-9");
  }

  [Test]
  public async Task MessageScope_AllNull_IsValidAsync() {
    var scope = new MessageScope();
    await Assert.That(scope.TenantId).IsNull();
    await Assert.That(scope.UserId).IsNull();
    await Assert.That(scope.PartitionKey).IsNull();
  }

  [Test]
  public async Task EventStoreRecord_FullInitialization_RoundTripsAllPropertiesAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var aggregateId = (Guid)TrackedGuid.NewMedo();
    var record = new EventStoreRecord {
      StreamId = streamId,
      AggregateId = aggregateId,
      AggregateType = "Order",
      Version = 3,
      EventType = "MyApp.Events.OrderCreated",
      EventData = JsonDocument.Parse("{\"k\":1}").RootElement,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()), Hops = [] },
      Scope = null,
      CreatedAt = DateTime.UtcNow,
      CommitSequence = 1234,
      OriginServiceId = (Guid)TrackedGuid.NewMedo(),
      OriginCommitSequence = 555,
    };
    await Assert.That(record.StreamId).IsEqualTo(streamId);
    await Assert.That(record.AggregateId).IsEqualTo(aggregateId);
    await Assert.That(record.AggregateType).IsEqualTo("Order");
    await Assert.That(record.Version).IsEqualTo(3);
    await Assert.That(record.EventType).IsEqualTo("MyApp.Events.OrderCreated");
    await Assert.That(record.CommitSequence).IsEqualTo(1234);
    await Assert.That(record.OriginCommitSequence).IsEqualTo(555);
  }

  [Test]
  public async Task ServiceInstanceRecord_FullInitialization_RoundTripsAllPropertiesAsync() {
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var record = new ServiceInstanceRecord {
      InstanceId = instanceId,
      ServiceName = "InventoryWorker",
      HostName = "pod-abc",
      ProcessId = 1234,
      StartedAt = DateTimeOffset.UtcNow,
      LastHeartbeatAt = DateTimeOffset.UtcNow,
      Metadata = null,
    };
    await Assert.That(record.InstanceId).IsEqualTo(instanceId);
    await Assert.That(record.ServiceName).IsEqualTo("InventoryWorker");
    await Assert.That(record.HostName).IsEqualTo("pod-abc");
    await Assert.That(record.ProcessId).IsEqualTo(1234);
  }

  [Test]
  public async Task ReceptorProcessingRecord_FullInitialization_RoundTripsAllPropertiesAsync() {
    var eventId = (Guid)TrackedGuid.NewMedo();
    var record = new ReceptorProcessingRecord {
      Id = (Guid)TrackedGuid.NewMedo(),
      EventId = eventId,
      ReceptorName = "OrderReceptor",
      Status = ReceptorProcessingStatus.Processing,
      Attempts = 1,
      Error = null,
      StartedAt = DateTime.UtcNow,
      ProcessedAt = DateTime.UtcNow,
    };
    await Assert.That(record.EventId).IsEqualTo(eventId);
    await Assert.That(record.ReceptorName).IsEqualTo("OrderReceptor");
    await Assert.That(record.Status).IsEqualTo(ReceptorProcessingStatus.Processing);
    await Assert.That(record.Attempts).IsEqualTo(1);
  }
}
