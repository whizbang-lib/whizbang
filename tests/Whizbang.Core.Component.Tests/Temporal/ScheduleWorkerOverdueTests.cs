// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.Temporal;
using Whizbang.Core.Tests.Observability;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// #2575: a due schedule that stays unclaimed past <see cref="TemporalOptions.OverdueWarningMilliseconds"/> is never
/// silent. Each drain pass of <see cref="ScheduleWorker"/> reads the Active schedules overdue by more than that bound
/// (<see cref="IScheduleClaimer.GetOverdueSchedulesAsync"/>), publishes the count and the oldest one's age on
/// <see cref="TemporalMetrics"/>, and logs a warning naming the oldest when the overdue set appears or changes, and
/// once when it clears. The schedules that never fired went unnoticed for days because nothing read this.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine#a-schedule-that-does-not-fire-is-reported</docs>
public class ScheduleWorkerOverdueTests {
  private const int OVERDUE_EVENT_ID = 5;
  private const int OVERDUE_CLEARED_EVENT_ID = 6;
  private const int OVERDUE_READ_FAILED_EVENT_ID = 7;
  private const string STUCK_KEY = "stuck-schedule";

  private static readonly ScheduleOverdueReading _oneStuck =
    new(1, TimeSpan.FromMinutes(10), Guid.CreateVersion7(), STUCK_KEY);

