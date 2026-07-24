namespace Whizbang.Core.Signals;

/// <summary>
/// Moves signals for the <see cref="ISignalBus"/>. A transport is a bidirectional
/// <see cref="ISignalSource"/> that additionally supports publishing. The framework ships a
/// Postgres NOTIFY (push) transport and an in-memory transport; applications may register their
/// own. Pull sources (<see cref="IPollSignalSource{TSignal}"/>) are also <see cref="ISignalSource"/>s
/// so subscribers are agnostic to which one raised a signal.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public interface ISignalTransport : ISignalSource {
  /// <summary>
  /// Propagate a published signal (e.g. emit a <c>NOTIFY</c>, or loop back in-memory). The
  /// <paramref name="target"/> selects which channel the transport routes to (broadcast, or a
  /// targeted set of streams/instance). Bus-level validation guarantees the target's kind
  /// matches the signal's <see cref="SignalTargeting"/> before the transport sees it.
  /// </summary>
  ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
    where TSignal : ISignal;
}

/// <summary>
/// The bus-side entry point a transport calls to deliver a received signal to subscribers.
/// Implemented by the bus; transports depend only on this to raise signals.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public interface ISignalSink {
  /// <summary>Deliver a received signal to all subscribers of <typeparamref name="TSignal"/>.</summary>
  ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
    where TSignal : ISignal;
}
