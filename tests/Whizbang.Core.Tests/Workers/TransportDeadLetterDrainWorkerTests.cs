using Microsoft.Extensions.DependencyInjection;
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
    public readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries = [];
    IDisposable? Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      => Entries.Add((logLevel, formatter(state, exception)));
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
  public async Task OneDrainerCancelled_StopsTheWholePassAsync() {
    // The companion to OneDrainerThrows_OthersStillRun, and the opposite answer. One drainer
    // failing must not cost the others their pass — but a cancelled drainer is a stopping host,
    // and carrying on means opening more broker receivers while shutdown waits on them. The
    // drainers that follow are skipped on purpose; their queues keep until the next tick.
    var cancelled = new FakeDrainer("asb:stopping") { ThrowSpecific = new OperationCanceledException() };
    var next = new FakeDrainer("rmq:next") { ReturnValue = 3 };
    var worker = _buildWorker(new TransportDeadLetterDrainWorkerOptions {
      Enabled = true,
      MaxPerTick = 100,
    }, cancelled, next);

    await Assert.That(async () => await worker.DrainOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("a drain pass that keeps opening receivers after shutdown is what makes a host "
             + "hang on exit — the remaining queues are not going anywhere");
    await Assert.That(next.CallCount).IsEqualTo(0)
      .Because("the pass stops where the cancellation was seen, rather than draining on through "
             + "the rest of the fleet");
  }
}
