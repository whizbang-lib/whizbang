// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Issue #1668: the collector reads the tables every <see cref="TableStatisticsOptions.CollectionInterval"/>, and
/// every thirty seconds when it is given no settings.
/// </summary>
/// <remarks>
/// Every wait is on a signal: a cycle's completion, or the collector arming its timer on a fake clock, which the test
/// then advances. Nothing sleeps.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsCollector.cs</code-under-test>
/// <docs>operations/observability/metrics#table-statistics</docs>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsCollectorIntervalTests {
  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

  [Test]
  public async Task TheConfiguredInterval_IsTheWaitBetweenCyclesAsync() {
    var clock = new ArmedTimerClock();
    var interval = TimeSpan.FromMinutes(2);
    await using var host = new CollectorHost(
      Options.Create(new TableStatisticsOptions { CollectionInterval = interval, CollectionJitter = 0 }), clock);

    var first = host.NextCycle();
    await host.StartAsync();
    await first.WaitAsync(_wait);
    await clock.WaitForArmedAsync(interval, _wait);
    var second = host.NextCycle();
    clock.Advance(interval);
    await second.WaitAsync(_wait);

    await Assert.That(host.Provider.Reads).IsEqualTo(2)
      .Because("the collector waits the configured interval after a cycle, and reads again when it has passed");
  }

  /// <summary>
  /// #2349: the probe runs on a cadence of minutes, not seconds. Without settings each wait is five minutes plus up to a
  /// fifth of that again, drawn from the collector's random source, so a fleet started together does not read the
  /// statistics catalog together. Reproducible: the same seed gives the same wait.
  /// </summary>
  /// <docs>operations/observability/metrics#table-statistics-refresh</docs>
  [Test]
  public async Task WithoutSettings_TheWaitIsFiveMinutesPlusItsJitterAsync() {
    var clock = new ArmedTimerClock();
    var expected = TimeSpan.FromMinutes(5) + (TimeSpan.FromMinutes(5) * 0.2 * new Random(7).NextDouble());
    await using var host = new CollectorHost(Options.Create(new TableStatisticsOptions()), clock, new Random(7));

    var first = host.NextCycle();
    await host.StartAsync();
    await first.WaitAsync(_wait);
    await clock.WaitForArmedAsync(expected, _wait);

    await Assert.That(host.Provider.Reads).IsEqualTo(1);
    await Assert.That(new TableStatisticsOptions().CollectionInterval).IsEqualTo(TimeSpan.FromMinutes(5));
    await Assert.That(new TableStatisticsOptions().CollectionJitter).IsEqualTo(0.2);
  }

  /// <summary>#2349: two collectors with different random sources wait different times, so their reads spread out.</summary>
  /// <docs>operations/observability/metrics#table-statistics-refresh</docs>
  [Test]
  public async Task TwoCollectors_WaitDifferentTimes_SoTheirReadsSpreadAsync() {
    var interval = TimeSpan.FromMinutes(2);
    var options = Options.Create(new TableStatisticsOptions { CollectionInterval = interval, CollectionJitter = 0.5 });
    var waits = new List<TimeSpan>();
    foreach (var seed in new[] { 1, 2 }) {
      var clock = new ArmedTimerClock();
      var expected = interval + (interval * 0.5 * new Random(seed).NextDouble());
      await using var host = new CollectorHost(options, clock, new Random(seed));
      var first = host.NextCycle();
      await host.StartAsync();
      await first.WaitAsync(_wait);
      await clock.WaitForArmedAsync(expected, _wait);
      waits.Add(expected);
    }

    await Assert.That(waits[0]).IsNotEqualTo(waits[1]);
    await Assert.That(waits.All(w => w >= interval && w <= interval * 1.5)).IsTrue()
      .Because("the jitter only ever lengthens the wait, by at most its fraction of the interval");
  }

  private sealed class CollectorHost : IAsyncDisposable {
    private readonly ServiceProvider _container;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TestMeterFactory _meters = new();
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CollectorHost(IOptions<TableStatisticsOptions> options, TimeProvider clock, Random? random = null) {
      var services = new ServiceCollection();
      services.AddSingleton<ITableStatisticsProvider>(Provider);
      _container = services.BuildServiceProvider();
      Collector = new TableStatisticsCollector(
        _container.GetRequiredService<IServiceScopeFactory>(),
        new TableStatisticsMetrics(new WhizbangMetrics(_meters)),
        NullLogger<TableStatisticsCollector>.Instance,
        options,
        clock,
        random);
      Collector.CycleCompleted += () => Volatile.Read(ref _next).TrySetResult();
    }

    public CountingProvider Provider { get; } = new();

    public TableStatisticsCollector Collector { get; }

    public Task NextCycle() {
      var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      Volatile.Write(ref _next, next);
      return next.Task;
    }

    public Task StartAsync() => Collector.StartAsync(_stopping.Token);

    public async ValueTask DisposeAsync() {
      await _stopping.CancelAsync();
      await Collector.StopAsync(CancellationToken.None);
      _stopping.Dispose();
      _meters.Dispose();
      await _container.DisposeAsync();
    }
  }

  /// <summary>Answers empty readings and counts how many cycles asked.</summary>
  private sealed class CountingProvider : ITableStatisticsProvider {
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    public Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) {
      Interlocked.Increment(ref _reads);
      return Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
    }

    public Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
  }
}
