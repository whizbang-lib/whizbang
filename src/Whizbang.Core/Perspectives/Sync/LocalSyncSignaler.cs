using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// In-process implementation of <see cref="IPerspectiveSyncSignaler"/> using concurrent collections.
/// </summary>
/// <remarks>
/// <para>
/// This implementation provides fast, in-process signaling for local (same-instance)
/// perspective synchronization. It uses a pub/sub pattern with perspective type filtering.
/// </para>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncSignalerTests.cs</tests>
public sealed class LocalSyncSignaler : IPerspectiveSyncSignaler {
  private readonly ConcurrentDictionary<Type, ConcurrentBag<Action<PerspectiveCheckpointSignal>>> _subscribers = new();
  private bool _disposed;

  /// <inheritdoc />
  public void SignalCheckpointUpdated(Type perspectiveType, Guid streamId, Guid lastEventId) {
    ArgumentNullException.ThrowIfNull(perspectiveType);

    if (_disposed) {
      return;
    }

    var signal = new PerspectiveCheckpointSignal(
        perspectiveType,
        streamId,
        lastEventId,
        DateTimeOffset.UtcNow);

    // Notify specific perspective subscribers
    if (_subscribers.TryGetValue(perspectiveType, out var handlers)) {
      _notifyHandlers(handlers, signal);
    }
  }

  /// <inheritdoc />
  public IDisposable Subscribe(Type perspectiveType, Action<PerspectiveCheckpointSignal> onSignal) {
    ArgumentNullException.ThrowIfNull(perspectiveType);
    ArgumentNullException.ThrowIfNull(onSignal);

    var handlers = _subscribers.GetOrAdd(perspectiveType, _ => new ConcurrentBag<Action<PerspectiveCheckpointSignal>>());
    handlers.Add(onSignal);

    return new Subscription(this, perspectiveType, onSignal);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (_disposed) {
      return;
    }

    _disposed = true;
    _subscribers.Clear();
  }

  private static void _notifyHandlers(
      ConcurrentBag<Action<PerspectiveCheckpointSignal>> handlers,
      PerspectiveCheckpointSignal signal) {
    foreach (var handler in handlers) {
      try {
        handler(signal);
      } catch {
        // Swallow handler exceptions to prevent one failing handler from
        // blocking others. In production, this should be logged.
      }
    }
  }

  private sealed class Subscription : IDisposable {
    private readonly LocalSyncSignaler _signaler;
    private readonly Type _perspectiveType;
    private readonly Action<PerspectiveCheckpointSignal> _handler;
    private bool _disposed;

    public Subscription(
        LocalSyncSignaler signaler,
        Type perspectiveType,
        Action<PerspectiveCheckpointSignal> handler) {
      _signaler = signaler;
      _perspectiveType = perspectiveType;
      _handler = handler;
    }

    public void Dispose() {
      if (_disposed) {
        return;
      }

      _disposed = true;

      // Remove handler from the bag
      // ConcurrentBag doesn't support removal, so we rebuild without this handler
      if (_signaler._subscribers.TryGetValue(_perspectiveType, out var handlers)) {
        var newHandlers = new ConcurrentBag<Action<PerspectiveCheckpointSignal>>(
            handlers.Where(h => !ReferenceEquals(h, _handler)));
        _signaler._subscribers.TryUpdate(_perspectiveType, newHandlers, handlers);
      }
    }
  }
}