  [Test]
  public async Task CheckOverdue_AScheduleOverdueBeyondTheBound_WarnsOnce_AndPublishesItAsync() {
    using var host = new Host();
    host.Claimer.Reading = _oneStuck;

    var first = await host.Worker.CheckOverdueOnceAsync();
    var second = await host.Worker.CheckOverdueOnceAsync();

    await Assert.That(first).IsEqualTo(_oneStuck);
    await Assert.That(second).IsEqualTo(_oneStuck);
    await Assert.That(host.Events(OVERDUE_EVENT_ID)).Count().IsEqualTo(1)
      .Because("the warning marks the overdue set appearing; repeating it every backstop tick buries it");
    var warning = host.Logger.Collector.GetSnapshot().Single(r => r.Id.Id == OVERDUE_EVENT_ID);
    await Assert.That(warning.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(warning.Message).Contains(STUCK_KEY)
      .Because("the operator needs to know which schedule is stuck, not only that one is");
    await Assert.That(host.ReadGauge(TemporalMetrics.OVERDUE_COUNT)).IsEquivalentTo(new[] { 1.0 });
    await Assert.That(host.ReadGauge(TemporalMetrics.OVERDUE_OLDEST_AGE_SECONDS)).IsEquivalentTo(new[] { 600.0 });
  }

  [Test]
  public async Task CheckOverdue_AsksTheClaimerWithTheConfiguredBoundAsync() {
    using var host = new Host(new TemporalOptions { OverdueWarningMilliseconds = 90_000 });

    _ = await host.Worker.CheckOverdueOnceAsync();

    await Assert.That(host.Claimer.Bounds).IsEquivalentTo(new[] { TimeSpan.FromSeconds(90) });
  }

  [Test]
  public async Task CheckOverdue_WhenTheOverdueSetChanges_WarnsAgainAsync() {
    using var host = new Host();
    host.Claimer.Reading = _oneStuck;
    _ = await host.Worker.CheckOverdueOnceAsync();

    host.Claimer.Reading = _oneStuck with { Count = 2 };
    _ = await host.Worker.CheckOverdueOnceAsync();
    host.Claimer.Reading = _oneStuck with { Count = 2, OldestScheduleId = Guid.CreateVersion7() };
    _ = await host.Worker.CheckOverdueOnceAsync();

    await Assert.That(host.Events(OVERDUE_EVENT_ID)).Count().IsEqualTo(3)
      .Because("another schedule falling behind, or a different oldest one, is news");
  }

  [Test]
  public async Task CheckOverdue_WhenTheOverdueSetClears_SaysSoOnceAsync() {
    using var host = new Host();
    host.Claimer.Reading = _oneStuck;
    _ = await host.Worker.CheckOverdueOnceAsync();

    host.Claimer.Reading = ScheduleOverdueReading.None;
    _ = await host.Worker.CheckOverdueOnceAsync();
    _ = await host.Worker.CheckOverdueOnceAsync();

    await Assert.That(host.Events(OVERDUE_CLEARED_EVENT_ID)).Count().IsEqualTo(1);
    await Assert.That(host.ReadGauge(TemporalMetrics.OVERDUE_COUNT)).IsEquivalentTo(new[] { 0.0 });
    await Assert.That(host.ReadGauge(TemporalMetrics.OVERDUE_OLDEST_AGE_SECONDS)).IsEquivalentTo(new[] { 0.0 });
  }

  [Test]
  public async Task CheckOverdue_NothingOverdue_LogsNothing_AndPublishesZeroAsync() {
    using var host = new Host();

    var reading = await host.Worker.CheckOverdueOnceAsync();

    await Assert.That(reading).IsEqualTo(ScheduleOverdueReading.None);
    await Assert.That(host.Logger.Collector.Count).IsEqualTo(0);
    await Assert.That(host.ReadGauge(TemporalMetrics.OVERDUE_COUNT)).IsEquivalentTo(new[] { 0.0 });
  }

  [Test]
  public async Task CheckOverdue_AnUnknownReading_PublishesNothing_AndKeepsTheLastStateAsync() {
    // The provider answers null when it has no connection yet: that is not "nothing overdue".
    using var host = new Host();
    host.Claimer.Reading = _oneStuck;
    _ = await host.Worker.CheckOverdueOnceAsync();

    host.Claimer.Reading = null;
    var unknown = await host.Worker.CheckOverdueOnceAsync();
    host.Claimer.Reading = _oneStuck;
    _ = await host.Worker.CheckOverdueOnceAsync();

    await Assert.That(unknown).IsNull();
    await Assert.That(host.Events(OVERDUE_CLEARED_EVENT_ID)).IsEmpty();
    await Assert.That(host.Events(OVERDUE_EVENT_ID)).Count().IsEqualTo(1)
      .Because("an unknown reading in between is neither a clear nor a change");
  }

  [Test]
  public async Task CheckOverdue_AnUnknownReading_LeavesTheGaugesEmptyAsync() {
    using var host = new Host();
    host.Claimer.Reading = null;

    _ = await host.Worker.CheckOverdueOnceAsync();

    await Assert.That(host.ReadGauge(TemporalMetrics.OVERDUE_COUNT)).IsEmpty()
      .Because("a gauge reading zero while the count is unknown would claim health it has not measured");
  }

  [Test]
  public async Task CheckOverdue_TheReadFails_LogsIt_AndReturnsNullAsync() {
    using var host = new Host();
    host.Claimer.Failure = new InvalidOperationException("read blew up");

    var reading = await host.Worker.CheckOverdueOnceAsync();

    await Assert.That(reading).IsNull();
    var failure = host.Logger.Collector.GetSnapshot().Single(r => r.Id.Id == OVERDUE_READ_FAILED_EVENT_ID);
    await Assert.That(failure.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(failure.Exception).IsSameReferenceAs(host.Claimer.Failure);
  }

  [Test]
  public async Task CheckOverdue_ACancellationOfTheCallersToken_PropagatesAsync() {
    using var host = new Host();
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();
    host.Claimer.Failure = new OperationCanceledException(canceled.Token);

    await Assert.That(async () => await host.Worker.CheckOverdueOnceAsync(canceled.Token))
      .Throws<OperationCanceledException>();
    await Assert.That(host.Events(OVERDUE_READ_FAILED_EVENT_ID)).IsEmpty()
      .Because("a shutdown is not a failed read");
  }

  [Test]
  public async Task CheckOverdue_ACancellationThatIsNotTheCallers_IsAFailedReadAsync() {
    // A command timeout surfaces as a cancellation the caller never asked for.
    using var host = new Host();
    host.Claimer.Failure = new OperationCanceledException();

    await Assert.That(await host.Worker.CheckOverdueOnceAsync()).IsNull();
    await Assert.That(host.Events(OVERDUE_READ_FAILED_EVENT_ID)).Count().IsEqualTo(1);
  }

  [Test]
  public async Task CheckOverdue_TurnedOff_DoesNotReadAsync() {
    using var host = new Host(new TemporalOptions { OverdueWarningMilliseconds = 0 });

    await Assert.That(await host.Worker.CheckOverdueOnceAsync()).IsNull();
    await Assert.That(host.Claimer.Bounds).IsEmpty();
  }

  [Test]
  public async Task CheckOverdue_NoClaimer_ReturnsNullAsync() {
    using var host = new Host(withClaimer: false);

    await Assert.That(await host.Worker.CheckOverdueOnceAsync()).IsNull();
  }

  [Test]
  public async Task CheckOverdue_WithNoMetrics_StillWarnsAsync() {
    var claimer = new OverdueClaimer { Reading = _oneStuck };
    await using var services = new ServiceCollection().AddSingleton<IScheduleClaimer>(claimer).BuildServiceProvider();
    var logger = new FakeLogger<ScheduleWorker>();
    using var worker = new ScheduleWorker(
      services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new TemporalOptions()), logger,
      NullLoggerFactory.Instance, NullSignalBus.Instance);

    _ = await worker.CheckOverdueOnceAsync();

    await Assert.That(logger.Collector.GetSnapshot().Count(r => r.Id.Id == OVERDUE_EVENT_ID)).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task Execute_EachDrainPass_ChecksForOverdueSchedulesAsync(CancellationToken ct) {
    using var host = new Host();
    host.Claimer.Reading = _oneStuck;

    await host.Worker.StartAsync(CancellationToken.None);
    await host.Claimer.FirstOverdueRead.WaitAsync(ct);
    await host.Worker.StopAsync(CancellationToken.None);

    await Assert.That(host.Claimer.Bounds).IsNotEmpty()
      .Because("the drain loop is what runs on every instance, so it is where a stuck schedule gets noticed");
  }

  [Test]
  public async Task TemporalMetrics_RefusesANullHolderAsync() {
    await Assert.That(() => new TemporalMetrics(null!)).Throws<ArgumentNullException>();
  }

  // --- helpers ---------------------------------------------------------------------------------

  /// <summary>A claimer that answers the overdue reading it is given and records each bound it is asked for.</summary>
  private sealed class OverdueClaimer : IScheduleClaimer {
    private readonly TaskCompletionSource _firstOverdueRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<TimeSpan> _bounds = [];

    public ScheduleOverdueReading? Reading { get; set; } = ScheduleOverdueReading.None;
    public Exception? Failure { get; set; }
    public Task FirstOverdueRead => _firstOverdueRead.Task;
    public IReadOnlyList<TimeSpan> Bounds { get { lock (_bounds) { return [.. _bounds]; } } }

    public Task<int> ClaimDueSchedulesAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult(0);

    public Task<DateTimeOffset?> GetNextFireTimeAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<DateTimeOffset?>(null);

    public Task<ScheduleOverdueReading?> GetOverdueSchedulesAsync(TimeSpan bound, CancellationToken cancellationToken = default) {
      lock (_bounds) {
        _bounds.Add(bound);
      }
      _firstOverdueRead.TrySetResult();
      return Failure is { } failure ? Task.FromException<ScheduleOverdueReading?>(failure) : Task.FromResult(Reading);
    }
  }

  /// <summary>The worker with a listener on its two gauges.</summary>
  private sealed class Host : IDisposable {
    private readonly ServiceProvider _container;
    private readonly TestMeterFactory _factory = new();
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, List<double>> _readings = [];

    public Host(TemporalOptions? options = null, bool withClaimer = true) {
      var services = new ServiceCollection();
      if (withClaimer) {
        services.AddSingleton<IScheduleClaimer>(Claimer);
      }
      _container = services.BuildServiceProvider();
      Worker = new ScheduleWorker(
        _container.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(options ?? new TemporalOptions()),
        Logger,
        NullLoggerFactory.Instance,
        NullSignalBus.Instance,
        new Microsoft.Extensions.Time.Testing.FakeTimeProvider(),
        metrics: new TemporalMetrics(new WhizbangMetrics(_factory)));

      var meter = _factory.CreatedMeters[0];
      _listener.InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter == meter) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => {
        if (!_readings.TryGetValue(instrument.Name, out var list)) {
          _readings[instrument.Name] = list = [];
        }
        list.Add(value);
      });
      _listener.Start();
    }

    public OverdueClaimer Claimer { get; } = new();
    public FakeLogger<ScheduleWorker> Logger { get; } = new();
    public ScheduleWorker Worker { get; }

    public IReadOnlyList<int> Events(int eventId) =>
      [.. Logger.Collector.GetSnapshot().Where(r => r.Id.Id == eventId).Select(r => r.Id.Id)];

    public List<double> ReadGauge(string name) {
      _readings.Clear();
      _listener.RecordObservableInstruments();
      return _readings.TryGetValue(name, out var list) ? [.. list] : [];
    }

    public void Dispose() {
      Worker.Dispose();
      _listener.Dispose();
      _factory.Dispose();
      _container.Dispose();
    }
  }
}
