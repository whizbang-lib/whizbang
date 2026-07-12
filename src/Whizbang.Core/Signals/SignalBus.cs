using System.Collections.Concurrent;

namespace Whizbang.Core.Signals;

/// <summary>
/// Transport-agnostic, multicast implementation of <see cref="ISignalBus"/>. Subscribers are
/// registered per signal type; publishing forwards to every injected <see cref="ISignalTransport"/>,
/// and each transport raises received signals back through <see cref="ReceiveAsync{TSignal}"/>,
/// which fans out to the type's subscribers.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public sealed class SignalBus : ISignalBus, ISignalSink {
  // Keyed by signal type -> SignalHandlerList<TSignal>. No reflection: entries are created and
  // retrieved via the generic type argument, so this stays AOT-safe.
  private readonly ConcurrentDictionary<Type, object> _handlers = new();
  private readonly ISignalTransport[] _transports;

  /// <summary>Create a bus over the given transports.</summary>
  public SignalBus(IEnumerable<ISignalTransport> transports) {
    ArgumentNullException.ThrowIfNull(transports);
    _transports = transports.ToArray();
  }

  /// <summary>
  /// Start every transport, wiring this bus as their sink so received signals are dispatched
  /// to subscribers. Call once before publishing.
  /// </summary>
  public async Task StartAsync(CancellationToken cancellationToken = default) {
    foreach (var transport in _transports) {
      await transport.StartAsync(this, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public async ValueTask PublishAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
    where TSignal : ISignal {
    foreach (var transport in _transports) {
      await transport.PublishAsync(signal, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler)
    where TSignal : ISignal {
    ArgumentNullException.ThrowIfNull(handler);
    var list = (SignalHandlerList<TSignal>)_handlers.GetOrAdd(
      typeof(TSignal),
      static _ => new SignalHandlerList<TSignal>());
    return list.Add(handler);
  }

  /// <inheritdoc />
  public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
    where TSignal : ISignal {
    if (_handlers.TryGetValue(typeof(TSignal), out var obj) && obj is SignalHandlerList<TSignal> list) {
      return list.InvokeAsync(signal, cancellationToken);
    }
    return ValueTask.CompletedTask;
  }
}
