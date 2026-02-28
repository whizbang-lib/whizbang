using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for ITraceOutput interface and its usage patterns.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/ITraceOutput.cs</code-under-test>
public class ITraceOutputTests {
  #region Interface Definition Tests

  [Test]
  public async Task ITraceOutput_IsInterfaceAsync() {
    // Arrange
    var type = typeof(ITraceOutput);

    // Assert
    await Assert.That(type.IsInterface).IsTrue();
  }

  [Test]
  public async Task ITraceOutput_HasBeginTraceMethodAsync() {
    // Arrange
    var type = typeof(ITraceOutput);
    var method = type.GetMethod("BeginTrace");

    // Assert
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.GetParameters().Length).IsEqualTo(1);
    await Assert.That(method.GetParameters()[0].ParameterType).IsEqualTo(typeof(TraceContext));
  }

  [Test]
  public async Task ITraceOutput_HasEndTraceMethodAsync() {
    // Arrange
    var type = typeof(ITraceOutput);
    var method = type.GetMethod("EndTrace");

    // Assert
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.GetParameters().Length).IsEqualTo(2);
    await Assert.That(method.GetParameters()[0].ParameterType).IsEqualTo(typeof(TraceContext));
    await Assert.That(method.GetParameters()[1].ParameterType).IsEqualTo(typeof(TraceResult));
  }

  #endregion

  #region Implementation Tests

  [Test]
  public async Task CustomOutput_CanImplementInterfaceAsync() {
    // Arrange
    var output = new TestTraceOutput();
    var context = _createContext();
    var result = TraceResult.Completed(TimeSpan.FromMilliseconds(10));

    // Act
    output.BeginTrace(context);
    output.EndTrace(context, result);

    // Assert
    await Assert.That(output.BeginTraceCallCount).IsEqualTo(1);
    await Assert.That(output.EndTraceCallCount).IsEqualTo(1);
    await Assert.That(output.LastContext).IsEqualTo(context);
    await Assert.That(output.LastResult).IsEqualTo(result);
  }

  [Test]
  public async Task MultipleOutputs_CanBeUsedSimultaneouslyAsync() {
    // Arrange
    var output1 = new TestTraceOutput();
    var output2 = new TestTraceOutput();
    var outputs = new ITraceOutput[] { output1, output2 };
    var context = _createContext();
    var result = TraceResult.Completed(TimeSpan.FromMilliseconds(10));

    // Act - Simulate tracer calling all outputs
    foreach (var output in outputs) {
      output.BeginTrace(context);
    }
    foreach (var output in outputs) {
      output.EndTrace(context, result);
    }

    // Assert - Both outputs received the traces
    await Assert.That(output1.BeginTraceCallCount).IsEqualTo(1);
    await Assert.That(output2.BeginTraceCallCount).IsEqualTo(1);
    await Assert.That(output1.EndTraceCallCount).IsEqualTo(1);
    await Assert.That(output2.EndTraceCallCount).IsEqualTo(1);
  }

  #endregion

  #region Test Fixtures

  private sealed class TestTraceOutput : ITraceOutput {
    public int BeginTraceCallCount { get; private set; }
    public int EndTraceCallCount { get; private set; }
    public TraceContext? LastContext { get; private set; }
    public TraceResult? LastResult { get; private set; }

    public void BeginTrace(TraceContext context) {
      BeginTraceCallCount++;
      LastContext = context;
    }

    public void EndTrace(TraceContext context, TraceResult result) {
      EndTraceCallCount++;
      LastContext = context;
      LastResult = result;
    }
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

  #endregion
}
