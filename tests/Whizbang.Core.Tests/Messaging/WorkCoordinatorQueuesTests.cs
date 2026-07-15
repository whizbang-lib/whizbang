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
/// helper shared by the work coordinator strategies. Focuses on <c>MergeAuditMessages</c>
/// (audit rows must land AFTER lifecycle stages, appended behind already-queued outbox
/// messages) — a surface the strategy tests never reach directly.
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

}
