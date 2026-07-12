namespace Whizbang.Core.Signals;

/// <summary>
/// Thread-safe list of handlers for one signal type. Uses copy-on-write so dispatch iterates a
/// stable snapshot without holding a lock across handler invocations (handlers must be
/// non-blocking, but must never be able to deadlock the subscribe/unsubscribe path).
/// </summary>
internal sealed class SignalHandlerList<TSignal> where TSignal : ISignal {
  private readonly Lock _gate = new();
  private Func<TSignal, ValueTask>[] _handlers = [];

  /// <summary>Register a handler; the returned handle removes it on dispose.</summary>
  public ISignalSubscription Add(Func<TSignal, ValueTask> handler) {
    lock (_gate) {
      var next = new Func<TSignal, ValueTask>[_handlers.Length + 1];
      Array.Copy(_handlers, next, _handlers.Length);
      next[_handlers.Length] = handler;
      _handlers = next;
    }
    return new Subscription(this, handler);
  }

  private void _remove(Func<TSignal, ValueTask> handler) {
    lock (_gate) {
      var index = Array.IndexOf(_handlers, handler);
      if (index < 0) {
        return;
      }
      var next = new Func<TSignal, ValueTask>[_handlers.Length - 1];
      Array.Copy(_handlers, 0, next, 0, index);
      Array.Copy(_handlers, index + 1, next, index, _handlers.Length - index - 1);
      _handlers = next;
    }
  }

  /// <summary>Invoke every current handler with the signal.</summary>
  public async ValueTask InvokeAsync(TSignal signal, CancellationToken cancellationToken) {
    var snapshot = Volatile.Read(ref _handlers);
    foreach (var handler in snapshot) {
      cancellationToken.ThrowIfCancellationRequested();
      await handler(signal).ConfigureAwait(false);
    }
  }

  private sealed class Subscription : ISignalSubscription {
    private readonly SignalHandlerList<TSignal> _owner;
    private Func<TSignal, ValueTask>? _handler;

    public Subscription(SignalHandlerList<TSignal> owner, Func<TSignal, ValueTask> handler) {
      _owner = owner;
      _handler = handler;
    }

    public void Dispose() {
      var h = Interlocked.Exchange(ref _handler, null);
      if (h is not null) {
        _owner._remove(h);
      }
    }
  }
}
