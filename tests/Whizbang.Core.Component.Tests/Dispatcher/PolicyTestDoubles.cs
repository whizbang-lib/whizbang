// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>Records what the dispatcher queues to the outbox; never delivers anything.</summary>
public sealed class PolicyRecordingOutbox : IWorkCoordinatorStrategy {
  private readonly List<OutboxMessage> _queued = [];

  /// <summary>Outbox entries whose payload is of the given type.</summary>
  public IReadOnlyList<OutboxMessage> QueuedOf(Type messageType) {
    var typeName = TypeNameFormatter.AssemblyQualifiedName(messageType);
    lock (_queued) {
      return [.. _queued.Where(m => m.MessageType == typeName)];
    }
  }

  public void QueueOutboxMessage(OutboxMessage message) {
    lock (_queued) {
      _queued.Add(message);
    }
  }

  public void QueueInboxMessage(InboxMessage message) { }
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

  public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => FlushAndGetBatchAsync(flags, ct);

  public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
    Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
}

/// <summary>
/// Keeps what the assertions read: the payload type name and the dispatch context. Nothing recorded
/// with it is ever delivered.
/// </summary>
public sealed class PolicyContextKeepingEnvelopeSerializer : IEnvelopeSerializer {
  public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
    var jsonEnvelope = new MessageEnvelope<System.Text.Json.JsonElement> {
      MessageId = envelope.MessageId,
      Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
      Hops = [],
      DispatchContext = envelope.DispatchContext
    };
    var payloadType = envelope.Payload?.GetType() ?? typeof(TMessage);
    return new SerializedEnvelope(
      jsonEnvelope,
      EnvelopeTypeNameHelper.Format(TypeNameFormatter.AssemblyQualifiedName(payloadType)),
      TypeNameFormatter.AssemblyQualifiedName(payloadType));
  }

  public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) =>
    throw new NotSupportedException("Outbox entries recorded by these tests are never delivered");
}
