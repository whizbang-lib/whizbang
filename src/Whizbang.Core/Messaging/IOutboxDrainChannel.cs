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
}

/// <summary>Default unbounded channel implementation.</summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public sealed class OutboxDrainChannel : IOutboxDrainChannel {
  private readonly Channel<Guid> _channel;

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
}
