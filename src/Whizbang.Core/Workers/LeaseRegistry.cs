using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Per-instance registry of currently-leased <see cref="LeaseHandle"/> objects keyed by
/// (<see cref="WorkCategory"/>, work_id). Phase H step 9 slice 1.
/// </summary>
/// <remarks>
/// <para>
/// Workers register their handle at dispatch start; <see cref="LeaseRenewalWorker"/> looks up
/// by (category, work_id) when it renews a DB lease and calls
/// <see cref="LeaseHandle.TryExtendDeadline"/> so the in-process CT deadline tracks the SQL lease.
/// </para>
/// <para>
/// Auto-removal: handles raise <see cref="LeaseHandle.Disposed"/> on dispose; this registry
/// subscribes when registering so workers don't have to remember to unregister explicitly. A
/// duplicate <see cref="Register"/> on the same key throws <see cref="InvalidOperationException"/>
/// — that signals a real bug in a dispatch worker (the same row claimed twice in flight).
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/lease-cancellation</docs>
public sealed class LeaseRegistry {
  private readonly ConcurrentDictionary<(WorkCategory Category, Guid WorkId), LeaseHandle> _handles = new();

  /// <summary>Number of currently-registered handles. Useful for tests + observability.</summary>
  public int Count => _handles.Count;

  /// <summary>
  /// Registers <paramref name="handle"/> by its (Category, WorkId). Throws when a handle is
  /// already registered under that key. Subscribes to <see cref="LeaseHandle.Disposed"/> so the
  /// handle auto-removes when disposed.
  /// </summary>
  public void Register(LeaseHandle handle) {
    ArgumentNullException.ThrowIfNull(handle);
    var key = (handle.Category, handle.WorkId);
    if (!_handles.TryAdd(key, handle)) {
      throw new InvalidOperationException(
        $"A lease handle is already registered for {handle.Category}/{handle.WorkId}. " +
        "This signals a bug — the dispatch worker should not claim the same row twice in flight.");
    }
    handle.Disposed += _onHandleDisposed;
  }

  /// <summary>
  /// Looks up a registered handle. Returns <c>false</c> when no handle exists for that key
  /// (either never registered, or already disposed and auto-removed).
  /// </summary>
  public bool TryGet(WorkCategory category, Guid workId, out LeaseHandle? handle) {
    return _handles.TryGetValue((category, workId), out handle);
  }

  private void _onHandleDisposed(LeaseHandle handle) {
    var key = (handle.Category, handle.WorkId);
    // Race-safe: only remove if the value at the key is still THIS handle. Prevents removing
    // a freshly-registered handle if dispose+re-register interleave (shouldn't happen but
    // cheap to defend against).
    ((System.Collections.Generic.ICollection<KeyValuePair<(WorkCategory, Guid), LeaseHandle>>)_handles)
      .Remove(new KeyValuePair<(WorkCategory, Guid), LeaseHandle>(key, handle));
  }
}
