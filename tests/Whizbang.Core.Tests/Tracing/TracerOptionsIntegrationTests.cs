using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rocks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;
using Whizbang.Testing.Observability;

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for <see cref="Tracer"/> integration with <see cref="TracingOptions"/>.
/// Validates that Tracer respects verbosity, component filtering, and targeted tracing.
/// </summary>
/// <remarks>
/// Uses <c>[NotInParallel]</c> because ActivityListener is global and captures
/// spans from all concurrent activity sources.
/// </remarks>
/// <tests>Whizbang.Core/Tracing/Tracer.cs</tests>
[Category("Core")]
[Category("Tracing")]
[NotInParallel(Order = 3)]
public class TracerOptionsIntegrationTests {

  // ==========================================================================
  // Verbosity Off Tests
  // ==========================================================================

  [Test]
  public async Task BeginHandlerTrace_VerbosityOff_DoesNotEmitSpanAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = _createOptions(verbosity: TraceVerbosity.Off);
    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("Handler", "Message", 1, false);
    tracer.EndHandlerTrace("Handler", "Message", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - No spans should be emitted when verbosity is Off
    await Assert.That(collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task BeginHandlerTrace_VerbosityOff_ExplicitTrue_StillDoesNotEmitSpanAsync() {
    // Arrange - Even explicit handlers should not trace when verbosity is Off
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = _createOptions(verbosity: TraceVerbosity.Off);
    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("Handler", "Message", 1, isExplicit: true);
    tracer.EndHandlerTrace("Handler", "Message", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - Explicit flag doesn't override Off verbosity (Off means completely off)
    await Assert.That(collector.Count).IsEqualTo(0);
  }

  // ==========================================================================
  // Component Filtering Tests
  // ==========================================================================

  [Test]
  public async Task BeginHandlerTrace_HandlersNotEnabled_DoesNotEmitSpanAsync() {
    // Arrange - Verbosity is on but Handlers component is not enabled
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = _createOptions(
      verbosity: TraceVerbosity.Verbose,
      components: TraceComponents.Lifecycle); // Only Lifecycle, not Handlers
    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("Handler", "Message", 1, false);
    tracer.EndHandlerTrace("Handler", "Message", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - No spans because Handlers component is not enabled
    await Assert.That(collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task BeginHandlerTrace_HandlersEnabled_EmitsSpanAsync() {
    // Arrange - Verbosity and Handlers component both enabled
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = _createOptions(
      verbosity: TraceVerbosity.Verbose,
      components: TraceComponents.Handlers);
    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("Handler", "Message", 1, false);
    tracer.EndHandlerTrace("Handler", "Message", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - Span should be emitted
    await Assert.That(collector.Count).IsEqualTo(1);
  }

  [Test]
  public async Task BeginHandlerTrace_AllComponentsEnabled_EmitsSpanAsync() {
    // Arrange - Using TraceComponents.All
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = _createOptions(
      verbosity: TraceVerbosity.Debug,
      components: TraceComponents.All);
    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("Handler", "Message", 1, false);
    tracer.EndHandlerTrace("Handler", "Message", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert
    await Assert.That(collector.Count).IsEqualTo(1);
  }

  // ==========================================================================
  // TracedHandlers Configuration Tests
  // ==========================================================================

  [Test]
  public async Task BeginHandlerTrace_MatchesTracedHandler_EmitsSpanAsync() {
    // Arrange - Handler matches TracedHandlers configuration
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = _createOptions(
      verbosity: TraceVerbosity.Minimal,  // Low baseline
      components: TraceComponents.Handlers);
    options.CurrentValue.TracedHandlers["OrderReceptor"] = TraceVerbosity.Debug;
    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("MyApp.Handlers.OrderReceptor", "CreateOrderCommand", 1, false);
    tracer.EndHandlerTrace("MyApp.Handlers.OrderReceptor", "CreateOrderCommand", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - Should trace because handler matches TracedHandlers config
    await Assert.That(collector.Count).IsEqualTo(1);
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  [Test]
  public async Task BeginHandlerTrace_NoMatchInTracedHandler_UsesGlobalVerbosityAsync() {
    // Arrange - Handler does not match any TracedHandlers pattern
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = _createOptions(
      verbosity: TraceVerbosity.Verbose,
      components: TraceComponents.Handlers);
    options.CurrentValue.TracedHandlers["OrderReceptor"] = TraceVerbosity.Debug; // Different handler
    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("MyApp.Handlers.PaymentReceptor", "ProcessPaymentCommand", 1, false);
    tracer.EndHandlerTrace("MyApp.Handlers.PaymentReceptor", "ProcessPaymentCommand", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - Should trace using global verbosity (Verbose)
    await Assert.That(collector.Count).IsEqualTo(1);
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(false);
  }

  // ==========================================================================
  // TracedMessages Configuration Tests
  // ==========================================================================

  [Test]
  public async Task BeginHandlerTrace_MatchesTracedMessage_EmitsSpanAsync() {
    // Arrange - Message matches TracedMessages configuration
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = _createOptions(
      verbosity: TraceVerbosity.Minimal,
      components: TraceComponents.Handlers);
    options.CurrentValue.TracedMessages["ReseedSystemEvent"] = TraceVerbosity.Debug;
    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("MyApp.Handlers.SeedHandler", "ReseedSystemEvent", 3, false);
    tracer.EndHandlerTrace("MyApp.Handlers.SeedHandler", "ReseedSystemEvent", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - Should trace because message matches TracedMessages config
    await Assert.That(collector.Count).IsEqualTo(1);
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  // ==========================================================================
  // Wildcard Pattern Matching Tests
  // ==========================================================================

  [Test]
  public async Task BeginHandlerTrace_WildcardPrefix_MatchesHandlerAsync() {
    // Arrange - Handler matches wildcard pattern "Order*"
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = _createOptions(
      verbosity: TraceVerbosity.Minimal,
      components: TraceComponents.Handlers);
    options.CurrentValue.TracedHandlers["Order*"] = TraceVerbosity.Debug;
    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("MyApp.Handlers.OrderValidator", "CreateOrder", 1, false);
    tracer.EndHandlerTrace("MyApp.Handlers.OrderValidator", "CreateOrder", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - Should match "Order*" pattern
    await Assert.That(collector.Count).IsEqualTo(1);
  }

  [Test]
  public async Task BeginHandlerTrace_WildcardSuffix_MatchesHandlerAsync() {
    // Arrange - Handler matches wildcard pattern "*Receptor"
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = _createOptions(
      verbosity: TraceVerbosity.Minimal,
      components: TraceComponents.Handlers);
    options.CurrentValue.TracedHandlers["*Receptor"] = TraceVerbosity.Debug;
    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("MyApp.Handlers.PaymentReceptor", "ProcessPayment", 1, false);
    tracer.EndHandlerTrace("MyApp.Handlers.PaymentReceptor", "ProcessPayment", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - Should match "*Receptor" pattern
    await Assert.That(collector.Count).IsEqualTo(1);
  }

  // ==========================================================================
  // IOptionsMonitor Runtime Updates Tests
  // ==========================================================================

  [Test]
  public async Task BeginHandlerTrace_OptionsChangeAtRuntime_ReflectsNewVerbosityAsync() {
    // Arrange - Start with tracing off
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var tracingOptions = new TracingOptions {
      Verbosity = TraceVerbosity.Off,
      Components = TraceComponents.Handlers
    };
    var optionsMonitor = new TestOptionsMonitor<TracingOptions>(tracingOptions);
    var tracer = _createTracer(optionsMonitor);

    // Act - First call with tracing off
    tracer.BeginHandlerTrace("Handler1", "Message1", 1, false);
    tracer.EndHandlerTrace("Handler1", "Message1", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - No span
    await Assert.That(collector.Count).IsEqualTo(0);

    // Arrange - Change options at runtime
    tracingOptions.Verbosity = TraceVerbosity.Verbose;

    // Act - Second call with tracing now on
    tracer.BeginHandlerTrace("Handler2", "Message2", 1, false);
    tracer.EndHandlerTrace("Handler2", "Message2", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - Should now have one span (only from second call)
    await Assert.That(collector.Count).IsEqualTo(1);
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.handler.name"]).IsEqualTo("Handler2");
  }

  // ==========================================================================
  // OpenTelemetry and Logging Toggles
  // ==========================================================================

  [Test]
  public async Task BeginHandlerTrace_OpenTelemetryDisabled_DoesNotEmitSpanAsync() {
    // Arrange - OpenTelemetry is disabled but structured logging is enabled
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = _createOptions(
      verbosity: TraceVerbosity.Verbose,
      components: TraceComponents.Handlers);
    options.CurrentValue.EnableOpenTelemetry = false;
    options.CurrentValue.EnableStructuredLogging = true;
    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("Handler", "Message", 1, false);
    tracer.EndHandlerTrace("Handler", "Message", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - No OTel spans when EnableOpenTelemetry is false
    await Assert.That(collector.Count).IsEqualTo(0);
  }

  // ==========================================================================
  // Helper Methods
  // ==========================================================================

  private static TestOptionsMonitor<TracingOptions> _createOptions(
    TraceVerbosity verbosity = TraceVerbosity.Off,
    TraceComponents components = TraceComponents.None) {
    var tracingOptions = new TracingOptions {
      Verbosity = verbosity,
      Components = components
    };
    return new TestOptionsMonitor<TracingOptions>(tracingOptions);
  }

  private static Tracer _createTracer(IOptionsMonitor<TracingOptions> options) {
    var logger = new TestNullLogger();
    return new Tracer(logger, options);
  }

  /// <summary>
  /// Simple IOptionsMonitor implementation for testing.
  /// </summary>
  private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T> {
    private readonly T _options;

    public TestOptionsMonitor(T options) {
      _options = options;
    }

    public T CurrentValue => _options;
    public T Get(string? name) => _options;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
  }

  /// <summary>
  /// Minimal null logger for testing.
  /// </summary>
  private sealed class TestNullLogger : ILogger<Tracer> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
  }
}
