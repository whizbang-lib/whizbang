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
/// Direct unit tests for the internal <see cref="WorkCoordinatorQueues"/> composition
/// helper shared by the work coordinator strategies. Focuses on the two surfaces the
/// strategy tests never reach directly: <c>MergeAuditMessages</c> (audit rows must land
/// AFTER lifecycle stages, appended behind already-queued outbox messages) and
/// <c>BuildRequest</c> (queue contents + instance identity + options must map 1:1 onto
/// the <see cref="ProcessWorkBatchRequest"/> handed to the coordinator).
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
public class WorkCoordinatorQueuesTests {

  // ========================================
  // MergeAuditMessages
  // ========================================

  [Test]
  public async Task MergeAuditMessages_WithPendingAudits_AppendsAfterQueuedOutboxMessagesAsync() {
    var queues = new WorkCoordinatorQueues();
    var normalMessage = _createOutboxMessage();
    var auditMessage = _createOutboxMessage();
    queues.AddOutboxMessage(normalMessage, systemEventOptions: null);
    queues.PendingAuditMessages.Add(auditMessage);

    queues.MergeAuditMessages();

    await Assert.That(queues.OutboxMessages.Count).IsEqualTo(2);
    await Assert.That(queues.OutboxMessages[0]).IsEqualTo(normalMessage);
    await Assert.That(queues.OutboxMessages[1]).IsEqualTo(auditMessage);
    await Assert.That(queues.PendingAuditMessages.Count).IsEqualTo(0);
  }

  [Test]
  public async Task MergeAuditMessages_NoPendingAudits_LeavesOutboxQueueUntouchedAsync() {
    var queues = new WorkCoordinatorQueues();
    var normalMessage = _createOutboxMessage();
    queues.AddOutboxMessage(normalMessage, systemEventOptions: null);

    queues.MergeAuditMessages();

    await Assert.That(queues.OutboxMessages.Count).IsEqualTo(1);
    await Assert.That(queues.PendingAuditMessages.Count).IsEqualTo(0);
  }

  [Test]
  public async Task MergeAuditMessages_IsIdempotent_SecondCallAddsNothingAsync() {
    var queues = new WorkCoordinatorQueues();
    queues.PendingAuditMessages.Add(_createOutboxMessage());

    queues.MergeAuditMessages();
    queues.MergeAuditMessages();

    await Assert.That(queues.OutboxMessages.Count).IsEqualTo(1);
    await Assert.That(queues.PendingAuditMessages.Count).IsEqualTo(0);
  }

  // ========================================
  // BuildRequest
  // ========================================

  [Test]
  public async Task BuildRequest_CopiesQueueContentsInstanceIdentityAndOptionsAsync() {
    var queues = new WorkCoordinatorQueues();
    var outboxMessage = _createOutboxMessage();
    var inboxMessage = _createInboxMessage();
    var outboxCompletionId = (Guid)TrackedGuid.NewMedo();
    var inboxCompletionId = (Guid)TrackedGuid.NewMedo();
    var outboxFailureId = (Guid)TrackedGuid.NewMedo();
    var inboxFailureId = (Guid)TrackedGuid.NewMedo();
    queues.AddOutboxMessage(outboxMessage, systemEventOptions: null);
    queues.AddInboxMessage(inboxMessage);
    queues.AddOutboxCompletion(outboxCompletionId, MessageProcessingStatus.Stored);
    queues.AddInboxCompletion(inboxCompletionId, MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored);
    queues.AddOutboxFailure(outboxFailureId, MessageProcessingStatus.Stored, "outbox boom");
    queues.AddInboxFailure(inboxFailureId, MessageProcessingStatus.None, "inbox boom");
    var provider = new FakeInstanceProvider();
    var options = new WorkCoordinatorOptions {
      PartitionCount = 128,
      LeaseSeconds = 45,
      AbandonStaleInstanceThresholdSeconds = 7,
      DebugMode = false
    };

    var request = queues.BuildRequest(provider, options, WorkBatchOptions.SkipInboxClaiming);

    await Assert.That(request.InstanceId).IsEqualTo(provider.InstanceId);
    await Assert.That(request.ServiceName).IsEqualTo("TestService");
    await Assert.That(request.HostName).IsEqualTo("test-host");
    await Assert.That(request.ProcessId).IsEqualTo(4242);
    await Assert.That(request.Metadata).IsNull();
    await Assert.That(request.NewOutboxMessages.Length).IsEqualTo(1);
    await Assert.That(request.NewOutboxMessages[0]).IsEqualTo(outboxMessage);
    await Assert.That(request.NewInboxMessages.Length).IsEqualTo(1);
    await Assert.That(request.NewInboxMessages[0]).IsEqualTo(inboxMessage);
    await Assert.That(request.OutboxCompletions.Length).IsEqualTo(1);
    await Assert.That(request.OutboxCompletions[0].MessageId).IsEqualTo(outboxCompletionId);
    await Assert.That(request.OutboxCompletions[0].Status).IsEqualTo(MessageProcessingStatus.Stored);
    await Assert.That(request.InboxCompletions.Length).IsEqualTo(1);
    await Assert.That(request.InboxCompletions[0].MessageId).IsEqualTo(inboxCompletionId);
    await Assert.That(request.InboxCompletions[0].Status).IsEqualTo(MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored);
    await Assert.That(request.OutboxFailures.Length).IsEqualTo(1);
    await Assert.That(request.OutboxFailures[0].MessageId).IsEqualTo(outboxFailureId);
    await Assert.That(request.OutboxFailures[0].Error).IsEqualTo("outbox boom");
    await Assert.That(request.InboxFailures.Length).IsEqualTo(1);
    await Assert.That(request.InboxFailures[0].MessageId).IsEqualTo(inboxFailureId);
    await Assert.That(request.InboxFailures[0].Error).IsEqualTo("inbox boom");
    await Assert.That(request.ReceptorCompletions.Length).IsEqualTo(0);
    await Assert.That(request.ReceptorFailures.Length).IsEqualTo(0);
    await Assert.That(request.PerspectiveCompletions.Length).IsEqualTo(0);
    await Assert.That(request.PerspectiveEventCompletions.Length).IsEqualTo(0);
    await Assert.That(request.PerspectiveFailures.Length).IsEqualTo(0);
    await Assert.That(request.RenewOutboxLeaseIds.Length).IsEqualTo(0);
    await Assert.That(request.RenewInboxLeaseIds.Length).IsEqualTo(0);
    await Assert.That(request.Flags).IsEqualTo(WorkBatchOptions.SkipInboxClaiming);
    await Assert.That(request.PartitionCount).IsEqualTo(128);
    await Assert.That(request.LeaseSeconds).IsEqualTo(45);
    await Assert.That(request.AbandonStaleInstanceThresholdSeconds).IsEqualTo(7);
  }

