using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Channel surface carrying outbox stream IDs from <c>ClaimWorker</c> to <c>OutboxDrainWorker</c>.
/// Restores the archive-specified poller-vs-drainer split for outbox work: <c>claim_work</c>
/// returns just stream_ids; the drainer reads a stream_id, fetches all leased messages for that
/// stream via <see cref="IWorkCoordinator.FetchOutboxBatchAsync"/>, and publishes them in
/// stream-FIFO order.
/// </summary>
/// <remarks>
/// Channel reader semantics give per-stream serialization for free — exactly one drainer task
/// pulls a given stream_id at a time. Mirror of <see cref="IPerspectiveDrainChannel"/> for outbox.
/// </remarks>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public interface IOutboxDrainChannel {
  /// <summary>Reader for the OutboxDrainWorker.</summary>
  ChannelReader<Guid> Reader { get; }

  /// <summary>Writes a stream_id whose leased outbox rows need draining.</summary>
  ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default);

  /// <summary>Synchronous best-effort write; returns false when the channel is closed.</summary>
  bool TryWrite(Guid streamId);

  /// <summary>
  /// Returns true if the given stream_id is currently being drained by a worker. Defense-in-depth
  /// for <see cref="ClaimWorker"/>: avoids re-queuing stream_ids whose drain is already in flight,
  /// which would cause redundant <c>fetch_outbox_batch</c> calls (each returns 0 rows mid-drain).
  /// </summary>
  bool IsInFlight(Guid streamId) => false;

  /// <summary>Marks a stream_id as currently being drained. Called by <c>OutboxDrainWorker</c>
  /// before fetching; cleared via <see cref="MarkDrained"/> after the batch completes.</summary>
  void MarkDraining(Guid streamId) { }

  /// <summary>Clears the in-flight marker. Called by <c>OutboxDrainWorker</c> after fetch + publish + completion enqueue.</summary>
  void MarkDrained(Guid streamId) { }
}

/// <summary>Default unbounded channel implementation.</summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public sealed class OutboxDrainChannel : IOutboxDrainChannel {
  private readonly Channel<Guid> _channel;
  private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

  /// <summary>Unbounded channel — drain stream IDs are small (16 bytes each) and bounded by the active stream set.</summary>
  public OutboxDrainChannel() {
    _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions {
      SingleReader = false,
      SingleWriter = false,
      AllowSynchronousContinuations = false,
    });
  }

  /// <inheritdoc />
  public ChannelReader<Guid> Reader => _channel.Reader;

  /// <inheritdoc />
  public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default)
    => _channel.Writer.WriteAsync(streamId, cancellationToken);

  /// <inheritdoc />
  public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);

  /// <inheritdoc />
  public bool IsInFlight(Guid streamId) => _inFlight.ContainsKey(streamId);

  /// <inheritdoc />
  public void MarkDraining(Guid streamId) => _inFlight[streamId] = 1;

  /// <inheritdoc />
  public void MarkDrained(Guid streamId) => _inFlight.TryRemove(streamId, out _);
}
