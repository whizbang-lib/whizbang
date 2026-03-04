using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Tracing;
using Whizbang.Testing.Observability;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for the pattern matching functionality in <see cref="Tracer"/>.
/// Validates TracedHandlers and TracedMessages pattern matching with wildcards.
/// </summary>
[NotInParallel(Order = 3)]
public class TracerPatternMatchingTests {
  // ==========================================================================
  // TracedHandlers Pattern Matching Tests
  // ==========================================================================

  [Test]
  public async Task TracedHandlers_ExactMatch_ElevatesTraceAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    options.TracedHandlers["OrderReceptor"] = TraceVerbosity.Debug;

    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("OrderReceptor", "CreateOrderCommand", 1, false);
    tracer.EndHandlerTrace("OrderReceptor", "CreateOrderCommand", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - explicit flag should be true due to TracedHandlers match
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  [Test]
  public async Task TracedHandlers_FullyQualifiedExactMatch_ElevatesTraceAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    // Pattern matches fully qualified name via EndsWith check
    options.TracedHandlers["OrderReceptor"] = TraceVerbosity.Debug;

    var tracer = _createTracer(options);

    // Act - Use fully qualified handler name
    tracer.BeginHandlerTrace("MyApp.Handlers.OrderReceptor", "CreateOrderCommand", 1, false);
    tracer.EndHandlerTrace("MyApp.Handlers.OrderReceptor", "CreateOrderCommand", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - should match because handler name ends with "OrderReceptor"
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  [Test]
  public async Task TracedHandlers_PrefixWildcard_MatchesHandlersStartingWithPatternAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    options.TracedHandlers["Order*"] = TraceVerbosity.Debug;

    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("OrderReceptor", "CreateOrderCommand", 1, false);
    tracer.EndHandlerTrace("OrderReceptor", "CreateOrderCommand", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  [Test]
  public async Task TracedHandlers_SuffixWildcard_MatchesHandlersEndingWithPatternAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    options.TracedHandlers["*Receptor"] = TraceVerbosity.Debug;

    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("PaymentReceptor", "ProcessPaymentCommand", 1, false);
    tracer.EndHandlerTrace("PaymentReceptor", "ProcessPaymentCommand", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  [Test]
  public async Task TracedHandlers_MiddleWildcard_MatchesPatternAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    options.TracedHandlers["Order*Receptor"] = TraceVerbosity.Debug;

    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("OrderValidationReceptor", "ValidateOrderCommand", 1, false);
    tracer.EndHandlerTrace("OrderValidationReceptor", "ValidateOrderCommand", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  [Test]
  public async Task TracedHandlers_NoMatch_DoesNotElevateTraceAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    options.TracedHandlers["Order*"] = TraceVerbosity.Debug;

    var tracer = _createTracer(options);

    // Act - Payment handler should NOT match Order* pattern
    tracer.BeginHandlerTrace("PaymentReceptor", "ProcessPaymentCommand", 1, false);
    tracer.EndHandlerTrace("PaymentReceptor", "ProcessPaymentCommand", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(false);
  }

  [Test]
  public async Task TracedHandlers_CaseInsensitiveMatch_MatchesAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    options.TracedHandlers["ORDERRECEPTOR"] = TraceVerbosity.Debug;

    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("OrderReceptor", "CreateOrderCommand", 1, false);
    tracer.EndHandlerTrace("OrderReceptor", "CreateOrderCommand", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - should match case-insensitively
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  // ==========================================================================
  // TracedMessages Pattern Matching Tests
  // ==========================================================================

  [Test]
  public async Task TracedMessages_ExactMatch_ElevatesTraceAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    options.TracedMessages["CreateOrderCommand"] = TraceVerbosity.Debug;

    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("AnyHandler", "CreateOrderCommand", 1, false);
    tracer.EndHandlerTrace("AnyHandler", "CreateOrderCommand", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  [Test]
  public async Task TracedMessages_SuffixWildcard_MatchesMessagesEndingWithPatternAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    options.TracedMessages["*Command"] = TraceVerbosity.Debug;

    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("AnyHandler", "ProcessPaymentCommand", 1, false);
    tracer.EndHandlerTrace("AnyHandler", "ProcessPaymentCommand", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  [Test]
  public async Task TracedMessages_PrefixWildcard_MatchesMessagesStartingWithPatternAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    options.TracedMessages["Order*"] = TraceVerbosity.Debug;

    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("AnyHandler", "OrderCreatedEvent", 1, false);
    tracer.EndHandlerTrace("AnyHandler", "OrderCreatedEvent", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  [Test]
  public async Task TracedMessages_FullyQualifiedMatch_ElevatesTraceAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    // Short name should match fully qualified message type via EndsWith
    options.TracedMessages["OrderCreatedEvent"] = TraceVerbosity.Debug;

    var tracer = _createTracer(options);

    // Act - Use fully qualified message type
    tracer.BeginHandlerTrace("AnyHandler", "MyApp.Events.OrderCreatedEvent", 1, false);
    tracer.EndHandlerTrace("AnyHandler", "MyApp.Events.OrderCreatedEvent", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  // ==========================================================================
  // Combined Tests
  // ==========================================================================

  [Test]
  public async Task EitherHandlerOrMessageMatch_ElevatesTraceAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    // Only TracedMessages configured, no TracedHandlers
    options.TracedMessages["*Event"] = TraceVerbosity.Debug;

    var tracer = _createTracer(options);

    // Act - Handler doesn't match anything, but message does
    tracer.BeginHandlerTrace("UnrelatedHandler", "OrderCreatedEvent", 1, false);
    tracer.EndHandlerTrace("UnrelatedHandler", "OrderCreatedEvent", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - Should be elevated due to message match
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  [Test]
  public async Task ExplicitFlag_TakesPrecedenceOverPatternMatchAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    // No patterns configured

    var tracer = _createTracer(options);

    // Act - isExplicit: true should elevate even without pattern match
    tracer.BeginHandlerTrace("AnyHandler", "AnyMessage", 1, isExplicit: true);
    tracer.EndHandlerTrace("AnyHandler", "AnyMessage", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  [Test]
  public async Task VerbosityOff_SkipsAllTracingAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Off, // Tracing completely disabled
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };
    options.TracedHandlers["*"] = TraceVerbosity.Debug;

    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("AnyHandler", "AnyMessage", 1, false);
    tracer.EndHandlerTrace("AnyHandler", "AnyMessage", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - No spans should be recorded
    await Assert.That(collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ComponentDisabled_SkipsTracingAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Lifecycle, // Only Lifecycle, not Handlers
      EnableOpenTelemetry = true,
      EnableStructuredLogging = false
    };

    var tracer = _createTracer(options);

    // Act
    tracer.BeginHandlerTrace("AnyHandler", "AnyMessage", 1, false);
    tracer.EndHandlerTrace("AnyHandler", "AnyMessage", HandlerStatus.Success, 10.0, 0, 100, null);

    // Assert - No spans should be recorded since Handlers component is disabled
    await Assert.That(collector.Count).IsEqualTo(0);
  }

  // ==========================================================================
  // Helper Methods
  // ==========================================================================

  private static Tracer _createTracer(TracingOptions options) {
    var optionsMonitor = new TestOptionsMonitor<TracingOptions>(options);
    var logger = new TestNullLogger();
    return new Tracer(logger, optionsMonitor);
  }

  private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T> {
    private readonly T _options;

    public TestOptionsMonitor(T options) {
      _options = options;
    }

    public T CurrentValue => _options;
    public T Get(string? name) => _options;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
  }

  private sealed class TestNullLogger : ILogger<Tracer> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
  }
}
