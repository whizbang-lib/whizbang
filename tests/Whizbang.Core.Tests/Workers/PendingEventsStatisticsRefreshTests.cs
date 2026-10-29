// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The pending-events gauge refresh reports a failed statistics query instead of swallowing it (#1865).
/// </summary>
/// <remarks>
/// The once-a-minute refresh behind <c>whizbang.perspective.pending_events</c> used to discard every failure, so a
/// timeout, a missing permission or a function a partial migration never created froze the gauge at its last value
/// with nothing to explain it. A failure now warns once per streak, naming the query and the stale gauge, counts every
/// failed refresh, and reports when the gauge is current again.
/// </remarks>
[Category("Unit")]
public class PendingEventsStatisticsRefreshTests {
  private const string PENDING_EVENTS = "whizbang.perspective.pending_events";
  private const string REFRESH_FAILURES = "whizbang.perspective.pending_events_refresh_failures";

  [Test]
  public async Task Success_SetsTheGauge_AndLogsNothingAsync() {
    using var fx = new Fixture();
    fx.Results.Enqueue(42);

    await fx.Refresh.RefreshAsync(CancellationToken.None);

    await Assert.That(fx.Meters.GetByName(PENDING_EVENTS).Single().Value).IsEqualTo(42d);
    await Assert.That(fx.Logs).IsEmpty();
  }

  [Test]
  public async Task Failure_WarnsNamingTheQueryAndTheStaleGauge_KeepsTheLastValue_AndCountsItAsync() {
    using var fx = new Fixture();
    fx.Results.Enqueue(42);
    fx.Results.Enqueue(new TimeoutException("statement timeout"));

    await fx.Refresh.RefreshAsync(CancellationToken.None);
    await fx.Refresh.RefreshAsync(CancellationToken.None);

    var warning = fx.Logs.Single();
    await Assert.That(warning.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(warning.Message).Contains("GatherStatisticsAsync");
    await Assert.That(warning.Message).Contains(PENDING_EVENTS);
    await Assert.That(warning.Exception).IsTypeOf<TimeoutException>();
    await Assert.That(fx.Meters.GetByName(PENDING_EVENTS).Single().Value).IsEqualTo(42d)
      .Because("the gauge keeps the last value it read, which is why the failure has to be visible");
    await Assert.That(fx.Meters.GetByName(REFRESH_FAILURES).Sum(m => m.Value)).IsEqualTo(1d);
  }

  [Test]
  public async Task FailureStreak_WarnsOnce_ThenLogsAtDebug_AndCountsEveryFailureAsync() {
    using var fx = new Fixture();
    fx.Results.Enqueue(new TimeoutException("first"));
    fx.Results.Enqueue(new TimeoutException("second"));
    fx.Results.Enqueue(new TimeoutException("third"));

    for (var i = 0; i < 3; i++) {
      await fx.Refresh.RefreshAsync(CancellationToken.None);
    }

    await Assert.That(fx.Logs.Select(l => l.Level)).IsEquivalentTo([LogLevel.Warning, LogLevel.Debug, LogLevel.Debug])
      .Because("one warning per streak; a refresh every minute that keeps failing is not a warning every minute");
    await Assert.That(fx.Logs[2].Message).Contains("3");
    await Assert.That(fx.Logs[2].Exception).IsTypeOf<TimeoutException>();
    await Assert.That(fx.Meters.GetByName(REFRESH_FAILURES).Sum(m => m.Value)).IsEqualTo(3d);
  }

  [Test]
  public async Task RecoveryAfterAStreak_ReportsTheGaugeCurrentAgain_AndTheNextFailureWarnsAgainAsync() {
    using var fx = new Fixture();
    fx.Results.Enqueue(new TimeoutException("first"));
    fx.Results.Enqueue(new TimeoutException("second"));
    fx.Results.Enqueue(7);
    fx.Results.Enqueue(new TimeoutException("again"));

    for (var i = 0; i < 4; i++) {
      await fx.Refresh.RefreshAsync(CancellationToken.None);
    }

    await Assert.That(fx.Logs.Select(l => l.Level)).IsEquivalentTo(
      [LogLevel.Warning, LogLevel.Debug, LogLevel.Information, LogLevel.Warning]);
    await Assert.That(fx.Logs[2].Message).Contains("2")
      .Because("the recovery says how many refreshes the gauge missed");
    await Assert.That(fx.Logs[2].Message).Contains(PENDING_EVENTS);
    await Assert.That(fx.Meters.GetByName(PENDING_EVENTS).Single().Value).IsEqualTo(7d);
  }

  [Test]
  public async Task Cancellation_IsNotAFailure_AndPropagatesAsync() {
    using var fx = new Fixture();
    fx.Results.Enqueue(new OperationCanceledException());

    await Assert.That(async () => await fx.Refresh.RefreshAsync(CancellationToken.None)).Throws<OperationCanceledException>();

    await Assert.That(fx.Logs).IsEmpty();
    await Assert.That(fx.Meters.GetByName(REFRESH_FAILURES).Sum(m => m.Value)).IsEqualTo(0d);
  }

  [Test]
  public async Task WithoutMetrics_SuccessAndFailureStillCompleteAsync() {
    var logs = new List<(LogLevel Level, string Message, Exception? Exception)>();
    var results = new Queue<object>([new TimeoutException("down"), 3L]);
    var refresh = new PendingEventsStatisticsRefresh(_ => _next(results), metrics: null, new ListLogger(logs));

    await refresh.RefreshAsync(CancellationToken.None);
    await refresh.RefreshAsync(CancellationToken.None);

    await Assert.That(logs.Select(l => l.Level)).IsEquivalentTo([LogLevel.Warning, LogLevel.Information]);
  }

  private static Task<long> _next(Queue<object> results) => results.Dequeue() switch {
    Exception ex => Task.FromException<long>(ex),
    var value => Task.FromResult(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
  };

  private sealed class Fixture : IDisposable {
    private readonly TestMeterFactory _meterFactory = new();

    public Fixture() {
      var metrics = new PerspectiveMetrics(new WhizbangMetrics(_meterFactory));
      Meters = new MetricAssertionHelper(_meterFactory.CreatedMeters[0]);
      Refresh = new PendingEventsStatisticsRefresh(_ => _next(Results), metrics, new ListLogger(Logs));
    }

    public Queue<object> Results { get; } = new();
    public List<(LogLevel Level, string Message, Exception? Exception)> Logs { get; } = [];
    public MetricAssertionHelper Meters { get; }
    public PendingEventsStatisticsRefresh Refresh { get; }

    public void Dispose() {
      Meters.Dispose();
      _meterFactory.Dispose();
    }
  }

  private sealed class ListLogger(List<(LogLevel Level, string Message, Exception? Exception)> sink) : ILogger {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      sink.Add((logLevel, formatter(state, exception), exception));
  }
}
