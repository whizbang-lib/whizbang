// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Branch backfill for <see cref="LoggingStartupStepObserver.OnStepCompletedAsync"/>: a failed step
/// that carries no reason still logs one, so the failure line never has a blank where the cause goes.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/BuiltInStartupObservers.cs</code-under-test>
[Category("Startup")]
public class BuiltInStartupObserversBranchCoverageTests {

  private sealed class CaptureLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  [Test]
  public async Task LoggingObserver_FailedStepWithNoReason_LogsTheNoReasonPlaceholderAsync() {
    var logger = new CaptureLogger();
    var observer = new LoggingStartupStepObserver(logger);

    await observer.OnStepCompletedAsync(
      new StartupStepResult("Migrate", StartupStepOutcome.Failed, TimeSpan.FromMilliseconds(12), Reason: null),
      CancellationToken.None);

    var (level, message) = logger.Entries.Single();
    await Assert.That(level).IsEqualTo(LogLevel.Warning);
    await Assert.That(message).Contains("Migrate");
    await Assert.That(message).Contains("(no reason)")
      .Because("a failure without a stated reason says so explicitly instead of leaving a blank in the log line");
  }
}
