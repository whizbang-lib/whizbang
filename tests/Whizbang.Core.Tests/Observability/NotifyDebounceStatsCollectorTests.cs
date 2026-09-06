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

  // ---------------- fakes ----------------

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
