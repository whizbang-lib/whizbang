using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Channel surface carrying inbox stream IDs from <c>ClaimWorker</c> to <c>InboxDrainWorker</c>.
/// Restores the archive-specified poller-vs-drainer split for inbox work: <c>claim_work</c>
/// returns just stream_ids; the drainer reads a stream_id, fetches all leased inbox messages
/// for that stream via <see cref="IWorkCoordinator.FetchInboxBatchAsync"/>, and dispatches each
/// to its handler in stream-FIFO order.
/// </summary>
/// <remarks>
/// Channel reader semantics give per-stream serialization for free.
/// Mirror of <see cref="IPerspectiveDrainChannel"/> and <see cref="IOutboxDrainChannel"/> for inbox.
/// </remarks>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public interface IInboxDrainChannel {
  /// <summary>Reader for the InboxDrainWorker.</summary>
  ChannelReader<Guid> Reader { get; }

  /// <summary>Writes a stream_id whose leased inbox rows need draining.</summary>
  ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default);

  /// <summary>Synchronous best-effort write; returns false when the channel is closed.</summary>
  bool TryWrite(Guid streamId);

  /// <summary>True if the stream_id is currently being drained. Defense-in-depth filter for ClaimWorker.</summary>
  bool IsInFlight(Guid streamId) => false;

  /// <summary>Marks a stream_id as currently being drained.</summary>
  void MarkDraining(Guid streamId) { }

  /// <summary>Clears the in-flight marker after the drain batch completes.</summary>
  void MarkDrained(Guid streamId) { }
}

/// <summary>Default unbounded channel implementation.</summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public sealed class InboxDrainChannel : IInboxDrainChannel {
  private readonly Channel<Guid> _channel;
  private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

  /// <summary>Unbounded channel — drain stream IDs are small and bounded by the active stream set.</summary>
  public InboxDrainChannel() {
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
