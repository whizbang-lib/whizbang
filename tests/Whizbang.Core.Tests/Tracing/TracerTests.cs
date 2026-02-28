using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for Tracer which orchestrates tracing decisions and output.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/Tracer.cs</code-under-test>
public class TracerTests {
  #region Implementation Tests

  [Test]
  public async Task Tracer_ImplementsITracerAsync() {
    // Arrange
    var type = typeof(Tracer);

    // Assert
    await Assert.That(typeof(ITracer).IsAssignableFrom(type)).IsTrue();
  }

  [Test]
  public async Task Tracer_IsSealedAsync() {
    // Arrange
    var type = typeof(Tracer);

    // Assert
    await Assert.That(type.IsSealed).IsTrue();
  }

  #endregion

  #region ShouldTrace Tests

  [Test]
  public async Task ShouldTrace_VerbosityOff_ReturnsFalseAsync() {
    // Arrange
    var options = _createOptions(TraceVerbosity.Off, TraceComponents.All);
    var tracer = new Tracer(options, []);

    // Act
    var result = tracer.ShouldTrace(TraceComponents.Handlers);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ShouldTrace_ComponentEnabled_ReturnsTrueAsync() {
    // Arrange
    var options = _createOptions(TraceVerbosity.Normal, TraceComponents.Handlers);
    var tracer = new Tracer(options, []);

    // Act
    var result = tracer.ShouldTrace(TraceComponents.Handlers);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ShouldTrace_ComponentNotEnabled_ReturnsFalseAsync() {
    // Arrange
    var options = _createOptions(TraceVerbosity.Normal, TraceComponents.Http);
    var tracer = new Tracer(options, []);

    // Act
    var result = tracer.ShouldTrace(TraceComponents.Handlers);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ShouldTrace_TracedHandlerMatch_ReturnsTrueAsync() {
    // Arrange
    var tracingOptions = new TracingOptions {
      Verbosity = TraceVerbosity.Off,
      Components = TraceComponents.None,
      TracedHandlers = { ["OrderReceptor"] = TraceVerbosity.Debug }
    };
    var options = Options.Create(tracingOptions);
    var tracer = new Tracer(options, []);

    // Act
    var result = tracer.ShouldTrace(TraceComponents.Handlers, handlerName: "OrderReceptor");

    // Assert - explicit config always traces
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ShouldTrace_TracedMessageMatch_ReturnsTrueAsync() {
    // Arrange
    var tracingOptions = new TracingOptions {
      Verbosity = TraceVerbosity.Off,
      Components = TraceComponents.None,
      TracedMessages = { ["ReseedSystemEvent"] = TraceVerbosity.Verbose }
    };
    var options = Options.Create(tracingOptions);
    var tracer = new Tracer(options, []);

    // Act
    var result = tracer.ShouldTrace(TraceComponents.Handlers, messageType: "ReseedSystemEvent");

    // Assert - explicit config always traces
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ShouldTrace_WildcardHandlerMatch_ReturnsTrueAsync() {
    // Arrange
    var tracingOptions = new TracingOptions {
      Verbosity = TraceVerbosity.Off,
      Components = TraceComponents.None,
      TracedHandlers = { ["Order*"] = TraceVerbosity.Debug }
    };
    var options = Options.Create(tracingOptions);
    var tracer = new Tracer(options, []);

    // Act
    var result = tracer.ShouldTrace(TraceComponents.Handlers, handlerName: "OrderReceptor");

    // Assert
    await Assert.That(result).IsTrue();
  }

  #endregion

  #region GetEffectiveVerbosity Tests

  [Test]
  public async Task GetEffectiveVerbosity_AttributeVerbosity_ReturnsThatAsync() {
    // Arrange
    var options = _createOptions(TraceVerbosity.Normal, TraceComponents.All);
    var tracer = new Tracer(options, []);
    var attributeVerbosity = TraceVerbosity.Debug;

    // Act
    var result = tracer.GetEffectiveVerbosity(null, null, attributeVerbosity);

    // Assert - attribute takes priority
    await Assert.That(result).IsEqualTo(TraceVerbosity.Debug);
  }

  [Test]
  public async Task GetEffectiveVerbosity_ConfigHandler_ReturnsThatAsync() {
    // Arrange
    var tracingOptions = new TracingOptions {
      Verbosity = TraceVerbosity.Minimal,
      TracedHandlers = { ["OrderReceptor"] = TraceVerbosity.Verbose }
    };
    var options = Options.Create(tracingOptions);
    var tracer = new Tracer(options, []);

    // Act
    var result = tracer.GetEffectiveVerbosity("OrderReceptor", null, null);

    // Assert
    await Assert.That(result).IsEqualTo(TraceVerbosity.Verbose);
  }

  [Test]
  public async Task GetEffectiveVerbosity_ConfigMessage_ReturnsThatAsync() {
    // Arrange
    var tracingOptions = new TracingOptions {
      Verbosity = TraceVerbosity.Minimal,
      TracedMessages = { ["ReseedSystemEvent"] = TraceVerbosity.Debug }
    };
    var options = Options.Create(tracingOptions);
    var tracer = new Tracer(options, []);

    // Act
    var result = tracer.GetEffectiveVerbosity(null, "ReseedSystemEvent", null);

    // Assert
    await Assert.That(result).IsEqualTo(TraceVerbosity.Debug);
  }

  [Test]
  public async Task GetEffectiveVerbosity_NoOverride_ReturnsGlobalAsync() {
    // Arrange
    var options = _createOptions(TraceVerbosity.Normal, TraceComponents.All);
    var tracer = new Tracer(options, []);

    // Act
    var result = tracer.GetEffectiveVerbosity(null, null, null);

    // Assert
    await Assert.That(result).IsEqualTo(TraceVerbosity.Normal);
  }

  #endregion

  #region BeginTrace Tests

  [Test]
  public async Task BeginTrace_CallsAllOutputsAsync() {
    // Arrange
    var outputs = new List<TestTraceOutput> { new(), new() };
    var options = _createOptions(TraceVerbosity.Normal, TraceComponents.All);
    var tracer = new Tracer(options, outputs);
    var context = _createContext();

    // Act
    using var scope = tracer.BeginTrace(context);

    // Assert - both outputs called
    await Assert.That(outputs[0].BeginTraceCalled).IsTrue();
    await Assert.That(outputs[1].BeginTraceCalled).IsTrue();
  }

  [Test]
  public async Task BeginTrace_ReturnsTraceScopeAsync() {
    // Arrange
    var options = _createOptions(TraceVerbosity.Normal, TraceComponents.All);
    var tracer = new Tracer(options, []);
    var context = _createContext();

    // Act
    using var scope = tracer.BeginTrace(context);

    // Assert
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope).IsAssignableTo<ITraceScope>();
  }

  [Test]
  public async Task TraceScope_Complete_CallsEndTraceOnOutputsAsync() {
    // Arrange
    var output = new TestTraceOutput();
    var options = _createOptions(TraceVerbosity.Normal, TraceComponents.All);
    var tracer = new Tracer(options, [output]);
    var context = _createContext();

    // Act
    using var scope = tracer.BeginTrace(context);
    scope.Complete();

    // Assert
    await Assert.That(output.EndTraceCalled).IsTrue();
    await Assert.That(output.LastResult).IsNotNull();
    await Assert.That(output.LastResult!.Success).IsTrue();
    await Assert.That(output.LastResult!.Status).IsEqualTo("Completed");
  }

  [Test]
  public async Task TraceScope_Fail_CallsEndTraceWithExceptionAsync() {
    // Arrange
    var output = new TestTraceOutput();
    var options = _createOptions(TraceVerbosity.Normal, TraceComponents.All);
    var tracer = new Tracer(options, [output]);
    var context = _createContext();
    var exception = new InvalidOperationException("Test error");

    // Act
    using var scope = tracer.BeginTrace(context);
    scope.Fail(exception);

    // Assert
    await Assert.That(output.EndTraceCalled).IsTrue();
    await Assert.That(output.LastResult).IsNotNull();
    await Assert.That(output.LastResult!.Success).IsFalse();
    await Assert.That(output.LastResult!.Status).IsEqualTo("Failed");
    await Assert.That(output.LastResult!.Exception).IsEqualTo(exception);
  }

  [Test]
  public async Task TraceScope_EarlyReturn_CallsEndTraceWithEarlyReturnStatusAsync() {
    // Arrange
    var output = new TestTraceOutput();
    var options = _createOptions(TraceVerbosity.Normal, TraceComponents.All);
    var tracer = new Tracer(options, [output]);
    var context = _createContext();

    // Act
    using var scope = tracer.BeginTrace(context);
    scope.EarlyReturn();

    // Assert
    await Assert.That(output.EndTraceCalled).IsTrue();
    await Assert.That(output.LastResult).IsNotNull();
    await Assert.That(output.LastResult!.Success).IsTrue();
    await Assert.That(output.LastResult!.Status).IsEqualTo("EarlyReturn");
  }

  [Test]
  public async Task TraceScope_Dispose_WithoutCompletion_CallsEndTraceAsync() {
    // Arrange
    var output = new TestTraceOutput();
    var options = _createOptions(TraceVerbosity.Normal, TraceComponents.All);
    var tracer = new Tracer(options, [output]);
    var context = _createContext();

    // Act
    var scope = tracer.BeginTrace(context);
    scope.Dispose();

    // Assert - disposal without completion still calls EndTrace
    await Assert.That(output.EndTraceCalled).IsTrue();
  }

  [Test]
  public async Task TraceScope_MeasuresDurationAsync() {
    // Arrange
    var output = new TestTraceOutput();
    var options = _createOptions(TraceVerbosity.Normal, TraceComponents.All);
    var tracer = new Tracer(options, [output]);
    var context = _createContext();

    // Act
    using var scope = tracer.BeginTrace(context);
    await Task.Delay(10); // Small delay to measure
    scope.Complete();

    // Assert
    await Assert.That(output.LastResult!.Duration.TotalMilliseconds).IsGreaterThan(0);
  }

  #endregion

  #region Helper Methods

  private static IOptions<TracingOptions> _createOptions(TraceVerbosity verbosity, TraceComponents components) {
    return Options.Create(new TracingOptions {
      Verbosity = verbosity,
      Components = components
    });
  }

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

  #region Test Helpers

  private sealed class TestTraceOutput : ITraceOutput {
    public bool BeginTraceCalled { get; private set; }
    public bool EndTraceCalled { get; private set; }
    public TraceContext? LastContext { get; private set; }
    public TraceResult? LastResult { get; private set; }

    public void BeginTrace(TraceContext context) {
      BeginTraceCalled = true;
      LastContext = context;
    }

    public void EndTrace(TraceContext context, TraceResult result) {
      EndTraceCalled = true;
      LastContext = context;
      LastResult = result;
    }
  }

  #endregion
}
