using System.Collections.Concurrent;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Thread-safe in-memory channel for deferred outbox messages.
/// Uses ConcurrentQueue for lock-free, AOT-compatible queuing.
/// </summary>
/// <remarks>
/// <para>This implementation is process-wide and should be registered as a singleton.
/// Messages queued here are picked up by the work coordinator during the next lifecycle loop
/// and written to the outbox in that transaction context.</para>
/// <para>No reflection is used - fully AOT compatible.</para>
/// </remarks>
/// <docs>core-concepts/dispatcher#deferred-event-channel</docs>
/// <tests>Whizbang.Core.Tests/Messaging/DeferredOutboxChannelTests.cs</tests>
public sealed class DeferredOutboxChannel : IDeferredOutboxChannel {
  private readonly ConcurrentQueue<OutboxMessage> _pending = new();

  /// <inheritdoc />
  public async ValueTask QueueAsync(OutboxMessage message, CancellationToken ct = default) {
    // ConcurrentQueue.Enqueue is non-blocking, but we use ValueTask
    // to match the interface signature and allow future async implementations
    _pending.Enqueue(message);
    await ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public IReadOnlyList<OutboxMessage> DrainAll() {
    var result = new List<OutboxMessage>();
    while (_pending.TryDequeue(out var message)) {
      result.Add(message);
    }
    return result;
  }

  /// <inheritdoc />
  public bool HasPending => !_pending.IsEmpty;
}
