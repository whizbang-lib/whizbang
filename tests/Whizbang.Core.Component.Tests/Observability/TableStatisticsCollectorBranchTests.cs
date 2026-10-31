// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Drives every branch of <see cref="TableStatisticsCollector.ExecuteAsync"/>
/// the existing single test (ObjectDisposedException → break) didn't reach:
/// the schema-ready gate happy path + its cancellation, the
/// missing-provider early return, the happy collection cycle (sizes + depths
/// flow into <see cref="TableStatisticsMetrics"/>), and the
/// generic-exception → retry path.
/// </summary>
/// <docs>operations/observability/metrics#table-statistics</docs>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsCollectorBranchTests {

  private static TableStatisticsMetrics _newMetrics() => new(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  [Test]
  [Timeout(30000)]
  public async Task NoProvider_LogsAndExitsLoopAsync(CancellationToken cancellationToken) {
    // When ITableStatisticsProvider isn't registered in DI, the collector
    // logs once and exits (no point retrying — registration is static).
    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var worker = new TableStatisticsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: _newMetrics(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(), logger: NullLogger<TableStatisticsCollector>.Instance,
      options: Microsoft.Extensions.Options.Options.Create(new TableStatisticsOptions()));

    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(cancellationToken);
    await Assert.That(worker.ExecuteTask.IsCompletedSuccessfully).IsTrue()
      .Because("Missing provider is a config-level no-op; collector exits without retrying");
  }

  [Test]
  [Timeout(30000)]
  public async Task NoLogger_StillLogsAndExitsAsync(CancellationToken cancellationToken) {
    // A null logger falls back to a discarding one: the missing-provider line is written to it
    // instead of faulting the worker on a null reference.
    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new TableStatisticsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: _newMetrics(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(), logger: null!,
      options: Microsoft.Extensions.Options.Options.Create(new TableStatisticsOptions()));

    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(cancellationToken);
    await Assert.That(worker.ExecuteTask.IsCompletedSuccessfully).IsTrue();
  }

  [Test]
  [Timeout(30000)]
  public async Task ProviderRegistered_PopulatesMetricsThenWaitsAsync(CancellationToken cancellationToken) {
    // Happy path: provider returns sizes + depths, both land on the metrics before the collector arms
    // its interval. Every wait is on the transition it asserts: the cycle completing (after the caches
    // are written), then the interval timer being armed on the fake clock. Nothing waits on wall time.
    var fakeProvider = new RecordingProvider {
      SizesToReturn = new Dictionary<string, long> { ["wh_outbox"] = 4096, ["wh_inbox"] = 8192 },
      DepthsToReturn = new Dictionary<string, long> { ["outbox"] = 3, ["inbox"] = 7 },
    };
    var services = new ServiceCollection();
    services.AddSingleton<ITableStatisticsProvider>(fakeProvider);
    var sp = services.BuildServiceProvider();

    var clock = new ArmedTimerClock();
    var metrics = _newMetrics();
    var worker = new TableStatisticsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: metrics,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(), logger: NullLogger<TableStatisticsCollector>.Instance,
      options: Microsoft.Extensions.Options.Options.Create(new TableStatisticsOptions()),
      timeProvider: clock);
    var cycled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.CycleCompleted += () => cycled.TrySetResult();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await cycled.Task.WaitAsync(cancellationToken);
    await clock.WaitForArmedAsync(TimeSpan.FromSeconds(30), cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken);

    await Assert.That(fakeProvider.SizesCallCount).IsEqualTo(1);
    await Assert.That(fakeProvider.DepthsCallCount).IsEqualTo(1)
      .Because("one cycle reads once, then waits its interval on the clock before reading again");
  }

  [Test]
  [Timeout(30000)]
  public async Task ProviderThrows_LogsAndContinuesLoopAsync(CancellationToken cancellationToken) {
    // A generic exception in the provider is logged and the loop continues: it arms its interval
    // instead of faulting, and when the interval passes it reads again. The fake clock is advanced
    // only once the timer is armed, so the second read is caused by the interval and nothing else.
    var fakeProvider = new RecordingProvider {
      ThrowOnNextCall = new InvalidOperationException("simulated db error"),
    };
    var services = new ServiceCollection();
    services.AddSingleton<ITableStatisticsProvider>(fakeProvider);
    var sp = services.BuildServiceProvider();
    var clock = new ArmedTimerClock();
    var interval = TimeSpan.FromSeconds(30);
    var worker = new TableStatisticsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: _newMetrics(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(), logger: NullLogger<TableStatisticsCollector>.Instance,
      options: Microsoft.Extensions.Options.Options.Create(new TableStatisticsOptions()),
      timeProvider: clock);
    var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.CycleCompleted += () => recovered.TrySetResult();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await fakeProvider.SizesCalled.Task.WaitAsync(cancellationToken);
    await clock.WaitForArmedAsync(interval, cancellationToken);
    clock.Advance(interval);
    await recovered.Task.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken);

    await Assert.That(fakeProvider.SizesCallCount).IsEqualTo(2)
      .Because("the failed read is logged, the loop waits its interval, and the next cycle reads again");
  }

  // ---------------- fakes ----------------

  private sealed class RecordingProvider : ITableStatisticsProvider {
    public IReadOnlyDictionary<string, long> SizesToReturn { get; set; } = new Dictionary<string, long>();
    public IReadOnlyDictionary<string, long> DepthsToReturn { get; set; } = new Dictionary<string, long>();
    public Exception? ThrowOnNextCall { get; set; }
    public int SizesCallCount { get; private set; }
    public int DepthsCallCount { get; private set; }
    public TaskCompletionSource SizesCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DepthsCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) {
      SizesCallCount++;
      SizesCalled.TrySetResult();
      if (ThrowOnNextCall is not null) {
        var ex = ThrowOnNextCall;
        ThrowOnNextCall = null;
        throw ex;
      }
      return Task.FromResult(SizesToReturn);
    }

    public Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default) {
      DepthsCallCount++;
      DepthsCalled.TrySetResult();
      return Task.FromResult(DepthsToReturn);
    }
  }
}
