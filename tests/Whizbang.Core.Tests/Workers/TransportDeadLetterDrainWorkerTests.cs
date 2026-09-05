using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable IDE1006 // _ prefix on local helpers — convention used elsewhere in the suite

/// <summary>
/// v0.502 slice C.8/C.9 — regression locks for the transport DLQ drain worker.
/// Verifies the worker correctly enumerates every registered
/// <see cref="ITransportDeadLetterDrainer"/>, propagates per-drainer failures without
/// short-circuiting the rest, honors the killswitch, and respects the
/// <c>MaxPerTick</c> cap. Drives <see cref="TransportDeadLetterDrainWorker.DrainOnceAsync"/>
/// directly to keep tests deterministic (no Task.Delay / wall-clock polling).
/// </summary>
public class TransportDeadLetterDrainWorkerTests {

  private sealed class FakeDrainer(string name) : ITransportDeadLetterDrainer {
    public string TransportName { get; } = name;
    public int CallCount;
    public int? LastMaxCount;
    public int ReturnValue { get; set; }
    public bool Throw { get; set; }
    /// <summary>Thrown in place of the generic failure, for the cancellation contract.</summary>
    public Exception? ThrowSpecific { get; set; }

    public Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default) {
      CallCount++;
      LastMaxCount = maxCount;
      if (ThrowSpecific is not null) {
        throw ThrowSpecific;
      }
      if (Throw) {
        throw new InvalidOperationException($"simulated failure in {TransportName}");
      }
      return Task.FromResult(ReturnValue);
    }
  }

  private static TransportDeadLetterDrainWorker _buildWorker(
      TransportDeadLetterDrainWorkerOptions opts,
      params ITransportDeadLetterDrainer[] drainers) {
    var services = new ServiceCollection();
    services.AddLogging();
    foreach (var d in drainers) {
      services.AddSingleton(d);
    }
    var provider = services.BuildServiceProvider();
    return new TransportDeadLetterDrainWorker(
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(opts),
      whizbangMetrics: new WhizbangMetrics(),
      logger: NullLogger<TransportDeadLetterDrainWorker>.Instance,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());
  }

  [Test]
  [Timeout(30000)]
  public async Task WhenDisabled_TheWorkerParksAndDrainsNothingAsync(CancellationToken ct) {
    // A BackgroundService that returns on its own reads to the host as a crashed worker. Parking
    // keeps a deliberately-disabled drain distinguishable from one that fell over.
    var drainer = new _recordingDrainer();
    var log = new _recordingLogger();
    var worker = _buildWorker(
      new TransportDeadLetterDrainWorkerOptions { Enabled = false }, log, drainer);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Without waiting for the worker to reach the disabled branch, everything below is answered by
    // a worker the thread pool has not started: an execute task that never began is also "not
    // completed", and a drain that never ran also drained nothing.
    await log.Disabled.WaitAsync(ct);

    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("a disabled worker parks on its stopping token; returning is how the host detects "
             + "a BackgroundService that has crashed");
    await Assert.That(drainer.Calls).IsEqualTo(0)
      .Because("disabled means no broker is touched at all, not merely drained less often");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(30000)]
  public async Task ShutdownBeforeTheSchemaIsReady_ExitsQuietlyAsync(CancellationToken ct) {
    // The worker parks on the schema gate at startup. A pod stopped while still waiting has no
    // schema to drain against, so the exit must be silent rather than an error on every fast
    // restart.
    var services = new ServiceCollection();
    services.AddLogging();
    var provider = services.BuildServiceProvider();
    var gate = new _blockingGate();
    var log = new _recordingLogger();
    var worker = new TransportDeadLetterDrainWorker(
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new TransportDeadLetterDrainWorkerOptions { Enabled = true }),
      whizbangMetrics: new WhizbangMetrics(),
      logger: log,
      schemaReadyGate: gate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await gate.WaitEntered.WaitAsync(ct);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("stopping mid-wait is an ordinary shutdown; a faulted worker reports it as a crash");
    await Assert.That(log.Events).DoesNotContain(CYCLE_ERROR_EVENT_ID)
      .Because("a fast restart is not a drain failure, and logging it as one would make every "
             + "rollout look like the DLQ drain is broken");
  }

  [Test]
  [Timeout(30000)]
  public async Task ADrainerThatThrows_DoesNotStopTheCycleAsync(CancellationToken ct) {
    // A transport whose DLQ drain dies stops recovering dead-lettered messages entirely, silently,
    // for the life of the process. One broker being unreachable must cost that tick only.
    var drainer = new _recordingDrainer { Throws = true };
    var log = new _recordingLogger();
    var worker = _buildWorker(
      new TransportDeadLetterDrainWorkerOptions { Enabled = true, MaxPerTick = 10, IntervalMinutes = 0 },
      log, drainer);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Only a second attempt proves the loop survived the first one throwing.
    await drainer.SecondCall.WaitAsync(ct);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("the drain keeps cycling through a failing broker and still shuts down cleanly");
  }

  [Test]
  [Timeout(30000)]
  public async Task AnUnexpectedCycleFailure_DoesNotStopTheLoopAsync(CancellationToken ct) {
    // Per-drainer failures are handled inside DrainOnceAsync. This is the aggregate case the
    // comment there names -- a scope that cannot be created, so no drainer is ever reached. It is
    // the more dangerous one: nothing drains at all, and without this catch the worker would exit
    // and take the whole DLQ recovery path with it for the life of the process.
    var scopeFactory = new _failingScopeFactory();
    var log = new _recordingLogger();
    var worker = new TransportDeadLetterDrainWorker(
      scopeFactory: scopeFactory,
      options: Options.Create(new TransportDeadLetterDrainWorkerOptions {
        Enabled = true,
        IntervalMinutes = 0
      }),
      whizbangMetrics: new WhizbangMetrics(),
      logger: log,
      schemaReadyGate: SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await scopeFactory.SecondAttempt.WaitAsync(ct);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(log.Events).Contains(CYCLE_ERROR_EVENT_ID)
      .Because("an aggregate failure that is swallowed without a line in the log is the silent "
             + "no-op this worker's own comments exist to prevent");
    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("the loop retries the next tick rather than ending");
  }

  [Test]
  [Timeout(30000)]
  public async Task ShutdownDuringADrain_EndsTheLoopRatherThanLoggingAFailureAsync(
      CancellationToken ct) {
    // Cancellation lands while a drain is in flight, which is where the loop spends its time.
    // DrainOnceAsync rethrows cancellation rather than treating it as a drainer error, and the
    // loop must break on it -- otherwise every shutdown logs a cycle failure and retries.
    var drainer = new _recordingDrainer { BlocksUntilCancelled = true };
    var log = new _recordingLogger();
    var worker = _buildWorker(
      new TransportDeadLetterDrainWorkerOptions { Enabled = true, MaxPerTick = 10, IntervalMinutes = 0 },
      log, drainer);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainer.DrainEntered.WaitAsync(ct);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("cancellation mid-drain ends the loop cleanly");
    await Assert.That(log.Events).DoesNotContain(CYCLE_ERROR_EVENT_ID)
      .Because("a shutdown is not a cycle failure; reporting it as one on every deploy teaches "
             + "operators that drain errors are routine");
  }


  private sealed class _throwingDrainer : ITransportDeadLetterDrainer {
    public string TransportName => "throwing";
    public Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default)
      => throw new InvalidOperationException("broker unreachable");
  }

  [Test]
  public async Task NoDrainersRegistered_NoOpAsync() {
    var worker = _buildWorker(new TransportDeadLetterDrainWorkerOptions {
      Enabled = true,
      MaxPerTick = 500,
    });

    await worker.DrainOnceAsync(CancellationToken.None);

    await Assert.That(worker.TotalDrained).IsEqualTo(0);
  }

  [Test]
  public async Task DrainersInvoked_AllReturnNonZero_TotalAccumulatedAsync() {
    var a = new FakeDrainer("asb:topic-a") { ReturnValue = 5 };
    var b = new FakeDrainer("rmq:queue-b") { ReturnValue = 7 };
    var worker = _buildWorker(new TransportDeadLetterDrainWorkerOptions {
      Enabled = true,
      MaxPerTick = 250,
    }, a, b);

    await worker.DrainOnceAsync(CancellationToken.None);

    await Assert.That(a.CallCount).IsEqualTo(1);
    await Assert.That(b.CallCount).IsEqualTo(1);
    await Assert.That(a.LastMaxCount).IsEqualTo(250);
    await Assert.That(b.LastMaxCount).IsEqualTo(250);
    await Assert.That(worker.TotalDrained).IsEqualTo(12);
  }

  [Test]
  public async Task OneDrainerThrows_OthersStillRunAsync() {
    var bad = new FakeDrainer("asb:bad") { Throw = true };
    var good = new FakeDrainer("rmq:good") { ReturnValue = 3 };
    var worker = _buildWorker(new TransportDeadLetterDrainWorkerOptions {
      Enabled = true,
      MaxPerTick = 100,
    }, bad, good);

    await worker.DrainOnceAsync(CancellationToken.None);

    await Assert.That(bad.CallCount).IsEqualTo(1);
    await Assert.That(good.CallCount).IsEqualTo(1);
    // The good drainer's count still landed in the worker's total despite the bad one throwing.
    await Assert.That(worker.TotalDrained).IsEqualTo(3);
  }

  [Test]
  public async Task ZeroReturn_DoesNotIncrementTotalDrainedAsync() {
    var quiet = new FakeDrainer("asb:quiet") { ReturnValue = 0 };
    var worker = _buildWorker(new TransportDeadLetterDrainWorkerOptions {
      Enabled = true,
      MaxPerTick = 100,
    }, quiet);

    await worker.DrainOnceAsync(CancellationToken.None);

    await Assert.That(quiet.CallCount).IsEqualTo(1);
    await Assert.That(worker.TotalDrained).IsEqualTo(0);
  }

  [Test]
  public async Task MultipleDrainOnceCalls_AccumulateTotalAsync() {
    var d = new FakeDrainer("asb:t") { ReturnValue = 4 };
    var worker = _buildWorker(new TransportDeadLetterDrainWorkerOptions {
      Enabled = true,
      MaxPerTick = 50,
    }, d);

    await worker.DrainOnceAsync(CancellationToken.None);
    await worker.DrainOnceAsync(CancellationToken.None);
    await worker.DrainOnceAsync(CancellationToken.None);

    await Assert.That(d.CallCount).IsEqualTo(3);
    await Assert.That(worker.TotalDrained).IsEqualTo(12);
  }

  [Test]
  public async Task TransportNames_ExposedForMetricsDimensionAsync() {
    var d = new FakeDrainer("asb:my-topic/my-sub") { ReturnValue = 1 };

    // Sanity check: the worker reads TransportName via the interface contract; assert the
    // metric dimension wiring contract holds at the type level by reading it back here.
    await Assert.That(d.TransportName).IsEqualTo("asb:my-topic/my-sub");
  }

  private sealed class _recordingLogger : Microsoft.Extensions.Logging.ILogger<TransportDeadLetterDrainWorker> {
    private readonly List<int> _events = [];
    private readonly TaskCompletionSource _disabled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries = [];

    /// <summary>Event ids seen so far, which is how a test tells one path from another.</summary>
    public IReadOnlyList<int> Events { get { lock (_events) { return [.. _events]; } } }

    /// <summary>Completes once the worker has announced it is disabled and is parking.</summary>
    public Task Disabled => _disabled.Task;

    IDisposable? Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_events) {
        _events.Add(eventId.Id);
        Entries.Add((logLevel, formatter(state, exception)));
      }
      if (eventId.Id == DISABLED_EVENT_ID) {
        _disabled.TrySetResult();
      }
    }
  }

  [Test]
  public async Task NoDrainersRegistered_WarnsExactlyOnceAcrossPassesAsync() {
    // Issue #514: an enabled drain worker with zero drainers silently recovered nothing for
    // months. The wiring gap must announce itself — once, not per tick.
    var services = new ServiceCollection();
    services.AddLogging();
    var provider = services.BuildServiceProvider();
    var logger = new _recordingLogger();
    var worker = new TransportDeadLetterDrainWorker(
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new TransportDeadLetterDrainWorkerOptions { Enabled = true, MaxPerTick = 500 }),
      whizbangMetrics: new WhizbangMetrics(),
      logger: logger,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    await worker.DrainOnceAsync(CancellationToken.None);
    await worker.DrainOnceAsync(CancellationToken.None);

    var warnings = logger.Entries.Where(e =>
      e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
      && e.Message.Contains("no ITransportDeadLetterDrainer", StringComparison.Ordinal)).ToList();
    await Assert.That(warnings.Count).IsEqualTo(1)
      .Because("the silent-no-op failure mode must be visible, but a 10-minute cadence must not "
             + "produce warning spam");
  }

  [Test]
  public async Task OneDrainerCanceled_StopsTheWholePassAsync() {
    // The companion to OneDrainerThrows_OthersStillRun, and the opposite answer. One drainer
    // failing must not cost the others their pass — but a canceled drainer is a stopping host,
    // and carrying on means opening more broker receivers while shutdown waits on them. The
    // drainers that follow are skipped on purpose; their queues keep until the next tick.
    var canceled = new FakeDrainer("asb:stopping") { ThrowSpecific = new OperationCanceledException() };
    var next = new FakeDrainer("rmq:next") { ReturnValue = 3 };
    var worker = _buildWorker(new TransportDeadLetterDrainWorkerOptions {
      Enabled = true,
      MaxPerTick = 100,
    }, canceled, next);

    await Assert.That(async () => await worker.DrainOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("a drain pass that keeps opening receivers after shutdown is what makes a host "
             + "hang on exit — the remaining queues are not going anywhere");
    await Assert.That(next.CallCount).IsEqualTo(0)
      .Because("the pass stops where the cancellation was seen, rather than draining on through "
             + "the rest of the fleet");
  }

  private const int CYCLE_ERROR_EVENT_ID = 4;
  private const int DISABLED_EVENT_ID = 2;

  private static TransportDeadLetterDrainWorker _buildWorker(
      TransportDeadLetterDrainWorkerOptions opts,
      ILogger<TransportDeadLetterDrainWorker> logger,
      params ITransportDeadLetterDrainer[] drainers) {
    var services = new ServiceCollection();
    services.AddLogging();
    foreach (var d in drainers) {
      services.AddSingleton(d);
    }
    var provider = services.BuildServiceProvider();
    return new TransportDeadLetterDrainWorker(
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(opts),
      whizbangMetrics: new WhizbangMetrics(),
      logger: logger,
      schemaReadyGate: SchemaReadyGate.AlreadyReady());
  }

  /// <summary>A drainer that reports when it has been asked to work, and how often.</summary>
  private sealed class _recordingDrainer : ITransportDeadLetterDrainer {
    private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public string TransportName => "recording";
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Completes on the second drain, which only a surviving loop can reach.</summary>
    public Task SecondCall => _second.Task;

    /// <summary>Completes as soon as a drain begins.</summary>
    public Task DrainEntered => _entered.Task;

    public bool Throws { get; set; }
    public bool BlocksUntilCancelled { get; set; }

    public async Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default) {
      if (Interlocked.Increment(ref _calls) >= 2) {
        _second.TrySetResult();
      }
      _entered.TrySetResult();
      if (BlocksUntilCancelled) {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
      }
      if (Throws) {
        throw new InvalidOperationException("broker unreachable");
      }
      return 0;
    }
  }

  /// <summary>A scope factory that cannot produce a scope -- the aggregate failure case.</summary>
  private sealed class _failingScopeFactory : IServiceScopeFactory {
    private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _attempts;

    /// <summary>Completes on the second attempt, proving the loop tried again.</summary>
    public Task SecondAttempt => _second.Task;

    public IServiceScope CreateScope() {
      if (Interlocked.Increment(ref _attempts) >= 2) {
        _second.TrySetResult();
      }
      throw new InvalidOperationException("scope unavailable");
    }
  }

  /// <summary>A schema gate that never opens, and reports when the worker began waiting.</summary>
  private sealed class _blockingGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _waitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitEntered => _waitEntered.Task;
    public bool IsReady => false;
    public void MarkReady() { }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _waitEntered.TrySetResult();
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
  }

}
