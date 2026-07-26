using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Locks the v0.654 invariants around <see cref="WorkCoordinatorGate.AcquireAsync"/>.
/// A production forensic investigation motivated this: with the pre-v0.654 implementation, an
/// exhausted gate's <c>WaitAsync(ct)</c> would hang every caller forever — silently,
/// no exception, no log. Every gated coordinator call AND every <c>MoveAsync</c> in
/// the DLQ pre-publish gate routed through this single chokepoint. The deadline turns
/// silent-saturation into a Warning-level operational signal.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public class WorkCoordinatorGateTests {

  private sealed record _LogEntry(LogLevel Level, string Message);

  private sealed class _CapturingLogger<T> : ILogger<T> {
    public List<_LogEntry> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add(new _LogEntry(logLevel, formatter(state, exception)));
    }
    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  /// <summary>
  /// Happy path: a gate with available slots returns a real <see cref="WorkCoordinatorGate.Releaser"/>.
  /// Disposing it releases the slot so a subsequent call can re-acquire — the "FIFO acquire / dispose"
  /// contract every gated coordinator method relies on.
  /// </summary>
  [Test]
  public async Task AcquireAsync_HasCapacity_ReturnsReleaserThatReleasesOnDisposeAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, acquireTimeoutMilliseconds: 10_000);

    var releaser = await gate.AcquireAsync(CancellationToken.None);
    releaser.Dispose();

    // If the dispose didn't release the slot, this WaitAsync would throw TimeoutException
    // — that's the assertion. The acquire-and-dispose pair is the invariant.
    var second = await gate.AcquireAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    second.Dispose();
    await Assert.That(gate.MaxConcurrent).IsEqualTo(1)
      .Because("Reaching this line means both acquires completed within their deadlines. The MaxConcurrent assertion is a placeholder — the real assertion is the absence of a TimeoutException above. If the first dispose hadn't released the slot, the second acquire would have hung past 1s and the test would have failed there.");
  }

  /// <summary>
  /// v0.654 deadline path: when the semaphore is fully saturated and the deadline elapses,
  /// AcquireAsync returns a no-op Releaser AND logs a Warning. Caller proceeds without holding
  /// a slot — saturation is advisory, not fatal.
  /// </summary>
  [Test]
  public async Task AcquireAsync_SaturatedBeyondDeadline_ReturnsNoopReleaserAndLogsWarningAsync() {
    var logger = new _CapturingLogger<WorkCoordinatorGate>();
    using var gate = new WorkCoordinatorGate(
      maxConcurrent: 1,
      acquireTimeoutMilliseconds: 100,
      logger: logger);

    var holding = await gate.AcquireAsync(CancellationToken.None);
    try {
      var deadlined = await gate.AcquireAsync(CancellationToken.None);
      deadlined.Dispose();

      var secondDeadlined = await gate.AcquireAsync(CancellationToken.None);
      secondDeadlined.Dispose();

      var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
      await Assert.That(warnings.Count).IsGreaterThanOrEqualTo(2)
        .Because("Each deadlined acquire produces exactly one Warning log entry; two deadlined calls produce two entries — operators can count them to spot saturation rate spikes. (v0.656 also emits Debug entry/grant/timeout breadcrumbs; the Warning invariant is what callers/operators key off of.)");
      await Assert.That(warnings[0].Message).Contains("WorkCoordinatorGate")
        .Because("Log must identify the gate by name so operators reading mixed Whizbang logs can correlate the saturation back to the chokepoint.");
      await Assert.That(warnings[0].Message).Contains("100")
        .Because("Including the configured deadline lets operators tell at a glance whether the deadline is the problem (too short for this load) vs whether there's a pool issue.");
    } finally {
      holding.Dispose();
    }
  }

  /// <summary>
  /// Opt-out: passing a non-positive deadline preserves the pre-v0.654 indefinite-wait behavior.
  /// Backward-compat escape hatch.
  /// </summary>
  [Test]
  public async Task AcquireAsync_TimeoutDisabled_BlocksUntilSlotAvailableAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, acquireTimeoutMilliseconds: 0);

    var holding = await gate.AcquireAsync(CancellationToken.None);

    var pending = gate.AcquireAsync(CancellationToken.None).AsTask();

    var didCompleteEarly = await Task.WhenAny(pending, Task.Delay(200)) == pending;
    await Assert.That(didCompleteEarly).IsFalse()
      .Because("acquireTimeoutMilliseconds=0 must wait indefinitely — proves the opt-out preserves original behavior.");

    holding.Dispose();
    var unblocked = await pending.WaitAsync(TimeSpan.FromSeconds(1));
    unblocked.Dispose();
  }

  /// <summary>
  /// Disabled gate (maxConcurrent <= 0): every call returns default Releaser without
  /// touching a semaphore. No deadline, no logging, no contention.
  /// </summary>
  [Test]
  public async Task AcquireAsync_DisabledGate_AlwaysReturnsDefaultReleaserAsync() {
    var logger = new _CapturingLogger<WorkCoordinatorGate>();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 0, logger: logger);

    var releasers = new List<WorkCoordinatorGate.Releaser>();
    for (var i = 0; i < 100; i++) {
      releasers.Add(await gate.AcquireAsync(CancellationToken.None));
    }
    foreach (var r in releasers) {
      r.Dispose();
    }

    await Assert.That(logger.Entries).IsEmpty()
      .Because("A disabled gate must not log — there's no semaphore to time out on.");
  }

  /// <summary>
  /// Properties reflect constructor args verbatim. Lets operator diagnostics inspect
  /// the configured gate at runtime.
  /// </summary>
  [Test]
  public async Task Constructor_PropertiesReflectArgumentsAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 17, acquireTimeoutMilliseconds: 42_000);
    await Assert.That(gate.MaxConcurrent).IsEqualTo(17);
    await Assert.That(gate.AcquireTimeoutMilliseconds).IsEqualTo(42_000);
  }

  /// <summary>
  /// v0.660 Slice 7 — gate hold-duration histogram. Records the time between
  /// acquire and dispose so operators can see which gated code paths are
  /// holding slots longest. A production forensic investigation showed gate held at 49-50/50 with
  /// the consumer's service CPU at 20% — gate slots are being held during application work, not
  /// just DB I/O. The histogram makes that visible per-caller.
  /// </summary>
  [Test]
  public async Task AcquireAsync_OnDispose_RecordsHistogramObservationAsync() {
    var metrics = new Whizbang.Core.Observability.WorkCoordinatorMetrics(
      new Whizbang.Core.Observability.WhizbangMetrics(meterFactory: null));
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, metrics: metrics);

    var observed = new List<KeyValuePair<string, object?>[]>();
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      // Filter by instrument REFERENCE, not name — other tests run in parallel,
      // creating their own WorkCoordinatorMetrics instances on Meters that share
      // the "Whizbang.WorkCoordinator" name. Name-based matching would catch
      // their measurements and inflate our counts.
      if (ReferenceEquals(instrument, metrics.GateHoldDuration)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<double>((_, _, tags, _) => {
      observed.Add(tags.ToArray());
    });
    listener.Start();

    using (await gate.AcquireAsync(CancellationToken.None)) {
      // gate held — Dispose triggers the histogram record
    }

    listener.Dispose();
    await Assert.That(observed.Count).IsEqualTo(1)
      .Because("Each completed acquire/dispose pair MUST emit exactly one histogram observation — operators count these to compute gate throughput and p95 hold latency.");
    var callerTag = observed[0].FirstOrDefault(kv => kv.Key == "caller");
    await Assert.That((string?)callerTag.Value).IsNotNull()
      .Because("Observations MUST be tagged with the calling method name so dashboards can group by caller (ClaimWorkAsync vs StoreOutboxMessagesAsync vs FetchOutboxBatchAsync).");
  }

  /// <summary>
  /// v0.660 Slice 1 — gate cap derived from connection-pool size. The gate's
  /// job is to bound concurrent <c>IWorkCoordinator</c> calls, NOT to bound
  /// the absolute number of DB connections. Each slot maps to at most one
  /// connection from the per-pod Npgsql pool, so the gate's *effective*
  /// ceiling is <c>min(MaxConcurrent, MaxPoolSize)</c>. Defaulting the gate
  /// to <c>MaxPoolSize - reserve</c> resolves to the env-tuned pool ceiling
  /// automatically — no separate magic number to drift.
  /// </summary>
  [Test]
  public async Task FromPoolSize_DerivesMaxConcurrentAsPoolMinusReserveAsync() {
    using var gate = WorkCoordinatorGate.FromPoolSize(maxPoolSize: 30, reserve: 5);
    await Assert.That(gate.MaxConcurrent).IsEqualTo(25)
      .Because("Pool size 30 minus reserve 5 = 25. The reserve protects non-gated DB work (LISTEN connections, health-check pings, manual queries) from being starved by gated callers saturating the pool.");
  }

  /// <summary>
  /// Pool tiny / reserve oversized: clamp to a minimum of 1 so the gate
  /// remains operative. A 0-or-negative cap would silently disable the gate
  /// (per the <c>maxConcurrent &lt;= 0</c> branch in the main constructor)
  /// which is NOT what FromPoolSize means — it means "match pool, minus
  /// reserve" with at least one slot available.
  /// </summary>
  [Test]
  public async Task FromPoolSize_TinyPool_ClampsToMinimumOneAsync() {
    using var gate = WorkCoordinatorGate.FromPoolSize(maxPoolSize: 3, reserve: 5);
    await Assert.That(gate.MaxConcurrent).IsEqualTo(1)
      .Because("(3 - 5) = -2 would disable the gate. Floor at 1 instead: a slow pipeline is recoverable; a silently-disabled gate is the production stuck-row class of bug the v0.654 hardening was supposed to prevent.");
  }

  /// <summary>
  /// Default reserve is 5 — chosen to leave headroom for the LISTEN
  /// connection (1), maintenance queries (1-2), and ad-hoc operator work
  /// (1-2). Tests below verify the parameter is wired all the way through.
  /// </summary>
  [Test]
  public async Task FromPoolSize_DefaultReserveIsFiveAsync() {
    using var gate = WorkCoordinatorGate.FromPoolSize(maxPoolSize: 50);
    await Assert.That(gate.MaxConcurrent).IsEqualTo(45)
      .Because("Default reserve must be 5 — documented in the connection-budget framing in plans/throughput-optimization.md. If this changes, the connection-pool-sizing doc must be updated in lockstep.");
  }

  /// <summary>
  /// v0.659.1-alpha.15 regression — the default DI registration in
  /// <c>WorkerPipelineExtensions.AddWhizbangWorkers</c> MUST resolve
  /// <see cref="Whizbang.Core.Observability.WorkCoordinatorMetrics"/> from the
  /// container and pass it to the gate, so the histogram from
  /// <see cref="AcquireAsync_OnDispose_RecordsHistogramObservationAsync"/>
  /// actually records under production wiring. Slice 7 originally landed only
  /// the class-level histogram contract; the DI factory was left passing
  /// <c>logger</c> alone, so several minutes of production traffic produced zero
  /// <c>whizbang.gate.hold_duration_ms</c> observations in App Insights —
  /// proving the wiring gap. This test locks the wiring so any future
  /// refactor of the factory that drops the metrics arg fails here.
  /// </summary>
  [Test]
  public async Task AddWhizbangWorkers_GateRegistration_PassesMetricsAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    // AddWhizbang registers WorkCoordinatorMetrics; AddWhizbangWorkers registers
    // the gate. Both are required for the gate's DI factory to receive the metrics.
    services.AddWhizbang();
    services.AddWhizbangWorkers();
    using var provider = services.BuildServiceProvider();
    var metrics = provider.GetRequiredService<Whizbang.Core.Observability.WorkCoordinatorMetrics>();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    var observed = new List<KeyValuePair<string, object?>[]>();
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.GateHoldDuration)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<double>((_, _, tags, _) => observed.Add(tags.ToArray()));
    listener.Start();

    using (await gate.AcquireAsync(CancellationToken.None)) {
      // held — Dispose triggers histogram record, IF wiring is correct
    }

    listener.Dispose();
    await Assert.That(observed.Count).IsEqualTo(1)
      .Because("The DI factory at WorkerPipelineExtensions.cs:184 MUST pass `metrics: sp.GetService<WorkCoordinatorMetrics>()` so the singleton gate carries the meter. Without it, every acquire/dispose pair records nothing — the production 0-observation forensic that caught this gap.");
  }

  /// <summary>
  /// The default-Releaser path (degraded after timeout-induced no-op) MUST NOT
  /// emit a histogram observation — nothing was held, so timing it is noise
  /// that would distort p95.
  /// </summary>
  [Test]
  public async Task AcquireAsync_DegradedNoopReleaser_NoHistogramObservationAsync() {
    var metrics = new Whizbang.Core.Observability.WorkCoordinatorMetrics(
      new Whizbang.Core.Observability.WhizbangMetrics(meterFactory: null));
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, acquireTimeoutMilliseconds: 100, metrics: metrics);

    var observed = new List<KeyValuePair<string, object?>[]>();
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      // Filter by instrument REFERENCE, not name — other tests run in parallel,
      // creating their own WorkCoordinatorMetrics instances on Meters that share
      // the "Whizbang.WorkCoordinator" name. Name-based matching would catch
      // their measurements and inflate our counts.
      if (ReferenceEquals(instrument, metrics.GateHoldDuration)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<double>((_, _, tags, _) => {
      observed.Add(tags.ToArray());
    });
    listener.Start();

    var holding = await gate.AcquireAsync(CancellationToken.None);  // 1 observation when this disposes
    var degraded = await gate.AcquireAsync(CancellationToken.None);  // deadlines after 100ms; returns default
    degraded.Dispose();
    holding.Dispose();
    listener.Dispose();

    await Assert.That(observed.Count).IsEqualTo(1)
      .Because("Only the actually-held acquire records a hold duration. The degraded default Releaser disposes without recording because nothing was held; including it would distort histograms with bogus near-zero measurements.");
  }
}
