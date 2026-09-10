using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// v0.654 hardening: <see cref="AcquireAsync(CancellationToken)"/> now applies a
/// deadline to its internal <see cref="SemaphoreSlim.WaitAsync(int, CancellationToken)"/>
/// call. A production forensic investigation confirmed the cooperative-CT-only pattern (the v0.648 version
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
  // Priority step 4: the permits held back for interactive callers. Null when nothing is reserved.
  private readonly SemaphoreSlim? _reserve;
  private readonly ILogger<WorkCoordinatorGate> _logger;
  private readonly Histogram<double>? _holdDurationHistogram;

  /// <summary>Maximum concurrent calls. 0 disables the cap.</summary>
  public int MaxConcurrent { get; }

  /// <summary>
  /// Permits held back for interactive callers (priority step 4): a caller whose ambient parent
  /// (<see cref="Whizbang.Core.Priority.PriorityContext"/>) is in the interactive bucket may take one when the shared
  /// permits are gone; every other caller can never take the last reserved permits. One tenth of the permits,
  /// rounded down, unless configured (a gate under ten permits reserves nothing unless told to: a reserve that is
  /// half of a two-permit gate is a haircut, not a share); never the whole gate.
  /// </summary>
  /// <docs>fundamentals/messaging/message-priority#bulkheads</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorGateInteractiveReserveTests.cs</tests>
  public int InteractiveReserve { get; }

  /// <summary>The reserve for <paramref name="maxConcurrent"/> permits: the configured value, else one tenth rounded down, capped so at least one shared permit remains.</summary>
  internal static int ComputeInteractiveReserve(int maxConcurrent, int? configured) {
    if (maxConcurrent <= 1) {
      return 0;
    }
    var reserve = configured ?? maxConcurrent / 10;
    return Math.Clamp(reserve, 0, maxConcurrent - 1);
  }

  /// <summary>One held slot: the coordinator method that took it and how long it has held it.</summary>
  public readonly record struct GateHolder(string Caller, long HeldMs);

  private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (string Caller, long StartTicks)> _holders = new();
  private long _nextHolderId;

  /// <summary>
  /// The slots currently held, oldest first. A saturated gate against an idle database is the
  /// signature of a hold-and-wait; without this nothing in the process could say which coordinator
  /// methods held the slots.
  /// </summary>
  public IReadOnlyList<GateHolder> SnapshotHolders() {
    var now = Environment.TickCount64;
    return _holders.Values
      .Select(h => new GateHolder(h.Caller, now - h.StartTicks))
      .OrderByDescending(h => h.HeldMs)
      .ToList();
  }

  /// <summary>Holders grouped by caller with a count and the oldest age, for the deadline warning.</summary>
  private string _holdersSummary() {
    var groups = SnapshotHolders()
      .GroupBy(h => h.Caller)
      .Select(g => $"{g.Key} x{g.Count()} (oldest {g.Max(h => h.HeldMs) / 1000.0:0.#} s)")
      .Take(8);
    var text = string.Join(", ", groups);
    return text.Length == 0 ? "(none)" : text;
  }

  private Releaser _grant(string caller) => _grant(_semaphore!, caller);

  private Releaser _grant(SemaphoreSlim taken, string caller) {
    var id = Interlocked.Increment(ref _nextHolderId);
    _holders[id] = (caller, Environment.TickCount64);
    return new Releaser(taken, _holdDurationHistogram, caller, _holders, id);
  }

  /// <summary>Whether the current caller runs inside an interactive handling and may use the reserve.</summary>
  private static bool _isInteractiveCaller() =>
    Whizbang.Core.Priority.WorkPriority.Bucket(Whizbang.Core.Priority.PriorityContext.CurrentParent) == Whizbang.Core.Priority.WorkBucket.Interactive;

  /// <summary>
  /// An interactive caller's acquire (priority step 4): a shared permit when one is free, else a reserved permit
  /// when one is free, else whichever of the two frees first within the deadline. The shared permits go
  /// first so the reserve is whole whenever it is needed.
  /// </summary>
  private async ValueTask<Releaser> _acquireInteractiveAsync(string caller, CancellationToken cancellationToken) {
    if (await _semaphore!.WaitAsync(0, cancellationToken).ConfigureAwait(false)) {
      return _grant(_semaphore, caller);
    }
    if (await _reserve!.WaitAsync(0, cancellationToken).ConfigureAwait(false)) {
      return _grant(_reserve, caller);
    }
    var sharedWait = AcquireTimeoutMilliseconds <= 0
      ? _semaphore.WaitAsync(cancellationToken).ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }, TaskScheduler.Default)
      : _semaphore.WaitAsync(AcquireTimeoutMilliseconds, cancellationToken);
    var reserveWait = AcquireTimeoutMilliseconds <= 0
      ? _reserve.WaitAsync(cancellationToken).ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }, TaskScheduler.Default)
      : _reserve.WaitAsync(AcquireTimeoutMilliseconds, cancellationToken);
    var first = await Task.WhenAny(sharedWait, reserveWait).ConfigureAwait(false);
    var taken = first == sharedWait ? _semaphore : _reserve;
    var other = first == sharedWait ? reserveWait : sharedWait;
    var acquired = await first.ConfigureAwait(false);
    // The loser keeps waiting in the background; if it lands later, its permit is handed straight back.
    _ = other.ContinueWith(t => {
      if (t.Status == TaskStatus.RanToCompletion && t.Result) {
        (taken == _semaphore ? _reserve : _semaphore).Release();
      }
    }, TaskScheduler.Default);
    if (acquired) {
      return _grant(taken, caller);
    }
    if (_logger is not null) {
      LogAcquireTimedOut(_logger, AcquireTimeoutMilliseconds, MaxConcurrent, _holdersSummary());
    }
    return default;
  }

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
      WorkCoordinatorMetrics? metrics = null,
      int? interactiveReserve = null) {
    MaxConcurrent = maxConcurrent;
    InteractiveReserve = ComputeInteractiveReserve(maxConcurrent, interactiveReserve);
    AcquireTimeoutMilliseconds = acquireTimeoutMilliseconds;
    // The shared permits are what is left after the reserve; the reserve is its own semaphore so a
    // non-interactive caller can never take one of its permits, and a released reserved permit goes
    // back to the reserve, never to the shared pool.
    var shared = maxConcurrent - InteractiveReserve;
    _semaphore = maxConcurrent > 0 ? new SemaphoreSlim(shared, shared) : null;
    _reserve = InteractiveReserve > 0 ? new SemaphoreSlim(InteractiveReserve, InteractiveReserve) : null;
    _logger = logger ?? NullLogger<WorkCoordinatorGate>.Instance;
    _holdDurationHistogram = metrics?.GateHoldDuration;
  }

  /// <summary>
  /// Creates a gate whose <see cref="MaxConcurrent"/> defaults to
  /// <paramref name="maxPoolSize"/> minus <paramref name="reserve"/>, with a
  /// floor of 1.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The gate's effective ceiling on DB-touching work is already
  /// <c>min(MaxConcurrent, MaxPoolSize)</c> because each slot holds at most
  /// one connection from the per-pod Npgsql pool. Deriving the cap from the
  /// pool size eliminates a separate magic number that has to be re-tuned
  /// every time the pool is resized.
  /// </para>
  /// <para>
  /// <paramref name="reserve"/> (default 5) protects non-gated DB work — the
  /// LISTEN connection, maintenance queries, health-check pings, ad-hoc
  /// operator queries — from being starved when every gate slot is held
  /// against a tight pool. Tune up if you have heavier non-gated traffic;
  /// never let it drive the effective cap to ≤ 0 (the floor of 1 catches
  /// that, but a 1-slot gate is a serialized pipeline).
  /// </para>
  /// </remarks>
  public static WorkCoordinatorGate FromPoolSize(
      int maxPoolSize,
      int reserve = 5,
      int acquireTimeoutMilliseconds = 30000,
      ILogger<WorkCoordinatorGate>? logger = null,
      WorkCoordinatorMetrics? metrics = null) {
    var derived = Math.Max(1, maxPoolSize - reserve);
    return new WorkCoordinatorGate(
      maxConcurrent: derived,
      acquireTimeoutMilliseconds: acquireTimeoutMilliseconds,
      logger: logger,
      metrics: metrics);
  }

  /// <summary>
  /// Acquire a slot. Returns a disposable that releases on dispose.
  /// </summary>
  /// <remarks>
  /// When the gate has a configured deadline and the semaphore is saturated for longer
  /// than that deadline, the call returns a degraded <see cref="Releaser"/> that holds no
  /// slot — the caller proceeds, the saturation is logged at Warning, and the gate
  /// becomes advisory for that call rather than blocking. The alternative (the
  /// pre-v0.654 behavior) was to wait forever silently, which surfaced in production as the
  /// "stuck row that never DLQ-promotes" pattern.
  /// </remarks>
  /// <docs>fundamentals/workers/pinned-connection-pool</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorGatePinnedExemptionTests.cs</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorGateHolderDiagnosticsTests.cs</tests>
  public async ValueTask<Releaser> AcquireAsync(
      CancellationToken cancellationToken = default,
      [CallerMemberName] string caller = "<unknown>") {
    if (_semaphore is null) {
      return default;
    }
    // A pinned-pool borrow already caps concurrency at the pool size, so gating it double-counts; and
    // the workers that borrow one (claim, lease renewal, the completion and failure flushers) are the
    // ones that must never queue behind the un-pinned drain bodies holding the gate. While they did,
    // nothing completed, leases lapsed at their full length and the claim loop re-offered the same
    // rows: the gate -> pinned wire -> flush -> gate edge of the perspective hold-and-wait.
    if (Whizbang.Core.Workers.PinnedConnectionContext.Current is not null) {
      if (_logger is not null) {
        LogAcquireExemptPinned(_logger, caller);
      }
      return default;
    }
    var currentCount = _semaphore.CurrentCount;
    if (_logger is not null) {
      LogAcquireEntry(_logger, currentCount, MaxConcurrent, AcquireTimeoutMilliseconds);
    }
    if (_reserve is not null && _isInteractiveCaller()) {
      return await _acquireInteractiveAsync(caller, cancellationToken).ConfigureAwait(false);
    }
    if (AcquireTimeoutMilliseconds <= 0) {
      // Caller opted out of the deadline — preserve the pre-v0.654 behavior verbatim.
      await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
      if (_logger is not null) {
        LogAcquireGrantedNoDeadline(_logger);
      }
      return _grant(caller);
    }
    var acquired = await _semaphore
      .WaitAsync(AcquireTimeoutMilliseconds, cancellationToken)
      .ConfigureAwait(false);
    if (acquired) {
      if (_logger is not null) {
        LogAcquireGranted(_logger, _semaphore.CurrentCount, MaxConcurrent);
      }
      return _grant(caller);
    }
    if (_logger is not null) {
      LogAcquireTimedOut(_logger, AcquireTimeoutMilliseconds, MaxConcurrent, _holdersSummary());
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (string Caller, long StartTicks)>? _holders;
    private readonly long _holderId;

    internal Releaser(
        SemaphoreSlim semaphore, Histogram<double>? holdDurationHistogram, string? caller,
        System.Collections.Concurrent.ConcurrentDictionary<long, (string Caller, long StartTicks)>? holders = null, long holderId = 0) {
      _semaphore = semaphore;
      _holdDurationHistogram = holdDurationHistogram;
      _caller = caller;
      _startTicks = Environment.TickCount64;
      _holders = holders;
      _holderId = holderId;
    }

    /// <inheritdoc />
    public void Dispose() {
      _holders?.TryRemove(_holderId, out _);
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
    _reserve?.Dispose();
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "WorkCoordinatorGate.AcquireAsync timed out after {TimeoutMilliseconds} ms (MaxConcurrent={MaxConcurrent}) — gate is saturated; this call proceeds WITHOUT holding a slot. Persistent saturation indicates pool pressure or callers leaking slots; investigate the gated call site.. Holders: {Holders}")]
  static partial void LogAcquireTimedOut(ILogger logger, int timeoutMilliseconds, int maxConcurrent, string holders);

  [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
    Message = "WorkCoordinatorGate.AcquireAsync exempt: {Caller} runs on a pinned connection, which already bounds its concurrency; no slot taken")]
  static partial void LogAcquireExemptPinned(ILogger logger, string caller);

  // v0.656 forensic Debug instrumentation: surface per-call gate decisions so
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
