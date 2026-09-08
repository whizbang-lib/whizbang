using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The automatic half of the retention adoption gate (issue #712). Migration 104 withholds the
/// enrolled reap until a perspective is acknowledged, and nothing in the framework acknowledged, so
/// a <c>[RowTtl]</c> declaration never started reaping. The maintenance cycle now adopts every
/// enrolled, unacknowledged perspective immediately before the enrolled reap, logs the backlog it
/// found, and records it on the maintenance meter; the option restores the manual gate.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
/// <docs>fundamentals/perspectives/row-retention</docs>
[Category("Core")]
[Category("Workers")]
public class MaintenanceWorkerRetentionAdoptionTests {
  private const string PERSPECTIVE = "TestApp.Orders+SummaryModel";
  private static readonly string[] _adoptThenReap = ["adopt", "reap"];
  private static readonly string[] _reapOnly = ["reap"];

  [Test]
  public async Task AutoAcknowledge_DefaultsToOnAsync() {
    await Assert.That(new PerspectiveRowRetentionOptions().AutoAcknowledge).IsTrue()
      .Because("a declared window must be in force on the next deploy without a manual step");
  }

  [Test]
  public async Task Adoption_RunsImmediatelyBeforeTheEnrolledReap_AndLogsEachPerspectiveAsync() {
    var coord = new AdoptionCoordinator { Adoptions = [new PerspectiveRetentionAdoption(PERSPECTIVE, 3600)] };
    var (worker, logger) = _build(coord, batchSize: 250);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Calls).IsEquivalentTo(_adoptThenReap)
      .Because("the adoption opens the gate in the same cycle the reap starts draining");
    var line = logger.Snapshot().SingleOrDefault(e => e.Message.Contains("retention adopted", StringComparison.OrdinalIgnoreCase));
    await Assert.That(line).IsNotNull().Because("the first day of a retroactive window must be visible without anyone acting");
    await Assert.That(line!.Level).IsEqualTo(LogLevel.Information);
    await Assert.That(line.Message).Contains(PERSPECTIVE);
    await Assert.That(line.Message).Contains("3600");
    await Assert.That(line.Message).Contains("250").Because("the operator reads how fast the backlog drains from the same line");
  }

  [Test]
  public async Task Adoption_IsOn_WhenNoRetentionOptionsAreRegisteredAsync() {
    var coord = new AdoptionCoordinator();
    var (worker, _) = _build(coord, registerOptions: false);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Calls).Contains("adopt").Because("turnkey means the default host adopts");
  }

  [Test]
  public async Task Adoption_Off_LeavesTheGateToTheOperatorAsync() {
    var coord = new AdoptionCoordinator { Adoptions = [new PerspectiveRetentionAdoption(PERSPECTIVE, 3600)] };
    var (worker, logger) = _build(coord, autoAcknowledge: false);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Calls).IsEquivalentTo(_reapOnly)
      .Because("AutoAcknowledge=false is exactly today's behavior: report, remove nothing, wait for the acknowledge call");
    await Assert.That(logger.Snapshot().Any(e => e.Message.Contains("retention adopted", StringComparison.OrdinalIgnoreCase))).IsFalse();
  }

  [Test]
  public async Task Adoption_NothingToAdopt_LogsNothingAsync() {
    var coord = new AdoptionCoordinator();
    var (worker, logger) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot().Any(e => e.Message.Contains("retention adopted", StringComparison.OrdinalIgnoreCase))).IsFalse()
      .Because("an acknowledged perspective produces no row, so the steady state is silent");
  }

  [Test]
  public async Task Adoption_RecordsTheBacklogOnTheMaintenanceMeter_TaggedByPerspectiveAsync() {
    var metrics = new MaintenanceMetrics(new WhizbangMetrics());
    var readings = new List<(string Name, long Value, string? Perspective)>();
    using var listener = new MeterListener();
    // Pin to THIS test's instrument, not the meter name: parallel tests build MaintenanceMetrics
    // on the same meter name, and a passive counter reports every instance's series at collection.
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.RetentionAdopted.Instrument)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
      string? perspective = null;
      foreach (var tag in tags) {
        if (tag.Key == "perspective") {
          perspective = tag.Value?.ToString();
        }
      }
      lock (readings) {
        readings.Add((instrument.Name, value, perspective));
      }
    });
    listener.Start();
    var coord = new AdoptionCoordinator { Adoptions = [new PerspectiveRetentionAdoption(PERSPECTIVE, 3600)] };
    var (worker, _) = _build(coord, metrics: metrics);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    // Passive counter: the perspective-tagged series reports its cumulative backlog at collection.
    listener.RecordObservableInstruments();

    await Assert.That(readings.Any(r => r.Name == "whizbang.maintenance.retention_adopted" && r.Value == 3600 && r.Perspective == PERSPECTIVE)).IsTrue()
      .Because("the backlog is the number a dashboard shows for the first day of a retroactive window");
  }

  // -------------------------------------------------------------------------------------------

  private static (MaintenanceWorker Worker, CapturingLogger Logger) _build(
      AdoptionCoordinator coord, bool? autoAcknowledge = null, bool registerOptions = true,
      int batchSize = 5000, MaintenanceMetrics? metrics = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    if (registerOptions) {
      services.AddSingleton(Options.Create(new PerspectiveRowRetentionOptions { AutoAcknowledge = autoAcknowledge ?? true }));
    }
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new CapturingLogger();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, RowReapBatchSize = batchSize }),
      logger,
      metrics);
    return (worker, logger);
  }

  private sealed record LogEntry(LogLevel Level, string Message);

  private sealed class CapturingLogger : ILogger<MaintenanceWorker> {
    private readonly List<LogEntry> _entries = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_entries) {
        _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
      }
    }
    public List<LogEntry> Snapshot() {
      lock (_entries) {
        return [.. _entries];
      }
    }
    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  private sealed class AdoptionCoordinator : IWorkCoordinator {
    public IReadOnlyList<PerspectiveRetentionAdoption> Adoptions { get; init; } = [];
    public List<string> Calls { get; } = [];

    public Task<IReadOnlyList<PerspectiveRetentionAdoption>> AdoptEnrolledPerspectiveRetentionAsync(
        CancellationToken ct = default) {
      Calls.Add("adopt");
      return Task.FromResult(Adoptions);
    }

    public Task<PerspectiveRowReapResult> ReapEnrolledPerspectiveRowsAsync(
        int batchSize = 5000, CancellationToken ct = default) {
      Calls.Add("reap");
      return Task.FromResult(new PerspectiveRowReapResult(0, "ok"));
    }

    public Task<bool> TryClaimRowCapSweepAsync(TimeSpan claimWindow, CancellationToken ct = default)
      => Task.FromResult(false);
    public Task<bool> TryClaimSettledFoldSweepAsync(TimeSpan claimWindow, CancellationToken ct = default)
      => Task.FromResult(false);

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] m, int partitionCount, CancellationToken ct = default)
      => Task.CompletedTask;
  }
}
