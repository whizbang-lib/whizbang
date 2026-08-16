using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The framework's own logging and metrics ride the same observer seam consumers get — one path,
/// not a privileged internal one and a lesser public one. These tests hold the built-ins to the
/// seam's contract and to stable instrument names.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/StartupPipelineMetrics.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/BuiltInStartupObservers.cs</code-under-test>
[Category("Startup")]
public class StartupBuiltInObserversTests {

  private sealed class _captureLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private static StartupStepResult _result(
      string name = "Migrate",
      StartupStepOutcome outcome = StartupStepOutcome.Completed,
      string? reason = null) =>
    new(name, outcome, TimeSpan.FromMilliseconds(42), reason);

  // ── metrics ─────────────────────────────────────────────────────────────

  [Test]
  public async Task Instruments_HaveStableNamesAsync() {
    var metrics = new StartupPipelineMetrics(new WhizbangMetrics());

    await Assert.That(metrics.StepDuration.Name).IsEqualTo("whizbang.startup.step_duration");
    await Assert.That(metrics.StepDuration.Unit).IsEqualTo("ms");
    await Assert.That(metrics.StepOutcomes.Name).IsEqualTo("whizbang.startup.step_outcomes");
  }

  [Test]
  public async Task MetricsObserver_OnStepCompleted_RecordsDurationAndOutcomeTaggedByStepAsync() {
    var metrics = new StartupPipelineMetrics(new WhizbangMetrics());
    var durations = new List<(double Value, string? Step, string? Outcome)>();
    var outcomes = new List<(long Value, string? Step, string? Outcome)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == StartupPipelineMetrics.METER_NAME) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<double>((_, value, tags, _) => {
      string? step = null, outcome = null;
      foreach (var tag in tags) {
        if (tag.Key == "step") { step = tag.Value as string; }
        if (tag.Key == "outcome") { outcome = tag.Value as string; }
      }
      durations.Add((value, step, outcome));
    });
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      string? step = null, outcome = null;
      foreach (var tag in tags) {
        if (tag.Key == "step") { step = tag.Value as string; }
        if (tag.Key == "outcome") { outcome = tag.Value as string; }
      }
      outcomes.Add((value, step, outcome));
    });
    listener.Start();

    var observer = new MetricsStartupStepObserver(metrics);
    await observer.OnStepCompletedAsync(_result("Migrate", StartupStepOutcome.Skipped, "done elsewhere"), CancellationToken.None);

    await Assert.That(durations.Count).IsEqualTo(1);
    await Assert.That(durations[0].Value).IsEqualTo(42.0);
    await Assert.That(durations[0].Step).IsEqualTo("Migrate");
    await Assert.That(durations[0].Outcome).IsEqualTo("Skipped");
    await Assert.That(outcomes.Count).IsEqualTo(1);
    await Assert.That(outcomes[0].Step).IsEqualTo("Migrate");
  }

  // ── logging ─────────────────────────────────────────────────────────────

  [Test]
  public async Task LoggingObserver_CompletedStep_LogsNameOutcomeAndDurationAsync() {
    var logger = new _captureLogger();
    var observer = new LoggingStartupStepObserver(logger);

    await observer.OnStepCompletedAsync(_result("Migrate"), CancellationToken.None);

    var entry = logger.Entries.Single();
    await Assert.That(entry.Level).IsEqualTo(LogLevel.Information);
    await Assert.That(entry.Message).Contains("Migrate");
    await Assert.That(entry.Message).Contains("Completed");
  }

  // A failed step is the record an operator greps for; it carries the reason and logs louder.
  [Test]
  public async Task LoggingObserver_FailedStep_LogsWarningWithTheReasonAsync() {
    var logger = new _captureLogger();
    var observer = new LoggingStartupStepObserver(logger);

    await observer.OnStepCompletedAsync(
      _result("Migrate", StartupStepOutcome.Failed, "schema unreachable"), CancellationToken.None);

    var entry = logger.Entries.Single();
    await Assert.That(entry.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(entry.Message).Contains("schema unreachable");
  }

  // "Skipped(reason)" is a different fact from "Completed" — the log must carry the reason, or the
  // silent-skip class this pipeline exists to expose stays invisible in the one place people look.
  [Test]
  public async Task LoggingObserver_SkippedStep_LogsTheReasonAsync() {
    var logger = new _captureLogger();
    var observer = new LoggingStartupStepObserver(logger);

    await observer.OnStepCompletedAsync(
      _result("Repair", StartupStepOutcome.Skipped, "no origins known yet"), CancellationToken.None);

    await Assert.That(logger.Entries.Single().Message).Contains("no origins known yet");
  }

  [Test]
  public async Task LoggingObserver_PipelineCompleted_SummarizesCountsAsync() {
    var logger = new _captureLogger();
    var observer = new LoggingStartupStepObserver(logger);

    await observer.OnPipelineCompletedAsync(new StartupSummary([
      _result("Migrate"),
      _result("Repair", StartupStepOutcome.Skipped, "cold"),
      _result("Provision", StartupStepOutcome.Failed, "broker down"),
    ]), CancellationToken.None);

    var entry = logger.Entries.Single();
    await Assert.That(entry.Message).Contains("3");
    await Assert.That(entry.Message).Contains("1 failed");
  }
}
