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
    var worker = new TableStatisticsCollector(
  scopeFactory: new AlwaysDisposedScopeFactory(),
  metrics: metrics,
  schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var executeTask = worker.ExecuteTask!;

    // With fix: completes immediately (breaks on ObjectDisposedException).
    // Without fix: waits 30 s before retrying → times out here.
    await executeTask.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(executeTask.IsCompletedSuccessfully).IsTrue()
      .Because("Collector should break out of the loop on ObjectDisposedException, not retry");
  }


  /// <summary>Answers with a bloated table so the test can tell whether the ratio reaches the metric.</summary>
  private sealed class BloatReportingProvider : ITableStatisticsProvider {
    public Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
    public Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
    public Task<IReadOnlyDictionary<string, double>> GetTableBloatRatiosAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, double>>(
        new Dictionary<string, double> { ["wh_event_store"] = 4.2 });
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
  ///
  /// <para>
  /// Waits on <c>CycleCompleted</c>, which fires after the caches are written. An earlier version
  /// waited on the provider being CALLED, which only proves the collector asked — the write lands
  /// on the following line, so the assertion raced it and lost under CI load. Subscribing before
  /// StartAsync closes the other direction: the cycle cannot complete before we are listening.
  /// </para>
  /// </summary>
  [Test]
  public async Task Collector_PublishesTableBloatRatioAsync() {
    var provider = new BloatReportingProvider();
    var metrics = new TableStatisticsMetrics(new WhizbangMetrics());
    var worker = new TableStatisticsCollector(
  scopeFactory: new SingleProviderScopeFactory(provider),
  metrics: metrics,
  schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    var cycled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    double? ratioWhenSignalled = null;
    worker.CycleCompleted += () => {
      // Sampled INSIDE the handler, so this also pins the ordering: a signal raised before the
      // write would read null here no matter how the continuations happen to be scheduled.
      ratioWhenSignalled = metrics.GetBloatRatioForTest("wh_event_store");
      cycled.TrySetResult();
    };

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await cycled.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(ratioWhenSignalled).IsEqualTo(4.2)
      .Because("the cycle signal must come after the caches are written, or every observer of it "
               + "races the value it is waiting for — which is how this test failed under CI load");

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
