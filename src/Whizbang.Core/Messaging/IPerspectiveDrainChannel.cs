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
}

/// <summary>Default unbounded channel implementation.</summary>
/// <docs>fundamentals/perspectives/drain-mode</docs>
public sealed class PerspectiveDrainChannel : IPerspectiveDrainChannel {
  private readonly Channel<Guid> _channel;

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
}
