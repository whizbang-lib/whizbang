// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Default <see cref="IPerspectiveApplyCoordinator"/> backed by reference-counted,
/// per-key <see cref="SemaphoreSlim"/>s.
/// </summary>
/// <remarks>
/// <para>
/// A key's lock exists only while something references it (#2227): an acquire takes a
/// reference before it waits, and the handle's release (or a wait that is canceled)
/// gives it back. The last reference out removes the key, so the coordinator's memory
/// follows the number of applies in flight, not the number of distinct streams the
/// process has ever seen. A waiter queued on a key holds a reference, so the holder's
/// release never drops a lock a waiter is about to take, and a later acquire always
/// finds the same lock. The reference count is kept under one short lock: taking or
/// giving back a reference is a dictionary lookup, never a wait. Releases are
/// reentrancy-safe via the returned handle's <c>DisposeAsync</c>; the handle
/// itself is reusable only once (matches <see cref="IAsyncDisposable"/>'s
/// "dispose-then-discard" contract).
/// </para>
/// <para>
/// The acquisition wait is unbounded by design — the holder is a same-pod apply
/// that is expected to finish. But a holder CAN leak (#679: an apply task
/// abandoned by lease-tied cancellation still owns the lock, and every later
/// apply for that key then parks forever — a production wedge showed six hours
/// of total perspective silence, leases renewing throughout, drain consumers
/// consumed one per wedged key). The coordinator cannot free a leaked lock, but
/// it must not be SILENT about the wait: every <see cref="WarnInterval"/> spent
/// waiting logs a WARN naming the key and the accumulated wait, so the wedge is
/// visible the moment it forms instead of after hours of archaeology.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rewind</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/RewindLiveApplyRaceTests.cs:Rewind_ConcurrentWithLiveApply_WithCoordinator_RetainsAllIncrementsAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/PerspectiveApplyCoordinatorDiagnosticsTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/PerspectiveApplyCoordinatorLifetimeTests.cs</tests>
public sealed partial class PerspectiveApplyCoordinator(
    ILogger<PerspectiveApplyCoordinator> logger) : IPerspectiveApplyCoordinator {
  private readonly ILogger<PerspectiveApplyCoordinator> _logger =
    ArgumentGuard.NotNull(logger);
  private readonly Dictionary<(Guid streamId, string perspectiveName), KeyLock> _byKey = [];
  private readonly Lock _sync = new();

  /// <summary>
  /// How long one acquisition may wait before each slow-wait WARN. Internal seam so tests
  /// exercise the diagnostic in milliseconds; production keeps the default.
  /// </summary>
  internal TimeSpan WarnInterval { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// The number of keys whose lock is currently referenced by a holder or a waiter.
  /// Zero once every apply has released.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/PerspectiveApplyCoordinatorLifetimeTests.cs:DistinctStreams_AfterEveryApplyReleases_CoordinatorHoldsNoEntriesAsync</tests>
  internal int TrackedKeyCount {
    get {
      lock (_sync) {
        return _byKey.Count;
      }
    }
  }

  /// <inheritdoc />
  public async Task<IAsyncDisposable> AcquireAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(perspectiveName);
    var key = (streamId, perspectiveName);
    var keyLock = _addReference(key);
    var acquired = false;
    try {
      // Fast path: uncontended acquire stays silent and allocation-light.
      if (!await keyLock.Semaphore.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false)) {
        var intervalsWaited = 0;
        while (!await keyLock.Semaphore.WaitAsync(WarnInterval, cancellationToken).ConfigureAwait(false)) {
          intervalsWaited++;
          LogApplyLockSlowAcquisition(
            _logger, perspectiveName, streamId, intervalsWaited * WarnInterval.TotalSeconds);
        }
      }
      acquired = true;
    } finally {
      if (!acquired) {
        // A canceled wait gives its reference back, or the key's lock is never released.
        _releaseReference(key, keyLock);
      }
    }
    return new Handle(this, key, keyLock);
  }

  private KeyLock _addReference((Guid streamId, string perspectiveName) key) {
    lock (_sync) {
      if (!_byKey.TryGetValue(key, out var keyLock)) {
        keyLock = new KeyLock();
        _byKey.Add(key, keyLock);
      }
      keyLock.References++;
      return keyLock;
    }
  }

  private void _releaseReference((Guid streamId, string perspectiveName) key, KeyLock keyLock) {
    lock (_sync) {
      if (--keyLock.References == 0) {
        _byKey.Remove(key);
      }
    }
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Apply lock for perspective {PerspectiveName} / stream {StreamId} still not acquired after {WaitedSeconds}s — the current holder is not completing (a leaked lock from an abandoned apply wedges this key permanently and consumes a drain consumer; #679)")]
  static partial void LogApplyLockSlowAcquisition(ILogger logger, string perspectiveName, Guid streamId, double waitedSeconds);

  /// <summary>One key's lock and the number of holders and waiters referencing it; guarded by <c>_sync</c>.</summary>
  private sealed class KeyLock {
    public SemaphoreSlim Semaphore { get; } = new(initialCount: 1, maxCount: 1);
    public int References { get; set; }
  }

  private sealed class Handle(
      PerspectiveApplyCoordinator owner,
      (Guid streamId, string perspectiveName) key,
      KeyLock keyLock) : IAsyncDisposable {
    private int _disposed;
    public ValueTask DisposeAsync() {
      if (Interlocked.Exchange(ref _disposed, 1) == 0) {
        // Release before giving back the reference: a queued waiter holds its own
        // reference, so the lock it is about to take stays in the map.
        keyLock.Semaphore.Release();
        owner._releaseReference(key, keyLock);
      }
      return ValueTask.CompletedTask;
    }
  }
}
