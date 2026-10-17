// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
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

/// <summary>An outbox whose every queue call fails, to drive the failure paths.</summary>
public sealed class ThrowingOutbox : IWorkCoordinatorStrategy {
  public void QueueOutboxMessage(OutboxMessage message) => throw new System.IO.IOException("outbox-boom");
  public void QueueInboxMessage(InboxMessage message) { }
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
  public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;

  public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
    Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
}

/// <summary>A developer hook that refuses every dispatch with the given cause.</summary>
public sealed class RefusingPolicyHook(Whizbang.Core.Dispatch.Policy.DispatchRejection rejection) : Whizbang.Core.Dispatch.Policy.IDispatchPolicyHook {
  public int Order => 1;

  public Whizbang.Core.Dispatch.Policy.DispatchDecision Decide(Whizbang.Core.Dispatch.Policy.DispatchPolicy policy,
      in Whizbang.Core.Dispatch.Policy.DispatchSituation situation, in Whizbang.Core.Dispatch.Policy.DispatchDecision proposed) =>
    Whizbang.Core.Dispatch.Policy.DispatchDecision.Rejected(rejection, Whizbang.Core.Dispatch.Policy.DispatchReason.Hook);
}

/// <summary>
/// An outbox whose flush signals that it started and then waits until the dispatch is canceled, so a
/// test can cancel at an exact point without timing.
/// </summary>
public sealed class BlockingFlushOutbox : IWorkCoordinatorStrategy {
  /// <summary>Completes when the dispatcher starts flushing.</summary>
  public TaskCompletionSource FlushStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

  public void QueueOutboxMessage(OutboxMessage message) { }
  public void QueueInboxMessage(InboxMessage message) { }
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

  public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => FlushAndGetBatchAsync(flags, ct);

  public async Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
    FlushStarted.TrySetResult();
    await Task.Delay(Timeout.Infinite, ct);
    return new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] };
  }
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

/// <summary>Captures warnings and above from every logger category.</summary>
public sealed class PolicyWarningLog : ILoggerProvider {
  public ConcurrentQueue<string> Entries { get; } = new();
  public ILogger CreateLogger(string categoryName) => new Logger(Entries);
  public void Dispose() { }

  private sealed class Logger(ConcurrentQueue<string> entries) : ILogger {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (IsEnabled(logLevel)) {
        entries.Enqueue(formatter(state, exception));
      }
    }
  }
}
