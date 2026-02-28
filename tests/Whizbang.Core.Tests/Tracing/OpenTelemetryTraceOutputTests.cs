using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for OpenTelemetryTraceOutput which emits spans via System.Diagnostics.ActivitySource.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/OpenTelemetryTraceOutput.cs</code-under-test>
public class OpenTelemetryTraceOutputTests {
  #region Implementation Tests

  [Test]
  public async Task OpenTelemetryTraceOutput_ImplementsITraceOutputAsync() {
    // Arrange
    var type = typeof(OpenTelemetryTraceOutput);

    // Assert
    await Assert.That(typeof(ITraceOutput).IsAssignableFrom(type)).IsTrue();
  }

  [Test]
  public async Task OpenTelemetryTraceOutput_IsSealedAsync() {
    // Arrange
    var type = typeof(OpenTelemetryTraceOutput);

    // Assert
    await Assert.That(type.IsSealed).IsTrue();
  }

  #endregion

  #region ActivitySource Tests

  [Test]
  public async Task OpenTelemetryTraceOutput_HasActivitySourceAsync() {
    // Assert - ActivitySource should be available (static property)
    await Assert.That(OpenTelemetryTraceOutput.ActivitySourceName).IsEqualTo("Whizbang.Tracing");
  }

  #endregion

  #region BeginTrace Tests

  [Test]
  public async Task BeginTrace_CreatesActivityAsync() {
    // Arrange
    using var listener = new ActivityListener {
      ShouldListenTo = source => source.Name == "Whizbang.Tracing",
      Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
      ActivityStarted = _ => { }
    };
    ActivitySource.AddActivityListener(listener);

    var output = new OpenTelemetryTraceOutput();
    var context = _createContext();
    Activity? capturedActivity = null;

    // Act
    output.BeginTrace(context);
    capturedActivity = Activity.Current;

    // Assert - An activity should be started
    await Assert.That(capturedActivity).IsNotNull();
  }

  [Test]
  public async Task BeginTrace_SetsMessageIdTagAsync() {
    // Arrange
    using var listener = _createListener();
    var output = new OpenTelemetryTraceOutput();
    var messageId = Guid.NewGuid();
    var context = _createContext() with { MessageId = messageId };

    // Act
    output.BeginTrace(context);
    var activity = Activity.Current;

    // Assert
    await Assert.That(activity).IsNotNull();
    var tag = activity!.GetTagItem("whizbang.message.id");
    await Assert.That(tag?.ToString()).IsEqualTo(messageId.ToString());
  }

  [Test]
  public async Task BeginTrace_SetsCorrelationIdTagAsync() {
    // Arrange
    using var listener = _createListener();
    var output = new OpenTelemetryTraceOutput();
    var context = _createContext() with { CorrelationId = "corr-test-123" };

    // Act
    output.BeginTrace(context);
    var activity = Activity.Current;

    // Assert
    await Assert.That(activity).IsNotNull();
    var tag = activity!.GetTagItem("whizbang.correlation_id");
    await Assert.That(tag?.ToString()).IsEqualTo("corr-test-123");
  }

  [Test]
  public async Task BeginTrace_SetsExplicitTagAsync() {
    // Arrange
    using var listener = _createListener();
    var output = new OpenTelemetryTraceOutput();
    var context = _createContext() with { IsExplicit = true };

    // Act
    output.BeginTrace(context);
    var activity = Activity.Current;

    // Assert
    await Assert.That(activity).IsNotNull();
    var tag = activity!.GetTagItem("whizbang.trace.explicit");
    await Assert.That(tag).IsEqualTo(true);
  }

  #endregion

  #region EndTrace Tests

  [Test]
  public async Task EndTrace_SetsSuccessStatusAsync() {
    // Arrange
    using var listener = _createListener();
    var output = new OpenTelemetryTraceOutput();
    var context = _createContext();
    var result = TraceResult.Completed(TimeSpan.FromMilliseconds(10));

    // Act
    output.BeginTrace(context);
    var activity = Activity.Current;
    output.EndTrace(context, result);

    // Assert
    await Assert.That(activity).IsNotNull();
    await Assert.That(activity!.Status).IsEqualTo(ActivityStatusCode.Ok);
  }

  [Test]
  public async Task EndTrace_SetsErrorStatusOnFailureAsync() {
    // Arrange
    using var listener = _createListener();
    var output = new OpenTelemetryTraceOutput();
    var context = _createContext();
    var result = TraceResult.Failed(TimeSpan.FromMilliseconds(10), new InvalidOperationException("Test"));

    // Act
    output.BeginTrace(context);
    var activity = Activity.Current;
    output.EndTrace(context, result);

    // Assert
    await Assert.That(activity).IsNotNull();
    await Assert.That(activity!.Status).IsEqualTo(ActivityStatusCode.Error);
  }

  [Test]
  public async Task EndTrace_SetsDurationTagAsync() {
    // Arrange
    using var listener = _createListener();
    var output = new OpenTelemetryTraceOutput();
    var context = _createContext();
    var result = TraceResult.Completed(TimeSpan.FromMilliseconds(42.5));

    // Act
    output.BeginTrace(context);
    var activity = Activity.Current;
    output.EndTrace(context, result);

    // Assert
    await Assert.That(activity).IsNotNull();
    var tag = activity!.GetTagItem("whizbang.duration_ms");
    await Assert.That(tag).IsEqualTo(42.5);
  }

  [Test]
  public async Task EndTrace_SetsStatusTagAsync() {
    // Arrange
    using var listener = _createListener();
    var output = new OpenTelemetryTraceOutput();
    var context = _createContext();
    var result = TraceResult.EarlyReturn(TimeSpan.FromMilliseconds(1));

    // Act
    output.BeginTrace(context);
    var activity = Activity.Current;
    output.EndTrace(context, result);

    // Assert
    await Assert.That(activity).IsNotNull();
    var tag = activity!.GetTagItem("whizbang.status");
    await Assert.That(tag?.ToString()).IsEqualTo("EarlyReturn");
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

  private static ActivityListener _createListener() {
    var listener = new ActivityListener {
      ShouldListenTo = source => source.Name == "Whizbang.Tracing",
      Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
      ActivityStarted = _ => { },
      ActivityStopped = _ => { }
    };
    ActivitySource.AddActivityListener(listener);
    return listener;
  }

  #endregion
}
