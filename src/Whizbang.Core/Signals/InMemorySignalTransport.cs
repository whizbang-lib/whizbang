namespace Whizbang.Core.Signals;

/// <summary>
/// Single-process signal transport: published signals loop straight back to the bus's sink,
/// with no database or <c>NOTIFY</c>. It is the default transport for single-process hosts and
/// the transport unit tests inject — fully deterministic (dispatch is synchronous), so tests
/// assert on captured state rather than timing.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public sealed class InMemorySignalTransport : ISignalTransport {
  private ISignalSink? _sink;

  /// <inheritdoc />
  public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(sink);
    _sink = sink;
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
    where TSignal : ISignal {
    // Target is irrelevant in-process — the single-process loopback delivers to every
    // subscriber of TSignal on this sink regardless of broadcast/streams/instance targeting.
    _ = target;
    var sink = _sink;
    if (sink is null) {
      // Not started: nothing to loop back to.
      return ValueTask.CompletedTask;
    }
    return sink.ReceiveAsync(signal, cancellationToken);
  }
}
