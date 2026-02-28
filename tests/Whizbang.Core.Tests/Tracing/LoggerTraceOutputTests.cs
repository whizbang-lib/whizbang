using Microsoft.Extensions.Logging;
using Rocks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for LoggerTraceOutput which writes traces via ILogger.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/LoggerTraceOutput.cs</code-under-test>
public class LoggerTraceOutputTests {
  #region Implementation Tests

  [Test]
  public async Task LoggerTraceOutput_ImplementsITraceOutputAsync() {
    // Arrange
    var type = typeof(LoggerTraceOutput);

    // Assert
    await Assert.That(typeof(ITraceOutput).IsAssignableFrom(type)).IsTrue();
  }

  [Test]
  public async Task LoggerTraceOutput_IsSealedAsync() {
    // Arrange
    var type = typeof(LoggerTraceOutput);

    // Assert
    await Assert.That(type.IsSealed).IsTrue();
  }

  #endregion

  #region BeginTrace Tests

  [Test]
  public async Task BeginTrace_ExplicitTrace_LogsAtInformationLevelAsync() {
    // Arrange
    var logMessages = new List<(LogLevel, string)>();
    var logger = _createTestLogger(logMessages);
    var output = new LoggerTraceOutput(logger);
    var context = _createContext() with { IsExplicit = true };

    // Act
    output.BeginTrace(context);

    // Assert - Explicit traces use Information level
    await Assert.That(logMessages.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(logMessages.Any(m => m.Item1 == LogLevel.Information)).IsTrue();
  }

  [Test]
  public async Task BeginTrace_NonExplicitTrace_LogsAtDebugLevelAsync() {
    // Arrange
    var logMessages = new List<(LogLevel, string)>();
    var logger = _createTestLogger(logMessages);
    var output = new LoggerTraceOutput(logger);
    var context = _createContext() with { IsExplicit = false };

    // Act
    output.BeginTrace(context);

    // Assert - Non-explicit traces use Debug level
    await Assert.That(logMessages.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(logMessages.Any(m => m.Item1 == LogLevel.Debug)).IsTrue();
  }

  [Test]
  public async Task BeginTrace_IncludesMessageTypeInLogAsync() {
    // Arrange
    var logMessages = new List<(LogLevel, string)>();
    var logger = _createTestLogger(logMessages);
    var output = new LoggerTraceOutput(logger);
    var context = _createContext() with { MessageType = "TestMessageType" };

    // Act
    output.BeginTrace(context);

    // Assert
    await Assert.That(logMessages.Any(m => m.Item2.Contains("TestMessageType"))).IsTrue();
  }

  [Test]
  public async Task BeginTrace_IncludesCorrelationIdInLogAsync() {
    // Arrange
    var logMessages = new List<(LogLevel, string)>();
    var logger = _createTestLogger(logMessages);
    var output = new LoggerTraceOutput(logger);
    var context = _createContext() with { CorrelationId = "corr-abc-123" };

    // Act
    output.BeginTrace(context);

    // Assert
    await Assert.That(logMessages.Any(m => m.Item2.Contains("corr-abc-123"))).IsTrue();
  }

  #endregion

  #region EndTrace Tests

  [Test]
  public async Task EndTrace_SuccessfulResult_LogsCompletionAsync() {
    // Arrange
    var logMessages = new List<(LogLevel, string)>();
    var logger = _createTestLogger(logMessages);
    var output = new LoggerTraceOutput(logger);
    var context = _createContext();
    var result = TraceResult.Completed(TimeSpan.FromMilliseconds(42));

    // Act
    output.EndTrace(context, result);

    // Assert
    await Assert.That(logMessages.Any(m => m.Item2.Contains("Completed"))).IsTrue();
  }

  [Test]
  public async Task EndTrace_FailedResult_LogsAtErrorLevelAsync() {
    // Arrange
    var logMessages = new List<(LogLevel, string)>();
    var logger = _createTestLogger(logMessages);
    var output = new LoggerTraceOutput(logger);
    var context = _createContext();
    var result = TraceResult.Failed(TimeSpan.FromMilliseconds(10), new InvalidOperationException("Test error"));

    // Act
    output.EndTrace(context, result);

    // Assert - Failures should log at Error level
    await Assert.That(logMessages.Any(m => m.Item1 == LogLevel.Error)).IsTrue();
    await Assert.That(logMessages.Any(m => m.Item2.Contains("Failed"))).IsTrue();
  }

  [Test]
  public async Task EndTrace_IncludesDurationInLogAsync() {
    // Arrange
    var logMessages = new List<(LogLevel, string)>();
    var logger = _createTestLogger(logMessages);
    var output = new LoggerTraceOutput(logger);
    var context = _createContext();
    var result = TraceResult.Completed(TimeSpan.FromMilliseconds(123.45));

    // Act
    output.EndTrace(context, result);

    // Assert - Duration should be in the log
    await Assert.That(logMessages.Any(m => m.Item2.Contains("123"))).IsTrue();
  }

  [Test]
  public async Task EndTrace_EarlyReturn_LogsEarlyReturnStatusAsync() {
    // Arrange
    var logMessages = new List<(LogLevel, string)>();
    var logger = _createTestLogger(logMessages);
    var output = new LoggerTraceOutput(logger);
    var context = _createContext();
    var result = TraceResult.EarlyReturn(TimeSpan.FromMilliseconds(1));

    // Act
    output.EndTrace(context, result);

    // Assert
    await Assert.That(logMessages.Any(m => m.Item2.Contains("EarlyReturn"))).IsTrue();
  }

  #endregion

  #region Helper Methods

  private static TraceContext _createContext() {
    return new TraceContext {
      MessageId = Guid.NewGuid(),
      CorrelationId = "test-correlation",
      MessageType = "TestMessage",
      Component = TraceComponents.Handlers,
      Verbosity = TraceVerbosity.Normal,
      StartTime = DateTimeOffset.UtcNow
    };
  }

  private static TestLogger _createTestLogger(List<(LogLevel, string)> logMessages) {
    return new TestLogger(logMessages);
  }

  private sealed class TestLogger : ILogger<LoggerTraceOutput> {
    private readonly List<(LogLevel, string)> _messages;

    public TestLogger(List<(LogLevel, string)> messages) {
      _messages = messages;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      _messages.Add((logLevel, formatter(state, exception)));
    }
  }

  #endregion
}
