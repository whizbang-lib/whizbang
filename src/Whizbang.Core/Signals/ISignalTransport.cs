namespace Whizbang.Core.Signals;

/// <summary>
/// Moves signals for the <see cref="ISignalBus"/>. A transport is an injected implementation —
/// the framework ships a Postgres NOTIFY (push) transport, a polling (pull) transport, and an
/// in-memory transport, and applications may register their own. Push and pull are peers: both
/// raise received signals into the bus's <see cref="ISignalSink"/>, so subscribers are
/// transport-agnostic.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public interface ISignalTransport {
  /// <summary>
  /// Begin producing signals (open a <c>LISTEN</c>, or start a poll loop). Received signals are
  /// raised into <paramref name="sink"/>, which dispatches them to subscribers.
  /// </summary>
  Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default);

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
