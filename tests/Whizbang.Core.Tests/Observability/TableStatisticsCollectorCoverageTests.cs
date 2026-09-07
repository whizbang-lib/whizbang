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
/// Coverage round 23 tail: <see cref="TableStatisticsCollector.ExecuteAsync"/>'s early return when
/// the schema-ready wait is cancelled BEFORE the schema ever becomes ready. Every existing test
/// (<c>TableStatisticsCollectorTests.cs</c>, <c>TableStatisticsCollectorBranchTests.cs</c>)
/// constructs the collector with <see cref="SchemaReadyGate.AlreadyReady"/>, so the gate's wait
/// never actually blocks and this catch/return has never run.
/// </summary>
/// <docs>operations/observability/metrics#table-statistics</docs>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsCollectorCoverageTests {

  /// <summary>
  /// Operator impact: a host that shuts down (or aborts startup) while migrations are still
  /// running must let this collector exit cleanly instead of surfacing as a faulted/cancelled
  /// background-service task — which the generic host treats as a startup failure. Just as
  /// important: it must exit BEFORE ever touching <see cref="ITableStatisticsProvider"/>, since
  /// the schema it would query against isn't provisioned yet.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_SchemaGateCancelledBeforeReady_ExitsGracefullyWithoutTouchingProviderAsync() {
    var provider = new _RecordingProvider();
    var services = new ServiceCollection();
    services.AddSingleton<ITableStatisticsProvider>(provider);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate(); // deliberately never marked ready
    var worker = new TableStatisticsCollector(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      metrics: new TableStatisticsMetrics(new WhizbangMetrics()),
      schemaReadyGate: gate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await cts.CancelAsync();
    // See ReadModelsReadyDriverCoverageTests: a cancellation-catch exit settles as RanToCompletion
    // or Canceled by thread-pool timing, so suppress and assert completion rather than success.
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue()
      .Because("a cancelled schema-ready wait must be caught and turned into a graceful exit, "
             + "not surface as a faulted/cancelled hosted-service task");
    await Assert.That(provider.CallCount).IsEqualTo(0)
      .Because("the collector must return before ever entering the collection loop — reaching "
             + "the provider at all would mean the gate cancellation was ignored");
  }

  private sealed class _RecordingProvider : ITableStatisticsProvider {
    public int CallCount { get; private set; }

    public Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) {
      CallCount++;
      return Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
    }

    public Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default) {
      CallCount++;
      return Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
    }

    public Task<IReadOnlyDictionary<string, double>> GetTableBloatRatiosAsync(CancellationToken ct = default) {
      CallCount++;
      return Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());
    }
  }
}
