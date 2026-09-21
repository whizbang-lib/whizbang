namespace Whizbang.Core.Signals;

/// <summary>
/// Typed, multicast publish/subscribe for control-plane signals. The bus is
/// <em>transport-ignorant</em>: signals reach subscribers through injected
/// <see cref="ISignalTransport"/> implementations (Postgres NOTIFY push, polling pull,
/// in-memory), and subscribers cannot tell which transport delivered a signal.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public interface ISignalBus {
  /// <summary>True when a real implementation is registered. The framework's null default returns false so
  /// a consumer takes the same skip path an unregistered subsystem produced, without a null check.</summary>
  bool IsConfigured => true;

  /// <summary>
  /// Publish a signal. Doorbell semantics: the signal carries no authoritative payload —
  /// subscribers fetch current state from the database. Reliability (best-effort vs durable)
  /// and targeting-kind (broadcast vs targeted) come from the signal type's static declarations;
  /// the per-call <paramref name="target"/> says <em>which</em> target for a targeted signal.
  /// The target kind must match the signal's <see cref="SignalTargeting"/> — a mismatch throws.
  /// </summary>
  ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
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
public interface ISignalSubscription : IDisposable;

/// <summary>
/// The framework's null default for <see cref="ISignalBus"/>: reports <see cref="ISignalBus.IsConfigured"/> false,
/// publishes nothing and hands out subscriptions that never fire. Registered by the core with TryAdd; the signal
/// bus subsystem displaces it through <see cref="NullDefaultServiceCollectionExtensions"/>.
/// </summary>
public sealed class NullSignalBus : ISignalBus, INullDefault {
  private NullSignalBus() { }

  /// <summary>The shared instance.</summary>
  public static NullSignalBus Instance { get; } = new();

  /// <inheritdoc />
  public bool IsConfigured => false;

  /// <inheritdoc />
  public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;

  /// <inheritdoc />
  public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal => NullSubscription.Instance;

  private sealed class NullSubscription : ISignalSubscription {
    public static NullSubscription Instance { get; } = new();
    public void Dispose() { }
  }
}
