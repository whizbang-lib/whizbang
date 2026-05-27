namespace Whizbang.Core.Messaging;

/// <summary>
/// Process-wide concurrency cap on <see cref="IWorkCoordinator"/> calls. Defense-in-depth
/// guard against runaway connection-pool draw if Npgsql config drifts (e.g. someone bumps
/// <c>Maximum Pool Size</c> without revisiting the budget). Wraps each coordinator method
/// invocation; when the cap is hit, callers wait on the semaphore rather than erroring.
/// </summary>
/// <remarks>
/// Singleton. Disabled if <see cref="MaxConcurrent"/> is &lt;= 0.
/// </remarks>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class WorkCoordinatorGate : IDisposable {
  private readonly SemaphoreSlim? _semaphore;

  /// <summary>Maximum concurrent calls. 0 disables the cap.</summary>
  public int MaxConcurrent { get; }

  /// <summary>Creates a gate with the given concurrency limit. 0 means unbounded (disabled).</summary>
  public WorkCoordinatorGate(int maxConcurrent) {
    MaxConcurrent = maxConcurrent;
    _semaphore = maxConcurrent > 0 ? new SemaphoreSlim(maxConcurrent, maxConcurrent) : null;
  }

  /// <summary>
  /// Acquire a slot. Returns a disposable that releases on dispose.
  /// </summary>
  public async ValueTask<Releaser> AcquireAsync(CancellationToken cancellationToken = default) {
    if (_semaphore is null) {
      return default;
    }
    await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    return new Releaser(_semaphore);
  }

  /// <summary>Disposable returned by <see cref="AcquireAsync"/> — releases the slot on dispose.</summary>
  public readonly struct Releaser : IDisposable {
    private readonly SemaphoreSlim? _semaphore;
    internal Releaser(SemaphoreSlim semaphore) {
      _semaphore = semaphore;
    }
    /// <inheritdoc />
    public void Dispose() {
      _semaphore?.Release();
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    _semaphore?.Dispose();
  }
}
