// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Helpers;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Issue #1669: each cycle reports the indexes nothing has scanned on the gauge, and says so in the log when the set
/// changes, so an operator is told once rather than every thirty seconds. Nothing is ever dropped.
/// </summary>
/// <remarks>
/// Every wait is on a signal: a cycle's completion, or the collector arming its timer on a fake clock, which the test
/// then advances. Nothing sleeps.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsCollector.cs</code-under-test>
/// <docs>operations/observability/metrics#unused-indexes</docs>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsCollectorUnusedIndexTests {
  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

  private static readonly UnusedIndex _customer = new("wh_per_orders", 81_920);
  private static readonly UnusedIndex _topic = new("wh_outbox", 16_384);

  [Test]
  public async Task AnUnusedIndex_IsOnTheGauge_AndInTheLogAsync() {
    await using var host = new CollectorHost(new Dictionary<string, UnusedIndex> { ["idx_orders_customer"] = _customer });

    await host.StartAndRunCycleAsync();

    await Assert.That(host.Read()).Contains(("whizbang.table.unused_index_bytes", 81_920d, "wh_per_orders", "idx_orders_customer"));
    var logged = host.UnusedIndexLines();
    await Assert.That(logged.Count).IsEqualTo(1);
    await Assert.That(logged[0].Level).IsEqualTo(LogLevel.Information);
    await Assert.That(logged[0].Message).Contains("idx_orders_customer (wh_per_orders, 81920 bytes)")
      .Because("the line names the index, its table and its size, which is what an operator needs to decide");
    await Assert.That(logged[0].Message).Contains("Nothing is dropped")
      .Because("an unused index may serve a rare path, so the report never acts on its own finding");
  }

  [Test]
  public async Task TheSameSetAgain_IsNotLoggedAgain_ButAChangedSetIsAsync() {
    var both = new Dictionary<string, UnusedIndex> { ["idx_orders_customer"] = _customer, ["idx_outbox_topic"] = _topic };
    await using var host = new CollectorHost(
      new Dictionary<string, UnusedIndex> { ["idx_orders_customer"] = _customer },
      new Dictionary<string, UnusedIndex> { ["idx_orders_customer"] = _customer },
      both);

    await host.StartAndRunCycleAsync();
    await host.RunNextCycleAsync();
    var afterRepeat = host.UnusedIndexLines().Count;
    await host.RunNextCycleAsync();
    var lines = host.UnusedIndexLines();

    await Assert.That(afterRepeat).IsEqualTo(1)
      .Because("a finding repeated every cycle is noise; the gauge carries the standing value");
    await Assert.That(lines.Count).IsEqualTo(2);
    await Assert.That(lines[1].Message).Contains("idx_outbox_topic (wh_outbox, 16384 bytes)");
  }

  [Test]
  public async Task NoUnusedIndexes_IsNotLogged_AndASetThatReturnsIsLoggedAgainAsync() {
    await using var host = new CollectorHost(
      new Dictionary<string, UnusedIndex> { ["idx_orders_customer"] = _customer },
      new Dictionary<string, UnusedIndex>(),
      new Dictionary<string, UnusedIndex> { ["idx_orders_customer"] = _customer });

    await host.StartAndRunCycleAsync();
    await host.RunNextCycleAsync();
    var afterEmpty = host.UnusedIndexLines().Count;
    var readingsWhenEmpty = host.Read();
    await host.RunNextCycleAsync();

    await Assert.That(afterEmpty).IsEqualTo(1)
      .Because("there is nothing to tell an operator when every index has been used");
    await Assert.That(readingsWhenEmpty).IsEmpty()
      .Because("an index a query has since used stops reporting");
    await Assert.That(host.UnusedIndexLines().Count).IsEqualTo(2)
      .Because("an index that is unused again after being used is a new finding");
  }

  [Test]
  public async Task WithInformationLoggingOff_TheGaugeIsStillReportedAsync() {
    // The line is built only when it would be written; the gauge does not depend on it.
    await using var host = new CollectorHost(
      new SilentLogger(),
      new Dictionary<string, UnusedIndex> { ["idx_orders_customer"] = _customer });

    await host.StartAndRunCycleAsync();

    await Assert.That(host.Read()).Contains(("whizbang.table.unused_index_bytes", 81_920d, "wh_per_orders", "idx_orders_customer"));
    await Assert.That(host.UnusedIndexLines()).IsEmpty();
  }

  /// <summary>A collector on a fake clock, over a provider that answers each cycle with the next scripted set.</summary>
  private sealed class CollectorHost : IAsyncDisposable {
    private readonly ServiceProvider _container;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TestMeterFactory _meters = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, double Value, string? Table, string? Index)> _readings = [];
    private readonly CapturingLogger<TableStatisticsCollector> _logger = new();
    private readonly ArmedTimerClock _clock = new();
    private readonly TableStatisticsCollector _collector;
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CollectorHost(params IReadOnlyDictionary<string, UnusedIndex>[] cycles)
      : this(logger: null, cycles) {
    }

    public CollectorHost(ILogger<TableStatisticsCollector>? logger, params IReadOnlyDictionary<string, UnusedIndex>[] cycles) {
      var services = new ServiceCollection();
      services.AddSingleton<ITableStatisticsProvider>(new ScriptedProvider(cycles));
      _container = services.BuildServiceProvider();
      _collector = new TableStatisticsCollector(
        _container.GetRequiredService<IServiceScopeFactory>(),
        new TableStatisticsMetrics(new WhizbangMetrics(_meters)),
        SchemaReadyGate.AlreadyReady(),
        logger ?? _logger,
        Options.Create(new TableStatisticsOptions { CollectionInterval = _interval }),
        _clock);
      _collector.CycleCompleted += () => Volatile.Read(ref _next).TrySetResult();

      var meter = _meters.CreatedMeters[0];
      _listener.InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter == meter && instrument.Name == "whizbang.table.unused_index_bytes") {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => _record(instrument, value, tags));
      _listener.Start();
    }

    public async Task StartAndRunCycleAsync() {
      var next = _nextCycle();
      await _collector.StartAsync(_stopping.Token);
      await next.WaitAsync(_wait);
    }

    public async Task RunNextCycleAsync() {
      await _clock.WaitForArmedAsync(_interval, _wait);
      var next = _nextCycle();
      _clock.Advance(_interval);
      await next.WaitAsync(_wait);
    }

    public List<CapturedLog> UnusedIndexLines() =>
      [.. _logger.Snapshot().Where(entry => entry.Message.Contains("not been scanned", StringComparison.Ordinal))];

    public List<(string Name, double Value, string? Table, string? Index)> Read() {
      _readings.Clear();
      _listener.RecordObservableInstruments();
      return [.. _readings];
    }

    private Task _nextCycle() {
      var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      Volatile.Write(ref _next, next);
      return next.Task;
    }

    private void _record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      string? table = null;
      string? index = null;
      foreach (var tag in tags) {
        if (tag.Key == "table_name") {
          table = tag.Value?.ToString();
        } else if (tag.Key == "index_name") {
          index = tag.Value?.ToString();
        }
      }
      _readings.Add((instrument.Name, value, table, index));
    }

    public async ValueTask DisposeAsync() {
      await _stopping.CancelAsync();
      await _collector.StopAsync(CancellationToken.None);
      _listener.Dispose();
      _meters.Dispose();
      _stopping.Dispose();
      await _container.DisposeAsync();
    }
  }

  /// <summary>A logger with every level off, as a host that filters out Information has.</summary>
  private sealed class SilentLogger : ILogger<TableStatisticsCollector> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      // Nothing is enabled, so nothing is written.
    }
  }

  /// <summary>Answers each cycle's unused indexes from a script; the last set repeats once the script runs out.</summary>
  private sealed class ScriptedProvider(IReadOnlyDictionary<string, UnusedIndex>[] cycles) : ITableStatisticsProvider {
    private int _calls;

    public Task<IReadOnlyDictionary<string, UnusedIndex>> GetUnusedIndexesAsync(CancellationToken ct = default) =>
      Task.FromResult(cycles[Math.Min(Interlocked.Increment(ref _calls) - 1, cycles.Length - 1)]);

    public Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());

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
