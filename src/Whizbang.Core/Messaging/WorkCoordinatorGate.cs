using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Process-wide concurrency cap on <see cref="IWorkCoordinator"/> calls. Defense-in-depth
/// guard against runaway connection-pool draw if Npgsql config drifts (e.g. someone bumps
/// <c>Maximum Pool Size</c> without revisiting the budget). Wraps each coordinator method
/// invocation; when the cap is hit, callers wait on the semaphore rather than erroring.
/// </summary>
/// <remarks>
/// <para>
/// Singleton. Disabled if <see cref="MaxConcurrent"/> is &lt;= 0.
/// </para>
/// <para>
/// v0.654 (Jun 2026) hardening: <see cref="AcquireAsync(CancellationToken)"/> now applies a
/// deadline to its internal <see cref="SemaphoreSlim.WaitAsync(int, CancellationToken)"/>
/// call. Slot-3 forensic confirmed the cooperative-CT-only pattern (the v0.648 version
/// used <c>WaitAsync(ct)</c> with the worker's stoppingToken, which only fires at pod
/// shutdown) lets a saturated gate hang every caller silently — no exception, no log,
/// no timeout. With <see cref="AcquireTimeoutMilliseconds"/> set, the gate logs a Warning
/// and proceeds without holding a slot when the deadline elapses; saturation becomes
/// observable instead of silently fatal.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed partial class WorkCoordinatorGate : IDisposable {
  private readonly SemaphoreSlim? _semaphore;
  private readonly ILogger<WorkCoordinatorGate>? _logger;
  private readonly Histogram<double>? _holdDurationHistogram;

  /// <summary>Maximum concurrent calls. 0 disables the cap.</summary>
  public int MaxConcurrent { get; }

  /// <summary>
  /// Deadline in milliseconds for an individual <see cref="AcquireAsync"/> call to acquire
  /// a slot. When the deadline elapses, the call logs a Warning and returns a degraded
  /// <see cref="Releaser"/> that holds no slot — the caller proceeds without gate
  /// protection rather than waiting forever. <c>0</c> or a negative value disables the
  /// deadline (the pre-v0.654 behavior — wait indefinitely).
  /// </summary>
  /// <remarks>
  /// Default 30000 ms (30 s). Tuning guidance: the gate exists to protect the connection
  /// pool, so the deadline should be longer than a normal acquire-wait under healthy load
  /// (sub-second) but short enough that saturation surfaces within a single operator
  /// pager-window. Half a minute is the floor; higher values trade observability for
  /// pool protection.
  /// </remarks>
  public int AcquireTimeoutMilliseconds { get; }

  /// <summary>
  /// Creates a gate with the given concurrency limit and acquire deadline.
  /// <paramref name="maxConcurrent"/> &lt;= 0 disables the cap entirely;
  /// <paramref name="acquireTimeoutMilliseconds"/> &lt;= 0 disables the deadline
  /// (the gate waits indefinitely — same as the pre-v0.654 behavior).
  /// </summary>
  public WorkCoordinatorGate(
      int maxConcurrent,
      int acquireTimeoutMilliseconds = 30000,
      ILogger<WorkCoordinatorGate>? logger = null,
      WorkCoordinatorMetrics? metrics = null) {
    MaxConcurrent = maxConcurrent;
    AcquireTimeoutMilliseconds = acquireTimeoutMilliseconds;
    _semaphore = maxConcurrent > 0 ? new SemaphoreSlim(maxConcurrent, maxConcurrent) : null;
    _logger = logger;
    _holdDurationHistogram = metrics?.GateHoldDuration;
  }

  /// <summary>
  /// Acquire a slot. Returns a disposable that releases on dispose.
  /// </summary>
  /// <remarks>
  /// When the gate has a configured deadline and the semaphore is saturated for longer
  /// than that deadline, the call returns a degraded <see cref="Releaser"/> that holds no
  /// slot — the caller proceeds, the saturation is logged at Warning, and the gate
  /// becomes advisory for that call rather than blocking. The alternative (the
  /// pre-v0.654 behavior) was to wait forever silently, which surfaced as the slot-3
  /// "stuck row that never DLQ-promotes" pattern.
  /// </remarks>
  public async ValueTask<Releaser> AcquireAsync(
      CancellationToken cancellationToken = default,
      [CallerMemberName] string caller = "<unknown>") {
    if (_semaphore is null) {
      return default;
    }
    var currentCount = _semaphore.CurrentCount;
    if (_logger is not null) {
      LogAcquireEntry(_logger, currentCount, MaxConcurrent, AcquireTimeoutMilliseconds);
    }
    if (AcquireTimeoutMilliseconds <= 0) {
      // Caller opted out of the deadline — preserve the pre-v0.654 behavior verbatim.
      await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
      if (_logger is not null) {
        LogAcquireGrantedNoDeadline(_logger);
      }
      return new Releaser(_semaphore, _holdDurationHistogram, caller);
    }
    var acquired = await _semaphore
      .WaitAsync(AcquireTimeoutMilliseconds, cancellationToken)
      .ConfigureAwait(false);
    if (acquired) {
      if (_logger is not null) {
        LogAcquireGranted(_logger, _semaphore.CurrentCount, MaxConcurrent);
      }
      return new Releaser(_semaphore, _holdDurationHistogram, caller);
    }
    if (_logger is not null) {
      LogAcquireTimedOut(_logger, AcquireTimeoutMilliseconds, MaxConcurrent);
    }
    // Degrade gracefully: return a no-op Releaser so the caller proceeds without
    // holding a slot. The cap becomes advisory for this single call; pool exhaustion
    // (if it materialises) surfaces at the Npgsql layer with a real exception instead
    // of a silent indefinite hang in our own code.
    // No-op Releaser has no histogram → no observation recorded; correct because
    // nothing was held.
    return default;
  }

  /// <summary>Disposable returned by <see cref="AcquireAsync"/> — releases the slot on dispose.</summary>
  /// <remarks>
  /// On dispose: releases the underlying semaphore slot AND records the elapsed
  /// time into <see cref="WorkCoordinatorMetrics.GateHoldDuration"/>, tagged with
  /// the calling method name (captured at <see cref="AcquireAsync"/> via
  /// <c>[CallerMemberName]</c>). The default-constructed Releaser (degraded
  /// timeout path) does NOT record because nothing was held.
  /// </remarks>
  public readonly struct Releaser : IDisposable {
    private readonly SemaphoreSlim? _semaphore;
    private readonly Histogram<double>? _holdDurationHistogram;
    private readonly string? _caller;
    private readonly long _startTicks;

    internal Releaser(SemaphoreSlim semaphore, Histogram<double>? holdDurationHistogram, string? caller) {
      _semaphore = semaphore;
      _holdDurationHistogram = holdDurationHistogram;
      _caller = caller;
      _startTicks = Environment.TickCount64;
    }

    /// <inheritdoc />
    public void Dispose() {
      _semaphore?.Release();
      if (_holdDurationHistogram is not null && _semaphore is not null) {
        var elapsedMs = (double)(Environment.TickCount64 - _startTicks);
        _holdDurationHistogram.Record(elapsedMs,
          new KeyValuePair<string, object?>("caller", _caller ?? "<unknown>"));
      }
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    _semaphore?.Dispose();
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "WorkCoordinatorGate.AcquireAsync timed out after {TimeoutMilliseconds} ms (MaxConcurrent={MaxConcurrent}) — gate is saturated; this call proceeds WITHOUT holding a slot. Persistent saturation indicates pool pressure or callers leaking slots; investigate the gated call site.")]
  static partial void LogAcquireTimedOut(ILogger logger, int timeoutMilliseconds, int maxConcurrent);

  // v0.656 forensic Debug instrumentation: surface per-call gate decisions so slot-3
  // operators can see whether the silent two-minute spin is being absorbed by the gate's
  // WaitAsync (saturation) vs by something downstream of acquire.

  [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
    Message = "WorkCoordinatorGate.AcquireAsync entered — currentCount={CurrentCount}/{MaxConcurrent} timeoutMs={TimeoutMilliseconds}")]
  static partial void LogAcquireEntry(ILogger logger, int currentCount, int maxConcurrent, int timeoutMilliseconds);

  [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
    Message = "WorkCoordinatorGate.AcquireAsync GRANTED — remaining={CurrentCount}/{MaxConcurrent}")]
  static partial void LogAcquireGranted(ILogger logger, int currentCount, int maxConcurrent);

  [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
    Message = "WorkCoordinatorGate.AcquireAsync GRANTED (no deadline — opted out)")]
  static partial void LogAcquireGrantedNoDeadline(ILogger logger);

}
