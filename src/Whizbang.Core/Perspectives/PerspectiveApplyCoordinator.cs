using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Default <see cref="IPerspectiveApplyCoordinator"/> backed by per-key
/// <see cref="SemaphoreSlim"/>s in a <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Allocations are lazy: a semaphore is created the first time a key is
/// acquired and is then reused for the lifetime of the process. Releases are
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
/// <tests>tests/Whizbang.Core.Tests/Perspectives/RewindLiveApplyRaceTests.cs:Rewind_ConcurrentWithLiveApply_WithCoordinator_RetainsAllIncrementsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveApplyCoordinatorDiagnosticsTests.cs</tests>
public sealed partial class PerspectiveApplyCoordinator(
    ILogger<PerspectiveApplyCoordinator> logger) : IPerspectiveApplyCoordinator {
  private readonly ILogger<PerspectiveApplyCoordinator> _logger =
    logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly ConcurrentDictionary<(Guid streamId, string perspectiveName), SemaphoreSlim> _byKey = new();

  /// <summary>
  /// How long one acquisition may wait before each slow-wait WARN. Internal seam so tests
  /// exercise the diagnostic in milliseconds; production keeps the default.
  /// </summary>
  internal TimeSpan WarnInterval { get; set; } = TimeSpan.FromSeconds(30);

  /// <inheritdoc />
  public async Task<IAsyncDisposable> AcquireAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(perspectiveName);
    var semaphore = _byKey.GetOrAdd((streamId, perspectiveName), static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
    // Fast path: uncontended acquire stays silent and allocation-light.
    if (!await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false)) {
      var intervalsWaited = 0;
      while (!await semaphore.WaitAsync(WarnInterval, cancellationToken).ConfigureAwait(false)) {
        intervalsWaited++;
        LogApplyLockSlowAcquisition(
          _logger, perspectiveName, streamId, intervalsWaited * WarnInterval.TotalSeconds);
      }
    }
    return new _Handle(semaphore);
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Apply lock for perspective {PerspectiveName} / stream {StreamId} still not acquired after {WaitedSeconds}s — the current holder is not completing (a leaked lock from an abandoned apply wedges this key permanently and consumes a drain consumer; #679)")]
  static partial void LogApplyLockSlowAcquisition(ILogger logger, string perspectiveName, Guid streamId, double waitedSeconds);

  private sealed class _Handle(SemaphoreSlim semaphore) : IAsyncDisposable {
    private int _disposed;
    public ValueTask DisposeAsync() {
      if (Interlocked.Exchange(ref _disposed, 1) == 0) {
        semaphore.Release();
      }
      return ValueTask.CompletedTask;
    }
  }
}
