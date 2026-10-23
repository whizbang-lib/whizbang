// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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
    await using var host = new CollectorHost(Options.Create(new TableStatisticsOptions { CollectionInterval = interval }), clock);

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

  [Test]
  public async Task WithoutSettings_TheWaitIsThirtySecondsAsync() {
    var clock = new ArmedTimerClock();
    await using var host = new CollectorHost(options: null, clock);

    var first = host.NextCycle();
    await host.StartAsync();
    await first.WaitAsync(_wait);
    await clock.WaitForArmedAsync(TimeSpan.FromSeconds(30), _wait);

    await Assert.That(host.Provider.Reads).IsEqualTo(1)
      .Because("thirty seconds is the interval the collector always used, so a host that sets nothing sees no change");
  }

  /// <summary>A collector on a fake clock over a provider that counts its reads.</summary>
  private sealed class CollectorHost : IAsyncDisposable {
    private readonly ServiceProvider _container;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TestMeterFactory _meters = new();
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CollectorHost(IOptions<TableStatisticsOptions>? options, TimeProvider clock) {
      var services = new ServiceCollection();
      services.AddSingleton<ITableStatisticsProvider>(Provider);
      _container = services.BuildServiceProvider();
      Collector = new TableStatisticsCollector(
        _container.GetRequiredService<IServiceScopeFactory>(),
        new TableStatisticsMetrics(new WhizbangMetrics(_meters)),
        SchemaReadyGate.AlreadyReady(),
        NullLogger<TableStatisticsCollector>.Instance,
        options,
        clock);
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

  /// <summary>A fake clock that reports each timer armed on it, so a test can wait for the collector to start waiting.</summary>
  private sealed class ArmedTimerClock() : FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) {
    private readonly Channel<TimeSpan> _armed = Channel.CreateUnbounded<TimeSpan>();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      var timer = base.CreateTimer(callback, state, dueTime, period);
      _armed.Writer.TryWrite(dueTime);
      return timer;
    }

    /// <summary>Completes once a timer with <paramref name="due"/> has been armed.</summary>
    public async Task WaitForArmedAsync(TimeSpan due, TimeSpan timeout) {
      using var cts = new CancellationTokenSource(timeout);
      TimeSpan armed;
      do {
        armed = await _armed.Reader.ReadAsync(cts.Token);
      } while (armed != due);
    }
  }
}
