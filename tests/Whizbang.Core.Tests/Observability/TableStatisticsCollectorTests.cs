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

  #region Test Fakes

  private sealed class AlwaysDisposedScopeFactory : IServiceScopeFactory {
    public IServiceScope CreateScope() {
      ObjectDisposedException.ThrowIf(true, nameof(IServiceProvider));
      return null!; // unreachable
    }
  }

  #endregion
}
