using Microsoft.Extensions.Logging;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Coverage for <see cref="LoggingStartupStepObserver.OnStepWaitingAsync"/> — the hook that turns
/// a step blocked on a contended duty into an operator-visible log line instead of a silent hang.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/BuiltInStartupObservers.cs</code-under-test>
[Category("Startup")]
public class BuiltInStartupObserversCoverageTests {

  private sealed class _captureLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private static StartupStepDescriptor _descriptor(string name = "Migrate") => new() { Name = name };

  // If a step stuck waiting on a contended duty stopped narrating itself here, a long boot would
  // go quiet instead of telling an operator which step, which duty, and for how long it has been
  // blocked — exactly the "hang with no output" this hook exists to prevent (issue #494/#493).
  [Test]
  public async Task LoggingObserver_StepWaiting_LogsStepDutyWaitedAndRefusalDetailAsync() {
    var logger = new _captureLogger();
    var observer = new LoggingStartupStepObserver(logger);
    var context = new StartupStepWaitContext(
      _descriptor("Migrate"), "schema-owner", TimeSpan.FromSeconds(30), "held by another candidate");

    await observer.OnStepWaitingAsync(context, CancellationToken.None);

    var entry = logger.Entries.Single();
    await Assert.That(entry.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(entry.Message).Contains("Migrate");
    await Assert.That(entry.Message).Contains("schema-owner");
    await Assert.That(entry.Message).Contains("held by another candidate");
  }

  // A missing refusal detail is still a fact worth one line: if the null-coalescing default
  // silently dropped out instead of substituting a generic explanation, an operator would see a
  // blank hole in the log exactly where the elector's reason for refusing should be.
  [Test]
  public async Task LoggingObserver_StepWaiting_WithNoRefusalDetail_DefaultsToHeldByAnotherInstanceAsync() {
    var logger = new _captureLogger();
    var observer = new LoggingStartupStepObserver(logger);
    var context = new StartupStepWaitContext(_descriptor("Migrate"), "schema-owner", TimeSpan.FromSeconds(5), null);

    await observer.OnStepWaitingAsync(context, CancellationToken.None);

    await Assert.That(logger.Entries.Single().Message).Contains("held by another instance");
  }
}
