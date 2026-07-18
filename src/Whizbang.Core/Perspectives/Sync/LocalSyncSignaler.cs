using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncSignalerTests.cs</tests>
public sealed partial class LocalSyncSignaler(ILogger<LocalSyncSignaler>? logger = null) : IPerspectiveSyncSignaler {
  private readonly ConcurrentDictionary<Type, ConcurrentBag<Action<PerspectiveCursorSignal>>> _subscribers = new();
  // Null-object default so the drop is ALWAYS logged: DI supplies a real logger in production; the
  // NullLogger fallback only applies to manual construction / a deliberately log-free host.
  private readonly ILogger<LocalSyncSignaler> _logger = logger ?? NullLogger<LocalSyncSignaler>.Instance;
  private bool _disposed;

  /// <inheritdoc />
  public void SignalCheckpointUpdated(Type perspectiveType, Guid streamId, Guid lastEventId) {
    ArgumentNullException.ThrowIfNull(perspectiveType);

    if (_disposed) {
      return;
    }

    var signal = new PerspectiveCursorSignal(
        perspectiveType,
        streamId,
        lastEventId,
        DateTimeOffset.UtcNow);

    // Notify specific perspective subscribers
    if (_subscribers.TryGetValue(perspectiveType, out var handlers)) {
      _notifyHandlers(handlers, signal, _logger);
    }
  }

  /// <inheritdoc />
  public IDisposable Subscribe(Type perspectiveType, Action<PerspectiveCursorSignal> onSignal) {
    ArgumentNullException.ThrowIfNull(perspectiveType);
    ArgumentNullException.ThrowIfNull(onSignal);

    var handlers = _subscribers.GetOrAdd(perspectiveType, _ => []);
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
      ConcurrentBag<Action<PerspectiveCursorSignal>> handlers,
      PerspectiveCursorSignal signal,
      ILogger<LocalSyncSignaler> logger) {
    foreach (var handler in handlers) {
      try {
        handler(signal);
      } catch (Exception ex) {
        // One failing handler must not block the others — but never silently. A dropped signal can
        // leave a sync waiter blocked until its poll/timeout, so log it.
        LogHandlerThrew(logger, ex, signal.PerspectiveType.Name);
      }
    }
  }

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "A perspective sync handler threw for {PerspectiveType}; continuing with the remaining handlers."
  )]
  private static partial void LogHandlerThrew(ILogger logger, Exception ex, string perspectiveType);

  private sealed class Subscription(
      LocalSyncSignaler signaler,
      Type perspectiveType,
      Action<PerspectiveCursorSignal> handler) : IDisposable {
    private readonly LocalSyncSignaler _signaler = signaler;
    private readonly Type _perspectiveType = perspectiveType;
    private readonly Action<PerspectiveCursorSignal> _handler = handler;
    private bool _disposed;

    public void Dispose() {
      if (_disposed) {
        return;
      }

      _disposed = true;

      // Remove handler from the bag
      // ConcurrentBag doesn't support removal, so we rebuild without this handler
      if (_signaler._subscribers.TryGetValue(_perspectiveType, out var handlers)) {
        var newHandlers = new ConcurrentBag<Action<PerspectiveCursorSignal>>(
            handlers.Where(h => !ReferenceEquals(h, _handler)));
        _signaler._subscribers.TryUpdate(_perspectiveType, newHandlers, handlers);
      }
    }
  }
}
