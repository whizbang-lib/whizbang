using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for <see cref="TableStatisticsCollector"/> background service lifecycle.
/// </summary>
/// <tests>src/Whizbang.Core/Observability/TableStatisticsCollector.cs</tests>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsCollectorTests {

  [Test]
  public async Task Collector_WhenScopeFactoryDisposed_ExitsGracefullyWithoutRetryAsync() {
    // Reproduces: Kestrel bind failure → DI container disposed → TableStatisticsCollector
    // used to catch ObjectDisposedException, log a warning, then wait 30s and retry forever.
    // After the fix it should break out of the loop and exit cleanly.
    var metrics = new TableStatisticsMetrics(new WhizbangMetrics());
    var worker = new TableStatisticsCollector(new AlwaysDisposedScopeFactory(), metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var executeTask = worker.ExecuteTask!;

    // With fix: completes immediately (breaks on ObjectDisposedException).
    // Without fix: waits 30 s before retrying → times out here.
    await executeTask.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(executeTask.IsCompletedSuccessfully).IsTrue()
      .Because("Collector should break out of the loop on ObjectDisposedException, not retry");
  }


  /// <summary>
  /// Records what the collector asked for and answers with a bloated table, so the test can tell
  /// whether the bloat ratio actually reaches the metric rather than merely being computable.
  /// </summary>
  private sealed class BloatReportingProvider : ITableStatisticsProvider {
    public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
    public Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
    public Task<IReadOnlyDictionary<string, double>> GetTableBloatRatiosAsync(CancellationToken ct = default) {
      Asked.TrySetResult();
      return Task.FromResult<IReadOnlyDictionary<string, double>>(
        new Dictionary<string, double> { ["wh_event_store"] = 4.2 });
    }
  }

  private sealed class SingleProviderScopeFactory(ITableStatisticsProvider provider)
    : IServiceScopeFactory, IServiceScope, IServiceProvider {
    public IServiceScope CreateScope() => this;
    public IServiceProvider ServiceProvider => this;
    public object? GetService(Type serviceType) =>
      serviceType == typeof(ITableStatisticsProvider) ? provider : null;
    public void Dispose() { }
  }

  /// <summary>
  /// A table that occupies far more space than its live rows need costs on every read: index
  /// heap-fetches pull emptier pages and the buffer cache holds fewer useful rows. The usual
  /// cause is dead tuples awaiting vacuum; the invisible one is a dropped column, whose bytes
  /// Postgres keeps in every pre-existing row until the table is rewritten — autovacuum never
  /// returns them. Either way the operator has no way to see it without going looking, which is
  /// exactly how a table ends up several times its necessary size unnoticed.
  ///
  /// So the collector must actually PUBLISH the ratio, not merely be able to compute it. This
  /// asserts the value reaches the metric, because a gauge that is wired but never fed reports
  /// a healthy silence indistinguishable from a healthy system.
  /// </summary>
  [Test]
  public async Task Collector_PublishesTableBloatRatioAsync() {
    var provider = new BloatReportingProvider();
    var metrics = new TableStatisticsMetrics(new WhizbangMetrics());
    var worker = new TableStatisticsCollector(new SingleProviderScopeFactory(provider), metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await provider.Asked.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(metrics.GetBloatRatioForTest("wh_event_store")).IsEqualTo(4.2)
      .Because("the collector must feed the bloat gauge; an unfed gauge is silent, and silence "
               + "is indistinguishable from a lean table");

    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
  }

  #region Test Fakes

  private sealed class AlwaysDisposedScopeFactory : IServiceScopeFactory {
    public IServiceScope CreateScope() {
      ObjectDisposedException.ThrowIf(true, nameof(IServiceProvider));
      return null!; // unreachable
    }
  }

  #endregion
}