  [Test]
  public async Task BuildRequest_DebugModeOption_AddsDebugModeFlagAsync() {
    var queues = new WorkCoordinatorQueues();
    var provider = new FakeInstanceProvider();
    var options = new WorkCoordinatorOptions { DebugMode = true };

    var request = queues.BuildRequest(provider, options, WorkBatchOptions.None);

    await Assert.That(request.Flags).IsEqualTo(WorkBatchOptions.DebugMode);
    await Assert.That(request.NewOutboxMessages.Length).IsEqualTo(0);
    await Assert.That(request.NewInboxMessages.Length).IsEqualTo(0);
  }

  [Test]
  public async Task BuildRequest_DebugModeOptionAndExplicitFlags_CombinesBothAsync() {
    var queues = new WorkCoordinatorQueues();
    var provider = new FakeInstanceProvider();
    var options = new WorkCoordinatorOptions { DebugMode = true };

    var request = queues.BuildRequest(provider, options, WorkBatchOptions.SkipInboxClaiming);

    await Assert.That(request.Flags).IsEqualTo(WorkBatchOptions.SkipInboxClaiming | WorkBatchOptions.DebugMode);
  }

  [Test]
  public async Task BuildRequest_SnapshotsQueues_LaterMutationsDoNotAffectRequestAsync() {
    var queues = new WorkCoordinatorQueues();
    queues.AddOutboxMessage(_createOutboxMessage(), systemEventOptions: null);
    var provider = new FakeInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var request = queues.BuildRequest(provider, options, WorkBatchOptions.None);
    queues.AddOutboxMessage(_createOutboxMessage(), systemEventOptions: null);
    queues.Clear();

    await Assert.That(request.NewOutboxMessages.Length).IsEqualTo(1);
    await Assert.That(queues.OutboxMessages.Count).IsEqualTo(0);
    await Assert.That(queues.IsEmpty).IsTrue();
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

  private static OutboxMessage _createOutboxMessage() {
    var messageId = (Guid)TrackedGuid.NewMedo();
    return new OutboxMessage {
      MessageId = messageId,
      Destination = "orders-topic",
      Envelope = _createJsonEnvelope(),
      EnvelopeType = "EnvType, TestAssembly",
      MessageType = "MsgType, TestAssembly",
      StreamId = (Guid)TrackedGuid.NewMedo(),
      IsEvent = false,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] }
    };
  }

  private static InboxMessage _createInboxMessage() {
    var messageId = (Guid)TrackedGuid.NewMedo();
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "ServiceBusConsumer",
      Envelope = _createJsonEnvelope(),
      EnvelopeType = "EnvType, TestAssembly",
      MessageType = "MsgType, TestAssembly",
      StreamId = (Guid)TrackedGuid.NewMedo(),
      IsEvent = false,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] }
    };
  }

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "TestService";
    public string HostName => "test-host";
    public int ProcessId => 4242;
    public ServiceInstanceInfo ToInfo() => ServiceInstanceInfo.Unknown;
  }
}
