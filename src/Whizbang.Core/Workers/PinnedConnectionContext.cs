using System;
using System.Data.Common;
using System.Threading;

namespace Whizbang.Core.Workers;

/// <summary>
/// AsyncLocal accessor for the currently-pinned <see cref="DbConnection"/>.
/// Workers set <see cref="Current"/> via <see cref="Push"/> when borrowing
/// from <see cref="IPinnedConnectionPool"/>; the <see cref="IWorkCoordinator"/>
/// implementations read <see cref="Current"/> on each connection acquisition
/// and prefer the pinned conn over the pgbouncer-pooled one.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the established Whizbang AsyncLocal idiom used by
/// <c>ScopedEventTrackerAccessor.CurrentTracker</c>: the value flows across
/// <c>await</c> boundaries within the same logical control-flow but does NOT
/// leak to sibling tasks spawned with <c>Task.Run</c> (per
/// <see cref="AsyncLocal{T}"/> semantics).
/// </para>
/// <para>
/// Use <see cref="Push"/> in <c>using var scope = ...</c> form so the
/// accessor is reset deterministically — even on exception paths.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
public static class PinnedConnectionContext {
  private static readonly AsyncLocal<DbConnection?> _current = new();

  /// <summary>
  /// The currently-pinned connection for this async context, or <c>null</c>
  /// when no pinned connection is in flight.
  /// </summary>
  public static DbConnection? Current => _current.Value;

  /// <summary>
  /// Sets <see cref="Current"/> to <paramref name="connection"/> and returns
  /// a disposable that restores the previous value on disposal. Designed for
  /// <c>using</c>-statement use at the borrow site:
  /// <code>
  /// await using var pin = await pool.TryPinForAsync(typeof(MyWorker), ct);
  /// using var ctx = PinnedConnectionContext.Push(pin.Connection);
  /// // ... do work; coordinator reads PinnedConnectionContext.Current ...
  /// </code>
  /// </summary>
  /// <param name="connection">The connection to pin, or <c>null</c> to explicitly clear (e.g. when the borrow returned a no-op).</param>
  public static ResetScope Push(DbConnection? connection) {
    var previous = _current.Value;
    _current.Value = connection;
    return new ResetScope(previous);
  }

  /// <summary>Disposable that restores the previous <see cref="Current"/> value.</summary>
  public readonly struct ResetScope : IDisposable {
    private readonly DbConnection? _previous;

    internal ResetScope(DbConnection? previous) {
      _previous = previous;
    }

    /// <inheritdoc />
    public void Dispose() {
      _current.Value = _previous;
    }
  }
}
