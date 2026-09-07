using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Drives every branch of <see cref="NotifyDebounceStatsCollector.ExecuteAsync"/>: the missing-
/// provider early return, the happy cycle (readings flow into <see cref="NotifyDebounceMetrics"/>),
/// and the provider-throws → log-and-retry path. Mirrors the TableStatisticsCollector branch tests.
/// </summary>
/// <docs>operations/observability/metrics#notify-debounce</docs>
[Category("Core")]
[Category("Observability")]
public class NotifyDebounceStatsCollectorTests {

  private static NotifyDebounceMetrics _newMetrics() => new(new WhizbangMetrics());

  [Test]
  public async Task NoProvider_LogsAndExitsLoopAsync() {
    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var worker = new NotifyDebounceStatsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: _newMetrics(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      logger: NullLogger<NotifyDebounceStatsCollector>.Instance);

    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(worker.ExecuteTask.IsCompletedSuccessfully).IsTrue()
      .Because("a missing provider is a config no-op — the collector exits without retrying");
  }

  [Test]
  public async Task ProviderRegistered_PopulatesMetrics_ThenWaitsAsync() {
    var fake = new _RecordingProvider {
      ToReturn = [
        new NotifyDebounceKindStats("inbox", 10, 2, 50, 0),
        new NotifyDebounceKindStats("outbox", 3, 40, 7000, 9),
      ],
    };
    var services = new ServiceCollection();
    services.AddSingleton<INotifyDebounceStatsProvider>(fake);
    var sp = services.BuildServiceProvider();

    var metrics = _newMetrics();
    var worker = new NotifyDebounceStatsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: metrics,
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      logger: NullLogger<NotifyDebounceStatsCollector>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await fake.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
    cts.Cancel();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(fake.CallCount).IsGreaterThanOrEqualTo(1);
    var outbox = metrics.GetForTest("outbox");
    await Assert.That(outbox.HasValue).IsTrue()
      .Because("the collector must feed the provider's readings into the metric cache");
    await Assert.That(outbox!.Value.MaxEffectiveWindowMs).IsEqualTo(7000);
  }

  [Test]
  public async Task ProviderThrows_LogsAndContinuesLoopAsync() {
    var fake = new _RecordingProvider { ThrowOnNextCall = new InvalidOperationException("simulated db error") };
    var services = new ServiceCollection();
    services.AddSingleton<INotifyDebounceStatsProvider>(fake);
    var sp = services.BuildServiceProvider();
    var worker = new NotifyDebounceStatsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: _newMetrics(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      logger: NullLogger<NotifyDebounceStatsCollector>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await fake.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
    cts.Cancel();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(fake.CallCount).IsEqualTo(1)
      .Because("the throw is logged and the loop continues to the delay, not a fault");
  }

  [Test]
  public async Task ShutdownDuringSchemaWait_ExitsWithoutQueryingTheProviderAsync() {
    // The gate exists because the state table arrives with a migration. A collector that fell
    // through the gate on shutdown would issue its first query against a schema that may not
    // exist yet -- and log an error for it -- while the host is already tearing down.
    var fake = new _RecordingProvider();
    var services = new ServiceCollection();
    services.AddSingleton<INotifyDebounceStatsProvider>(fake);
    var sp = services.BuildServiceProvider();

    var gate = new _BlockingGate();
    var worker = new NotifyDebounceStatsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: _newMetrics(),
      schemaReadyGate: gate,
      logger: NullLogger<NotifyDebounceStatsCollector>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Wait on a signal the code under test emits -- StartAsync returning proves nothing, the
    // loop body runs on the thread pool.
    await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();

    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue();
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("a shutdown while waiting on the schema gate is an ordinary exit, not a fault");
    await Assert.That(fake.CallCount).IsEqualTo(0)
      .Because("the collector must never reach its first provider read when shutdown cancels "
             + "the schema wait -- that read would hit a table the migration may not have created");
    await Assert.That(gate.WaitCount).IsEqualTo(1)
      .Because("proves the gate really was awaited rather than skipped, which is what makes the "
             + "zero-call assertion above mean something");
  }

  [Test]
  public async Task ProviderThrowsObjectDisposed_LeavesTheLoopInsteadOfRetryingAsync() {
    // ObjectDisposedException here means the host's service provider is gone -- every later
    // cycle would throw the same way. The generic handler would log a warning every 15 seconds
    // for the remaining life of the process; the dedicated break is what stops that.
    var fake = new _RecordingProvider { ThrowOnNextCall = new ObjectDisposedException("root provider") };
    var services = new ServiceCollection();
    services.AddSingleton<INotifyDebounceStatsProvider>(fake);
    var sp = services.BuildServiceProvider();
    var worker = new NotifyDebounceStatsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: _newMetrics(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      logger: NullLogger<NotifyDebounceStatsCollector>.Instance);

    // No cancellation anywhere: the ONLY thing that can end this task within the timeout is the
    // ObjectDisposedException break. The log-and-continue arm would sit in a 15-second delay.
    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(worker.ExecuteTask.IsCompletedSuccessfully).IsTrue();
    await Assert.That(fake.CallCount).IsEqualTo(1)
      .Because("a disposed host is terminal -- the collector exits rather than retrying forever");
  }

  // ---------------- fakes ----------------

  /// <summary>A gate that never opens, and reports when a waiter has actually reached it.</summary>
  private sealed class _BlockingGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _waitCount;

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int WaitCount => Volatile.Read(ref _waitCount);
    public bool IsReady => false;
    public void MarkReady() { }

    public Task WaitForReadyAsync(CancellationToken cancellationToken) {
      Interlocked.Increment(ref _waitCount);
      Entered.TrySetResult();
      return _never.Task.WaitAsync(cancellationToken);
    }
  }

  private sealed class _RecordingProvider : INotifyDebounceStatsProvider {
    public IReadOnlyList<NotifyDebounceKindStats> ToReturn { get; set; } = [];
    public Exception? ThrowOnNextCall { get; set; }
    public int CallCount { get; private set; }
    public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyList<NotifyDebounceKindStats>> GetStatsAsync(CancellationToken ct = default) {
      CallCount++;
      Called.TrySetResult();
      if (ThrowOnNextCall is not null) {
        var ex = ThrowOnNextCall;
        ThrowOnNextCall = null;
        throw ex;
      }
      return Task.FromResult(ToReturn);
    }
  }
}
