using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Channel surface carrying drain-mode stream IDs from <c>ClaimWorker</c> to
/// <c>PerspectiveWorker</c>. Drain mode is the SQL-detected case where a stream has many leased
/// perspective events ready for batch processing — <c>claim_work</c> returns the stream ID
/// (rather than per-event work items) so the worker can do a single batched fetch + run.
/// </summary>
/// <remarks>
/// Separate from <see cref="IPerspectiveChannelWriter"/> because the payload is a bare stream
/// <see cref="Guid"/>, not a <see cref="PerspectiveWork"/> item. Singleton so producer and
/// consumer share the same underlying channel.
/// </remarks>
/// <docs>fundamentals/perspectives/drain-mode</docs>
public interface IPerspectiveDrainChannel {
  /// <summary>Reader for the worker.</summary>
  ChannelReader<Guid> Reader { get; }

  /// <summary>Writes a drain-mode stream ID for the worker to pick up.</summary>
  ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default);

  /// <summary>Synchronous best-effort write; returns false when the channel is closed.</summary>
  bool TryWrite(Guid streamId);

  /// <summary>
  /// Returns true if the given stream_id is currently being drained by a worker.
  /// Defense-in-depth for <see cref="ClaimWorker"/>: avoids re-queuing stream_ids whose drain
  /// is already in flight — symmetric with <see cref="IOutboxDrainChannel"/> Part B.
  /// </summary>
  bool IsInFlight(Guid streamId) => false;

  /// <summary>Marks a stream_id as currently being drained. Called by <c>PerspectiveWorker</c>
  /// before processing the stream; cleared via <see cref="MarkDrained"/> after the drain session.</summary>
  void MarkDraining(Guid streamId) { }

  /// <summary>Clears the in-flight marker. Called by <c>PerspectiveWorker</c> when the drain session ends.</summary>
  void MarkDrained(Guid streamId) { }
}

/// <summary>Default unbounded channel implementation.</summary>
/// <docs>fundamentals/perspectives/drain-mode</docs>
public sealed class PerspectiveDrainChannel : IPerspectiveDrainChannel {
  private readonly Channel<Guid> _channel;
  private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

  /// <summary>Creates an unbounded channel — drain stream IDs are small and infrequent.</summary>
  public PerspectiveDrainChannel() {
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
