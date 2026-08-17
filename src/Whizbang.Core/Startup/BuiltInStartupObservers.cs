using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Startup;

/// <summary>
/// The framework's own log record of the pipeline — written as an ordinary
/// <see cref="IStartupStepObserver"/>, so the built-in path and the consumer path are the same
/// path. Completed steps log at Information, failures at Warning with the reason; the reason on a
/// skip is always carried, because "found nothing to do" and "could not look" are different facts
/// and the log is the one place people grep during a slow boot.
/// </summary>
/// <docs>operations/startup/startup-pipeline#hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupBuiltInObserversTests.cs</tests>
public sealed partial class LoggingStartupStepObserver : IStartupStepObserver {
  private readonly ILogger _logger;

  /// <summary>Creates the observer over the given logger.</summary>
  public LoggingStartupStepObserver(ILogger logger) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <inheritdoc />
  public ValueTask OnStepStartingAsync(StartupStepContext context, CancellationToken cancellationToken) {
    LogStarting(_logger, context.Descriptor.Name);
    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask OnStepCompletedAsync(StartupStepResult result, CancellationToken cancellationToken) {
    if (result.Outcome == StartupStepOutcome.Failed) {
      LogFailed(_logger, result.Name, result.Duration.TotalMilliseconds, result.Reason ?? "(no reason)");
    } else {
      LogCompleted(_logger, result.Name, result.Outcome, result.Duration.TotalMilliseconds,
        result.Reason is null ? "" : $" — {result.Reason}");
    }
    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask OnPipelineCompletedAsync(StartupSummary summary, CancellationToken cancellationToken) {
    var failed = 0;
    var skipped = 0;
    foreach (var result in summary.Results) {
      if (result.Outcome == StartupStepOutcome.Failed) { failed++; }
      if (result.Outcome == StartupStepOutcome.Skipped) { skipped++; }
    }
    LogPipelineCompleted(_logger, summary.Results.Count, skipped, failed);
    return ValueTask.CompletedTask;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
    Message = "Startup step {Step} starting")]
  static partial void LogStarting(ILogger logger, string step);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "Startup step {Step}: {Outcome} in {DurationMs:F0}ms{Reason}")]
  static partial void LogCompleted(ILogger logger, string step, StartupStepOutcome outcome, double durationMs, string reason);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "Startup step {Step}: FAILED in {DurationMs:F0}ms — {Reason}")]
  static partial void LogFailed(ILogger logger, string step, double durationMs, string reason);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information,
    Message = "Startup pipeline completed: {Total} step(s), {Skipped} skipped, {Failed} failed")]
  static partial void LogPipelineCompleted(ILogger logger, int total, int skipped, int failed);
}

/// <summary>
/// Feeds each step completion into <see cref="StartupPipelineMetrics"/> — the other built-in
/// observer, riding the same seam as everything else.
/// </summary>
/// <docs>operations/startup/startup-pipeline#hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupBuiltInObserversTests.cs</tests>
public sealed class MetricsStartupStepObserver : IStartupStepObserver {
  private readonly StartupPipelineMetrics _metrics;

  /// <summary>Creates the observer over the given instruments.</summary>
  public MetricsStartupStepObserver(StartupPipelineMetrics metrics) {
    ArgumentNullException.ThrowIfNull(metrics);
    _metrics = metrics;
  }

  /// <inheritdoc />
  public ValueTask OnStepStartingAsync(StartupStepContext context, CancellationToken cancellationToken)
    => ValueTask.CompletedTask;

  /// <inheritdoc />
  public ValueTask OnStepCompletedAsync(StartupStepResult result, CancellationToken cancellationToken) {
    _metrics.Record(result);
    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask OnPipelineCompletedAsync(StartupSummary summary, CancellationToken cancellationToken)
    => ValueTask.CompletedTask;
}
