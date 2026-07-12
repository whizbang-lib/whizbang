namespace Whizbang.Core.Signals;

/// <summary>
/// Typed, multicast publish/subscribe for control-plane signals. The bus is
/// <em>transport-ignorant</em>: signals reach subscribers through injected
/// <see cref="ISignalTransport"/> implementations (Postgres NOTIFY push, polling pull,
/// in-memory), and subscribers cannot tell which transport delivered a signal.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public interface ISignalBus {
  /// <summary>
  /// Publish a signal. Doorbell semantics: the signal carries no authoritative payload —
  /// subscribers fetch current state from the database. Routing (targeted vs broadcast) and
  /// reliability (best-effort vs durable) come from the signal type's static declarations.
  /// </summary>
  ValueTask PublishAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
    where TSignal : ISignal;

  /// <summary>
  /// Subscribe a handler for a signal type. The handler must be fast and non-blocking
  /// (<em>enqueue-and-return</em>) because dispatch may run on the shared notify connection's
  /// receive loop. Dispose the returned handle to unsubscribe.
  /// </summary>
  ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler)
    where TSignal : ISignal;
}

/// <summary>Handle for an active subscription. Dispose to unsubscribe.</summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public interface ISignalSubscription : IDisposable {
}
