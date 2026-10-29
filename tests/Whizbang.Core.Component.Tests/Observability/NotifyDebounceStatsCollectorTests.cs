// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
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
/// Every wait is on the transition it asserts, on a fake clock, bounded only by the test's own timeout.
/// </summary>
/// <docs>operations/observability/metrics#notify-debounce</docs>
[Category("Core")]
[Category("Observability")]
public class NotifyDebounceStatsCollectorTests {

  private static NotifyDebounceMetrics _newMetrics() => new(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  private static readonly TimeSpan _interval = TimeSpan.FromSeconds(15);

  [Test]
  [Timeout(30000)]
  public async Task NoProvider_LogsAndExitsLoopAsync(CancellationToken cancellationToken) {
    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var worker = new NotifyDebounceStatsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: _newMetrics(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      logger: NullLogger<NotifyDebounceStatsCollector>.Instance,
      timeProvider: new ArmedTimerClock());

    // The clock is never advanced, so only the early return can complete the task.
    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(cancellationToken);
    await Assert.That(worker.ExecuteTask.IsCompletedSuccessfully).IsTrue()
      .Because("a missing provider is a config no-op: the collector exits without retrying");
  }

  [Test]
  [Timeout(30000)]
  public async Task ProviderRegistered_PopulatesMetrics_ThenWaitsAsync(CancellationToken cancellationToken) {
    var fake = new RecordingProvider {
      ToReturn = [
        new NotifyDebounceKindStats("inbox", 10, 2, 50, 0),
        new NotifyDebounceKindStats("outbox", 3, 40, 7000, 9),
      ],
    };
    var services = new ServiceCollection();
    services.AddSingleton<INotifyDebounceStatsProvider>(fake);
    var sp = services.BuildServiceProvider();

    var clock = new ArmedTimerClock();
    var metrics = _newMetrics();
    var worker = new NotifyDebounceStatsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: metrics,
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      logger: NullLogger<NotifyDebounceStatsCollector>.Instance,
      timeProvider: clock);
    var cycled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.CycleCompleted += () => cycled.TrySetResult();

    // Each wait is on the transition asserted: the cycle completing after the cache is written, then the
    // interval being armed on the fake clock. Only the test's own timeout bounds them.
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await cycled.Task.WaitAsync(cancellationToken);
    await clock.WaitForArmedAsync(_interval, cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken);

    await Assert.That(fake.CallCount).IsEqualTo(1)
      .Because("one cycle reads once, then waits its interval on the clock before reading again");
    var outbox = metrics.GetForTest("outbox");
    await Assert.That(outbox.HasValue).IsTrue()
      .Because("the collector must feed the provider's readings into the metric cache");
    await Assert.That(outbox!.Value.MaxEffectiveWindowMs).IsEqualTo(7000);
  }

  [Test]
  [Timeout(30000)]
  public async Task ProviderThrows_LogsAndContinuesLoopAsync(CancellationToken cancellationToken) {
    var fake = new RecordingProvider { ThrowOnNextCall = new InvalidOperationException("simulated db error") };
    var services = new ServiceCollection();
    services.AddSingleton<INotifyDebounceStatsProvider>(fake);
    var sp = services.BuildServiceProvider();
    var clock = new ArmedTimerClock();
    var worker = new NotifyDebounceStatsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: _newMetrics(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      logger: NullLogger<NotifyDebounceStatsCollector>.Instance,
      timeProvider: clock);
    var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.CycleCompleted += () => recovered.TrySetResult();

    // The failed read is logged and the loop arms its interval on the collector's clock instead of
    // faulting; advancing the clock past it is the only thing that causes the second read.
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await fake.Called.Task.WaitAsync(cancellationToken);
    await clock.WaitForArmedAsync(_interval, cancellationToken);
    clock.Advance(_interval);
    await recovered.Task.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken);

    await Assert.That(fake.CallCount).IsEqualTo(2)
      .Because("the throw is logged, the loop waits its interval, and the next cycle reads again");
  }

  [Test]
  [Timeout(30000)]
  public async Task ShutdownDuringSchemaWait_ExitsWithoutQueryingTheProviderAsync(CancellationToken cancellationToken) {
    // The gate exists because the state table arrives with a migration. A collector that fell
    // through the gate on shutdown would issue its first query against a schema that may not
    // exist yet -- and log an error for it -- while the host is already tearing down.
    var fake = new RecordingProvider();
    var services = new ServiceCollection();
    services.AddSingleton<INotifyDebounceStatsProvider>(fake);
    var sp = services.BuildServiceProvider();

    var gate = new BlockingGate();
    var worker = new NotifyDebounceStatsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: _newMetrics(),
      schemaReadyGate: gate,
      logger: NullLogger<NotifyDebounceStatsCollector>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Wait on a signal the code under test emits -- StartAsync returning proves nothing, the
    // loop body runs on the thread pool.
    await gate.Entered.Task.WaitAsync(cancellationToken);
    await cts.CancelAsync();

    await worker.ExecuteTask!.WaitAsync(cancellationToken)
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
  [Timeout(30000)]
  public async Task ProviderThrowsObjectDisposed_LeavesTheLoopInsteadOfRetryingAsync(CancellationToken cancellationToken) {
    // ObjectDisposedException here means the host's service provider is gone -- every later
    // cycle would throw the same way. The generic handler would log a warning every 15 seconds
    // for the remaining life of the process; the dedicated break is what stops that.
    var fake = new RecordingProvider { ThrowOnNextCall = new ObjectDisposedException("root provider") };
    var services = new ServiceCollection();
    services.AddSingleton<INotifyDebounceStatsProvider>(fake);
    var sp = services.BuildServiceProvider();
    var worker = new NotifyDebounceStatsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: _newMetrics(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      logger: NullLogger<NotifyDebounceStatsCollector>.Instance,
      timeProvider: new ArmedTimerClock());

    // No cancellation anywhere: the ONLY thing that can end this task is the ObjectDisposedException
    // break. The log-and-continue arm would park on an interval the fake clock never reaches.
    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(cancellationToken);

    await Assert.That(worker.ExecuteTask.IsCompletedSuccessfully).IsTrue();
    await Assert.That(fake.CallCount).IsEqualTo(1)
      .Because("a disposed host is terminal -- the collector exits rather than retrying forever");
  }

  // ---------------- fakes ----------------

  /// <summary>A gate that never opens, and reports when a waiter has actually reached it.</summary>
  private sealed class BlockingGate : ISchemaReadyGate {
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

  private sealed class RecordingProvider : INotifyDebounceStatsProvider {
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
