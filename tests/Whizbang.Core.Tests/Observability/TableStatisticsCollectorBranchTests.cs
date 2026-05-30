using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

#pragma warning disable CA1707
#pragma warning disable IDE1006

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

  private static TableStatisticsMetrics _newMetrics() => new(new WhizbangMetrics());

  [Test]
  public async Task NoProvider_LogsAndExitsLoopAsync() {
    // When ITableStatisticsProvider isn't registered in DI, the collector
    // logs once and exits (no point retrying — registration is static).
    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var worker = new TableStatisticsCollector(
      sp.GetRequiredService<IServiceScopeFactory>(),
      _newMetrics());

    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(worker.ExecuteTask.IsCompletedSuccessfully).IsTrue()
      .Because("Missing provider is a config-level no-op; collector exits without retrying");
  }

  [Test]
  public async Task ProviderRegistered_PopulatesMetricsThenWaitsAsync() {
    // Happy path: provider returns sizes + depths, both land on the metrics
    // before the collector enters its 30s Task.Delay.
    var fakeProvider = new _RecordingProvider {
      SizesToReturn = new Dictionary<string, long> { ["wh_outbox"] = 4096, ["wh_inbox"] = 8192 },
      DepthsToReturn = new Dictionary<string, long> { ["outbox"] = 3, ["inbox"] = 7 },
    };
    var services = new ServiceCollection();
    services.AddSingleton<ITableStatisticsProvider>(fakeProvider);
    var sp = services.BuildServiceProvider();

    var metrics = _newMetrics();
    var worker = new TableStatisticsCollector(
      sp.GetRequiredService<IServiceScopeFactory>(), metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Wait until the provider has been hit, then cancel so the collector
    // exits its Task.Delay and we don't sit through 30s.
    await fakeProvider.SizesCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await fakeProvider.DepthsCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    cts.Cancel();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(fakeProvider.SizesCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(fakeProvider.DepthsCallCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task ProviderThrows_LogsAndContinuesLoopAsync() {
    // Generic exception in the provider should be logged and the loop should
    // continue to the Task.Delay. Cancel the token after the first throw so
    // the test exits promptly instead of waiting 30s for the next tick.
    var fakeProvider = new _RecordingProvider {
      ThrowOnNextCall = new InvalidOperationException("simulated db error"),
    };
    var services = new ServiceCollection();
    services.AddSingleton<ITableStatisticsProvider>(fakeProvider);
    var sp = services.BuildServiceProvider();
    var worker = new TableStatisticsCollector(
      sp.GetRequiredService<IServiceScopeFactory>(), _newMetrics());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await fakeProvider.SizesCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    cts.Cancel();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(fakeProvider.SizesCallCount).IsEqualTo(1);
  }

  // ---------------- fakes ----------------

  private sealed class _RecordingProvider : ITableStatisticsProvider {
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
