using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;
using Whizbang.Core.Temporal;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Unit tests for <see cref="ScheduleWorker"/> — the drain loop resolves an <see cref="IScheduleClaimer"/>
/// per pass and claims in batches until a claim returns fewer than the limit, no-ops when no claimer is
/// registered, and wakes early when a <see cref="ScheduleDueSignal"/> arrives on the bus.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public class ScheduleWorkerTests {
  private sealed class FakeClaimer : IScheduleClaimer {
    private readonly Queue<int> _returns;
    public int Calls { get; private set; }
    public DateTimeOffset? NextFireTime { get; set; }
    public FakeClaimer(params int[] returns) => _returns = new Queue<int>(returns);
    public Task<int> ClaimDueSchedulesAsync(int limit, CancellationToken cancellationToken = default) {
      Calls++;
      return Task.FromResult(_returns.Count > 0 ? _returns.Dequeue() : 0);
    }
    public Task<DateTimeOffset?> GetNextFireTimeAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(NextFireTime);
  }

  private static (ScheduleWorker Worker, IScheduleClaimer? Claimer) _create(
      TemporalOptions? options = null, ISignalBus? bus = null, IScheduleClaimer? claimer = null, TimeProvider? clock = null,
      ISchemaReadyGate? gate = null, Microsoft.Extensions.Logging.ILogger<ScheduleWorker>? logger = null) {
    var services = new ServiceCollection();
    if (claimer is not null) {
      services.AddSingleton(claimer);
    }
    var provider = services.BuildServiceProvider();
    var worker = new ScheduleWorker(
      provider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(options ?? new TemporalOptions()),
      logger ?? NullLogger<ScheduleWorker>.Instance,
      schemaReadyGate: gate ?? Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      signalBus: bus,
      timeProvider: clock);
    return (worker, claimer);
  }

  /// <summary>
  /// Claimer that signals each claim, so the ExecuteAsync tests can await the drain loop actually
  /// reaching a claim rather than sleeping/polling for it.
  /// </summary>
  private sealed class SignallingClaimer : IScheduleClaimer {
    private readonly TaskCompletionSource _firstClaim = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondClaim = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    /// <summary>When set, the claim throws — exercising the loop's error-swallow branch.</summary>
    public bool Throw { get; set; }

    /// <summary>When set, the claim hangs until the worker is cancelled.</summary>
    public bool BlockUntilCancelled { get; set; }
    public int Calls => Volatile.Read(ref _calls);
    public Task FirstClaim => _firstClaim.Task;
    public Task SecondClaim => _secondClaim.Task;

    public async Task<int> ClaimDueSchedulesAsync(int limit, CancellationToken cancellationToken = default) {
      var n = Interlocked.Increment(ref _calls);
      if (n == 1) {
        _firstClaim.TrySetResult();
      } else if (n == 2) {
        _secondClaim.TrySetResult();
      }
      if (BlockUntilCancelled) {
        // Must propagate the cancellation rather than absorb it: the loop distinguishes a
        // cancelled claim from a failed one, and a claim that returned normally on shutdown
        // would exercise neither branch.
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
      }
      if (Throw) {
        throw new InvalidOperationException("claim blew up");
      }
      return 0;
    }

    public Task<DateTimeOffset?> GetNextFireTimeAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<DateTimeOffset?>(null);
  }

  /// <summary>Schema gate that stays closed until <see cref="Open"/> — models a host still migrating.</summary>
  private sealed class ManualSchemaGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _waitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the worker has actually reached the gate and started waiting.</summary>
    public Task WaitEntered => _waitEntered.Task;

    public bool IsReady => _ready.Task.IsCompleted;
    public void Open() => _ready.TrySetResult();
    public void MarkReady() => Open();
    public Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _waitEntered.TrySetResult();
      return _ready.Task.WaitAsync(cancellationToken);
    }
  }

  // ==================== ExecuteAsync — the BackgroundService drain loop ====================

  [Test]
  [Timeout(30000)]
  public async Task Execute_Disabled_ParksWithoutEverClaimingAsync(CancellationToken ct) {
    var claimer = new SignallingClaimer();
    var log = new _recordingLogger();
    var (worker, _) = _create(new TemporalOptions { Enabled = false }, claimer: claimer, logger: log);

    await worker.StartAsync(CancellationToken.None);
    // The host starts ExecuteAsync on the thread pool, so without waiting for the worker to reach
    // its disabled branch the claim count below is zero for the wrong reason -- a loop that has
    // not begun has also never claimed.
    await log.Disabled.WaitAsync(ct);

    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("a disabled worker parks on its stopping token; returning immediately reads to the "
             + "host as a BackgroundService that crashed");
    await Assert.That(claimer.Calls).IsEqualTo(0)
      .Because("Enabled=false must park the loop entirely — a non-temporal host must never claim schedules");

    await worker.StopAsync(CancellationToken.None);
    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("the parked delay absorbs the shutdown rather than surfacing it as a fault");
  }

  [Test]
  public async Task Execute_SchemaGateClosed_DoesNotClaimUntilReadyAsync() {
    var claimer = new SignallingClaimer();
    var gate = new ManualSchemaGate();
    var (worker, _) = _create(claimer: claimer, gate: gate);

    await worker.StartAsync(CancellationToken.None);
    await gate.WaitEntered.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(claimer.Calls).IsEqualTo(0)
      .Because("claiming before the schema exists would hit missing tables — the gate must hold the loop");

    gate.Open();
    await claimer.FirstClaim.WaitAsync(TimeSpan.FromSeconds(10));   // gate released → loop drains

    await Assert.That(claimer.Calls).IsGreaterThanOrEqualTo(1);
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(30000)]
  public async Task Execute_SchemaGateNeverOpens_CancellationExitsWithoutClaimingAsync(
      CancellationToken ct) {
    var claimer = new SignallingClaimer();
    var gate = new ManualSchemaGate();
    var (worker, _) = _create(claimer: claimer, gate: gate);

    await worker.StartAsync(CancellationToken.None);
    // Observed at the gate first: otherwise "never claimed" is satisfied by a worker that never ran.
    await gate.WaitEntered.WaitAsync(ct);
    await worker.StopAsync(CancellationToken.None);   // cancels while still waiting on the gate

    await Assert.That(claimer.Calls).IsEqualTo(0)
      .Because("shutdown while gated must return cleanly, not claim and not hang");
    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("a host stopped mid-migration is an ordinary shutdown, not a worker crash");
  }

  [Test]
  [Timeout(30000)]
  public async Task Execute_CancellationDuringAClaim_EndsTheLoopAsync(CancellationToken ct) {
    // Shutdown lands while a claim is in flight, which is where the drain spends its time. The
    // loop must treat that as a stop, not as a failed pass -- otherwise every deploy logs a drain
    // failure and re-arms, and the log stops distinguishing a real claim fault from a rollout.
    var claimer = new SignallingClaimer { BlockUntilCancelled = true };
    var log = new _recordingLogger();
    var (worker, _) = _create(claimer: claimer, logger: log);

    await worker.StartAsync(CancellationToken.None);
    await claimer.FirstClaim.WaitAsync(ct);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("cancellation mid-claim breaks the loop and the worker stops cleanly");
    await Assert.That(log.Events).DoesNotContain(TICK_FAILED_EVENT_ID)
      .Because("a shutdown is not a failed drain pass, and logging it as one on every deploy "
             + "teaches operators that drain warnings are routine");
  }

  [Test]
  public async Task Execute_DoorbellSignal_WakesDrainBeforeBackstopAsync() {
    var claimer = new SignallingClaimer();
    // A backstop far longer than the test: any second claim can only come from the doorbell, never
    // from the interval elapsing — that is what makes this a real assertion about the fast path.
    var (worker, _) = _create(new TemporalOptions { BackstopIntervalMilliseconds = 600_000 }, claimer: claimer);

    await worker.StartAsync(CancellationToken.None);
    await claimer.FirstClaim.WaitAsync(TimeSpan.FromSeconds(10));   // startup pass

    worker.RequestImmediateRun();                                   // ring the doorbell
    await claimer.SecondClaim.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(claimer.Calls).IsGreaterThanOrEqualTo(2)
      .Because("a ScheduleDueSignal doorbell must wake the drain immediately, not wait out the backstop");
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Execute_ClaimThrows_SwallowsAndKeepsLoopAliveAsync() {
    var claimer = new SignallingClaimer { Throw = true };
    var (worker, _) = _create(new TemporalOptions { BackstopIntervalMilliseconds = 600_000 }, claimer: claimer);

    await worker.StartAsync(CancellationToken.None);
    await claimer.FirstClaim.WaitAsync(TimeSpan.FromSeconds(10));

    worker.RequestImmediateRun();
    await claimer.SecondClaim.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(claimer.Calls).IsGreaterThanOrEqualTo(2)
      .Because("a failed drain pass must be logged and retried on the next tick — one bad claim "
        + "(a transient DB blip) must never kill the engine and silently stop every schedule");
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Execute_DoorbellArrivesViaSignalBus_WakesDrainAsync() {
    var bus = new SignalBus([]);
    var claimer = new SignallingClaimer();
    var (worker, _) = _create(new TemporalOptions { BackstopIntervalMilliseconds = 600_000 }, bus: bus, claimer: claimer);

    await worker.StartAsync(CancellationToken.None);
    await claimer.FirstClaim.WaitAsync(TimeSpan.FromSeconds(10));   // startup pass

    // The real doorbell path, end to end: a ScheduleDueSignal arriving on the bus (as the NOTIFY
    // transport / poll-source backstop delivers it) must wake the drain, not wait out the backstop.
    await ((ISignalSink)bus).ReceiveAsync(new ScheduleDueSignal());
    await claimer.SecondClaim.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(claimer.Calls).IsGreaterThanOrEqualTo(2);
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Dispose_IsIdempotentAsync() {
    var (worker, _) = _create(bus: new SignalBus([]), claimer: new FakeClaimer());

    worker.Dispose();
    worker.Dispose();   // double dispose must not throw (unsubscribes + disposes timer/semaphore once)

    await Assert.That(worker.TimerForTests).IsNotNull();
  }

  [Test]
  public async Task TickOnce_NoClaimer_ReturnsZeroAsync() {
    var (worker, _) = _create();
    await Assert.That(await worker.TickOnceAsync()).IsEqualTo(0);
  }

  [Test]
  public async Task TickOnce_StopsWhenBelowBatchLimitAsync() {
    var claimer = new FakeClaimer(2);   // one short batch => drained after a single call
    var (worker, _) = _create(new TemporalOptions { ClaimBatchLimit = 100 }, claimer: claimer);

    var total = await worker.TickOnceAsync();

    await Assert.That(total).IsEqualTo(2);
    await Assert.That(claimer.Calls).IsEqualTo(1);
  }

  [Test]
  public async Task TickOnce_DrainsWhileBatchIsFullAsync() {
    // Two full batches (== limit) then a short one => three claim calls, then stop.
    var claimer = new FakeClaimer(10, 10, 4);
    var (worker, _) = _create(new TemporalOptions { ClaimBatchLimit = 10 }, claimer: claimer);

    var total = await worker.TickOnceAsync();

    await Assert.That(total).IsEqualTo(24);
    await Assert.That(claimer.Calls).IsEqualTo(3);
  }

  [Test]
  public async Task RequestImmediateRun_ReleasesWakeAsync() {
    var (worker, _) = _create();

    worker.RequestImmediateRun();

    await Assert.That(worker.TryConsumeWakeForTests()).IsTrue();
    await Assert.That(worker.TryConsumeWakeForTests()).IsFalse();   // consumed
  }

  [Test]
  public async Task RequestImmediateRun_CoalescesMultipleWakesAsync() {
    var (worker, _) = _create();

    worker.RequestImmediateRun();
    worker.RequestImmediateRun();   // second is coalesced (semaphore capacity 1)

    await Assert.That(worker.TryConsumeWakeForTests()).IsTrue();
    await Assert.That(worker.TryConsumeWakeForTests()).IsFalse();
  }

  [Test]
  public async Task ScheduleDueSignal_Received_WakesWorkerAsync() {
    var bus = new SignalBus([]);
    var (worker, _) = _create(bus: bus);   // ctor subscribes to ScheduleDueSignal

    await ((ISignalSink)bus).ReceiveAsync(new ScheduleDueSignal());

    await Assert.That(worker.TryConsumeWakeForTests()).IsTrue();
  }

  [Test]
  public async Task ArmTimerOnce_ArmsToClaimerNextTimeAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var next = clock.GetUtcNow().AddSeconds(30);
    var claimer = new FakeClaimer { NextFireTime = next };
    var (worker, _) = _create(claimer: claimer, clock: clock);

    await worker.ArmTimerOnceAsync();

    await Assert.That(worker.TimerForTests.ArmedFor).IsEqualTo(next);
  }

  [Test]
  public async Task ArmTimerOnce_NoClaimer_LeavesTimerDisarmedAsync() {
    var (worker, _) = _create();   // no claimer registered

    await worker.ArmTimerOnceAsync();

    await Assert.That(worker.TimerForTests.ArmedFor).IsNull();
  }

  [Test]
  public async Task Timer_FiresAtNextFireTime_WakesWorkerAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var claimer = new FakeClaimer { NextFireTime = clock.GetUtcNow().AddSeconds(30) };
    var (worker, _) = _create(claimer: claimer, clock: clock);
    await worker.ArmTimerOnceAsync();

    clock.Advance(TimeSpan.FromSeconds(29));
    await Assert.That(worker.TryConsumeWakeForTests()).IsFalse();   // not yet due

    clock.Advance(TimeSpan.FromSeconds(1));                          // now at the fire time
    await Assert.That(worker.TryConsumeWakeForTests()).IsTrue();     // timer rang the doorbell
    await Assert.That(worker.TimerForTests.WakeCount).IsEqualTo(1L);
  }

  private const int DISABLED_EVENT_ID = 2;
  private const int TICK_FAILED_EVENT_ID = 3;

  /// <summary>Records the worker's log events, and reports when it announced it was disabled.</summary>
  private sealed class _recordingLogger : Microsoft.Extensions.Logging.ILogger<ScheduleWorker> {
    private readonly List<int> _events = [];
    private readonly TaskCompletionSource _disabled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Event ids seen so far, which is how a test tells one loop exit from another.</summary>
    public IReadOnlyList<int> Events { get { lock (_events) { return [.. _events]; } } }

    /// <summary>Completes once the worker has announced it is disabled and is parking.</summary>
    public Task Disabled => _disabled.Task;

    IDisposable? Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_events) { _events.Add(eventId.Id); }
      if (eventId.Id == DISABLED_EVENT_ID) {
        _disabled.TrySetResult();
      }
    }
  }
}
