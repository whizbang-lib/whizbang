namespace Whizbang.Core.Perspectives;

/// <summary>
/// In-process serializer for perspective Apply operations on the same
/// <c>(streamId, perspectiveName)</c>. Closes the rewind-vs-live race window
/// where the generated runner's <c>RewindAndRunAsync</c> reads a snapshot,
/// applies events in memory, and persists with a single <c>UpsertAsync</c>
/// while the live drain path concurrently applies a new event on the same
/// row — last-writer-wins on the row replacement drops one path's increment.
/// </summary>
/// <remarks>
/// <para>
/// A production bulk import of several hundred records landed a handful short
/// of the full count on <c>CompletedItems</c> for a bulk-import orchestration
/// saga — the lost increments correlated exactly with an equal number of
/// <c>PerspectiveRewindStarted</c>/<c>Completed</c> pairs during the burst.
/// </para>
/// <para>
/// <see cref="IPerspectiveStreamLocker"/> handles cross-pod safety (and is
/// already taken around the worker's rewind path). It does NOT close the
/// same-pod race because <c>TryAcquireLockAsync</c> is idempotent for the
/// calling instance — the live drain path on the same instance would always
/// succeed even if it tried to acquire. This coordinator complements the
/// cross-pod locker with in-process mutual exclusion: every Apply path on
/// the SAME pod for the SAME <c>(streamId, perspectiveName)</c> serializes
/// through one <see cref="System.Threading.SemaphoreSlim"/>.
/// </para>
/// <para>
/// Registered as a singleton in <c>AddWhizbangCore</c>. The semaphores are
/// allocated lazily per stream + perspective via <c>ConcurrentDictionary
/// .GetOrAdd</c> and are never explicitly removed — production deployments
/// have bounded active-stream counts; if a future workload needs eviction,
/// add an expiry sweep here without changing the public surface.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rewind</docs>
public interface IPerspectiveApplyCoordinator {
  /// <summary>
  /// Acquires exclusive Apply access for the given <c>(streamId, perspectiveName)</c>.
  /// Returns an <see cref="IAsyncDisposable"/> that releases on dispose;
  /// the caller MUST <c>await using</c> the result so the release fires even
  /// on exception.
  /// </summary>
  /// <param name="streamId">Stream being applied.</param>
  /// <param name="perspectiveName">Perspective applying it.</param>
  /// <param name="cancellationToken">Cancellation token honored during the wait
  /// for the semaphore.</param>
  /// <returns>Disposable lock handle that releases when disposed.</returns>
  Task<IAsyncDisposable> AcquireAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default);
}
