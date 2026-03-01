using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rocks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Tracing;
using Whizbang.Testing.Observability;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for <see cref="Tracer"/> implementation.
/// Validates handler tracing, explicit markers, and exception handling.
/// </summary>
/// <remarks>
/// These tests use <c>[NotInParallel]</c> because the
/// <see cref="ActivityListener"/> is global and captures spans
/// from all concurrent activity sources.
/// </remarks>
[NotInParallel(Order = 2)]
public class TracerTests {
  [Test]
  public async Task BeginHandlerTrace_CreatesSpanWithHandlerTagsAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var logger = new LoggerMockBuilder().Build();
    var tracer = new Tracer(logger);

    // Act
    tracer.BeginHandlerTrace(
      handlerName: "MyApp.Handlers.OrderReceptor",
      messageTypeName: "CreateOrderCommand",
      handlerCount: 1,
      isExplicit: false);

    tracer.EndHandlerTrace(
      handlerName: "MyApp.Handlers.OrderReceptor",
      messageTypeName: "CreateOrderCommand",
      status: HandlerStatus.Success,
      durationMs: 42.5,
      startTimestamp: 0,
      endTimestamp: 1000,
      exception: null);

    // Assert
    await Assert.That(collector.Count).IsEqualTo(1);
    var span = collector.Spans[0];
    await Assert.That(span.Name).Contains("OrderReceptor");
    await Assert.That(span.Tags["whizbang.handler.name"]).IsEqualTo("MyApp.Handlers.OrderReceptor");
    await Assert.That(span.Tags["whizbang.message.type"]).IsEqualTo("CreateOrderCommand");
    await Assert.That(span.Tags["whizbang.handler.count"]).IsEqualTo(1);
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(false);
  }

  [Test]
  public async Task BeginHandlerTrace_ExplicitTrue_SetsExplicitTagAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var logger = new LoggerMockBuilder().Build();
    var tracer = new Tracer(logger);

    // Act
    tracer.BeginHandlerTrace(
      handlerName: "MyApp.Handlers.PaymentReceptor",
      messageTypeName: "ProcessPaymentCommand",
      handlerCount: 3,
      isExplicit: true);

    tracer.EndHandlerTrace(
      handlerName: "MyApp.Handlers.PaymentReceptor",
      messageTypeName: "ProcessPaymentCommand",
      status: HandlerStatus.Success,
      durationMs: 100.0,
      startTimestamp: 0,
      endTimestamp: 2000,
      exception: null);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }

  [Test]
  public async Task EndHandlerTrace_Success_SetsOkStatusAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var logger = new LoggerMockBuilder().Build();
    var tracer = new Tracer(logger);

    // Act
    tracer.BeginHandlerTrace("Handler", "Message", 1, false);
    tracer.EndHandlerTrace("Handler", "Message", HandlerStatus.Success, 50.0, 0, 1000, null);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Ok);
    await Assert.That(span.Tags["whizbang.handler.status"]).IsEqualTo("Success");
  }

  [Test]
  public async Task EndHandlerTrace_EarlyReturn_SetsOkStatusAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var logger = new LoggerMockBuilder().Build();
    var tracer = new Tracer(logger);

    // Act
    tracer.BeginHandlerTrace("Handler", "Message", 1, false);
    tracer.EndHandlerTrace("Handler", "Message", HandlerStatus.EarlyReturn, 1.0, 0, 100, null);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Ok);
    await Assert.That(span.Tags["whizbang.handler.status"]).IsEqualTo("EarlyReturn");
  }

  [Test]
  public async Task EndHandlerTrace_Failed_SetsErrorStatusAndRecordsExceptionAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var logger = new LoggerMockBuilder().Build();
    var tracer = new Tracer(logger);
    var exception = new InvalidOperationException("Handler failed");

    // Act
    tracer.BeginHandlerTrace("Handler", "Message", 1, false);
    tracer.EndHandlerTrace("Handler", "Message", HandlerStatus.Failed, 200.0, 0, 5000, exception);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(span.Tags["whizbang.handler.status"]).IsEqualTo("Failed");

    // Check exception event was recorded
    await Assert.That(span.Events.Count).IsGreaterThan(0);
    var exceptionEvent = span.Events.First(e => e.Name == "exception");
    await Assert.That(exceptionEvent.Tags["exception.type"]).IsEqualTo(typeof(InvalidOperationException).FullName);
    await Assert.That(exceptionEvent.Tags["exception.message"]).IsEqualTo("Handler failed");
  }

  [Test]
  public async Task EndHandlerTrace_RecordsDurationAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var logger = new LoggerMockBuilder().Build();
    var tracer = new Tracer(logger);

    // Act
    tracer.BeginHandlerTrace("Handler", "Message", 1, false);
    tracer.EndHandlerTrace("Handler", "Message", HandlerStatus.Success, 123.45, 0, 1000, null);

    // Assert
    var span = collector.Spans[0];
    await Assert.That(span.Tags["whizbang.handler.duration_ms"]).IsEqualTo(123.45);
  }

  [Test]
  public async Task Tracer_ExtractsShortHandlerNameForSpanAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var logger = new LoggerMockBuilder().Build();
    var tracer = new Tracer(logger);

    // Act
    tracer.BeginHandlerTrace(
      "MyApp.Features.Orders.Handlers.OrderReceptor",
      "CreateOrderCommand",
      1,
      false);
    tracer.EndHandlerTrace(
      "MyApp.Features.Orders.Handlers.OrderReceptor",
      "CreateOrderCommand",
      HandlerStatus.Success,
      10.0,
      0,
      100,
      null);

    // Assert - Span name should be shortened
    var span = collector.Spans[0];
    await Assert.That(span.Name).Contains("Handlers.OrderReceptor");
    // Full name still in tag
    await Assert.That(span.Tags["whizbang.handler.name"])
      .IsEqualTo("MyApp.Features.Orders.Handlers.OrderReceptor");
  }

  [Test]
  public async Task MultipleHandlers_CreateSeparateSpansAsync() {
    // Arrange
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var logger = new LoggerMockBuilder().Build();
    var tracer = new Tracer(logger);

    // Act - Simulate multiple handlers being invoked
    tracer.BeginHandlerTrace("Handler1", "Event", 3, false);
    tracer.EndHandlerTrace("Handler1", "Event", HandlerStatus.Success, 10.0, 0, 100, null);

    tracer.BeginHandlerTrace("Handler2", "Event", 3, false);
    tracer.EndHandlerTrace("Handler2", "Event", HandlerStatus.Success, 20.0, 0, 200, null);

    tracer.BeginHandlerTrace("Handler3", "Event", 3, true);
    tracer.EndHandlerTrace("Handler3", "Event", HandlerStatus.EarlyReturn, 5.0, 0, 50, null);

    // Assert - Spans are captured in completion order, verify by name presence
    await Assert.That(collector.Count).IsEqualTo(3);

    var handlerNames = collector.Spans
      .Select(s => s.Tags["whizbang.handler.name"]?.ToString())
      .ToHashSet();

    await Assert.That(handlerNames).Contains("Handler1");
    await Assert.That(handlerNames).Contains("Handler2");
    await Assert.That(handlerNames).Contains("Handler3");

    // Verify explicit handler
    var explicitSpan = collector.FirstOrDefault(s =>
      s.Tags["whizbang.handler.name"]?.ToString() == "Handler3");
    await Assert.That(explicitSpan).IsNotNull();
    await Assert.That(explicitSpan!.Tags["whizbang.trace.explicit"]).IsEqualTo(true);
  }
}

/// <summary>
/// Builder for creating mock ILogger instances for tests.
/// </summary>
internal sealed class LoggerMockBuilder {
  public ILogger<Tracer> Build() {
    // For these tests, we just need a no-op logger since we're testing span output
    return new NullLogger();
  }

  /// <summary>
  /// Minimal null logger implementation for testing.
  /// </summary>
  private sealed class NullLogger : ILogger<Tracer> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
  }
}
