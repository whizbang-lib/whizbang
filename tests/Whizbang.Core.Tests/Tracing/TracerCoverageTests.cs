using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;
using Whizbang.Testing.Observability;

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Coverage for three <see cref="Tracer"/> branches the primary suites
/// (<see cref="TracerStructuredLoggingTests"/> in this project, <c>TracerTests</c> in
/// Whizbang.Observability.Tests) don't reach: a wildcard watch pattern that matches only a
/// handler's short (class) name rather than its fully-qualified name, a configured pattern that
/// matches neither and must not falsely elevate the trace, and the OpenTelemetry span name for a
/// handler whose fully-qualified name carries only one dot. An operator raises verbosity for one
/// handler by name — a pattern-matching regression here either buries their watch in silence or
/// floods it with every handler in the process.
/// </summary>
public class TracerCoverageTests {

  private sealed class _capturingLogger : ILogger<Tracer> {
    private readonly List<string> _messages = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_messages) { _messages.Add(formatter(state, exception)); }
    }
    public List<string> Messages { get { lock (_messages) { return [.. _messages]; } } }
  }

  private sealed class _staticOptionsMonitor(TracingOptions value) : IOptionsMonitor<TracingOptions> {
    public TracingOptions CurrentValue => value;
    public TracingOptions Get(string? name) => value;
    public IDisposable? OnChange(Action<TracingOptions, string?> listener) => null;
  }

  /// <summary>What breaks: an operator who configures a watch pattern against a short class name
  /// (the common case — nobody writes the fully-qualified namespace when raising verbosity for
  /// "OrderReceptor") must still see it elevated for every namespaced handler that class name
  /// belongs to, or the watch silently does nothing.</summary>
  [Test]
  public async Task BeginHandlerTrace_WildcardPatternMatchesOnlyTheShortNameAsync() {
    var logger = new _capturingLogger();
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableStructuredLogging = true,
    };
    options.TracedHandlers["Order*"] = TraceVerbosity.Verbose;
    var tracer = new Tracer(logger, new _staticOptionsMonitor(options));

    tracer.BeginHandlerTrace("MyApp.Handlers.OrderReceptor", "OrderPlaced", handlerCount: 1, isExplicit: false);
    tracer.EndHandlerTrace("MyApp.Handlers.OrderReceptor", "OrderPlaced", HandlerStatus.Success,
      durationMs: 1.0, startTimestamp: 0, endTimestamp: 10, exception: null);

    await Assert.That(logger.Messages.Any(m => m.Contains("[TRACE]", StringComparison.Ordinal))).IsTrue()
      .Because("\"Order*\" cannot match the fully-qualified \"MyApp.Handlers.OrderReceptor\" directly (regex is anchored at the start) — it must fall back to matching the short class name \"OrderReceptor\", or a watch configured the ordinary way silently elevates nothing");
  }

  /// <summary>What breaks: a configured pattern that matches neither the full nor the short name
  /// must leave the trace at its ordinary verbosity — falsely elevating it would flood an
  /// operator's targeted watch with every routine trace instead of just the one they asked for.</summary>
  [Test]
  public async Task BeginHandlerTrace_NonWildcardPatternNotMatching_StaysRoutineAsync() {
    var logger = new _capturingLogger();
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableStructuredLogging = true,
    };
    options.TracedHandlers["SomethingElseEntirely"] = TraceVerbosity.Verbose;
    var tracer = new Tracer(logger, new _staticOptionsMonitor(options));

    tracer.BeginHandlerTrace("MyApp.Handlers.OrderReceptor", "OrderPlaced", handlerCount: 1, isExplicit: false);
    tracer.EndHandlerTrace("MyApp.Handlers.OrderReceptor", "OrderPlaced", HandlerStatus.Success,
      durationMs: 1.0, startTimestamp: 0, endTimestamp: 10, exception: null);

    await Assert.That(logger.Messages.Any(m => m.Contains("[trace]", StringComparison.Ordinal))).IsTrue()
      .Because("a mismatched pattern must fall through to the ordinary (lowercase) trace message");
    await Assert.That(logger.Messages.Any(m => m.Contains("[TRACE]", StringComparison.Ordinal))).IsFalse()
      .Because("a mismatched watch pattern must never falsely elevate — that would flood the operator's targeted watch with unrelated handlers");
  }

  /// <summary>What breaks: a handler name with only one dot has nothing left to shorten past the
  /// namespace/class boundary. If the short-name extraction mishandled this and threw, or produced
  /// garbage, the OpenTelemetry span for every simply-named handler would be broken or missing.</summary>
  [Test]
  public async Task BeginHandlerTrace_SingleDotHandlerName_SpanNameKeepsTheFullNameAsync() {
    using var collector = new InMemorySpanCollector("Whizbang.Tracing");
    var options = new TracingOptions {
      Verbosity = TraceVerbosity.Verbose,
      Components = TraceComponents.Handlers,
      EnableOpenTelemetry = true,
    };
    var tracer = new Tracer(NullLogger<Tracer>.Instance, new _staticOptionsMonitor(options));

    tracer.BeginHandlerTrace("Foo.Bar", "SomeMessage", handlerCount: 1, isExplicit: false);
    tracer.EndHandlerTrace("Foo.Bar", "SomeMessage", HandlerStatus.Success,
      durationMs: 1.0, startTimestamp: 0, endTimestamp: 10, exception: null);

    var span = collector.FirstOrDefault(s => s.Name.StartsWith("Handler:", StringComparison.Ordinal));
    await Assert.That(span).IsNotNull();
    await Assert.That(span!.Name).IsEqualTo("Handler: Foo.Bar")
      .Because("a name with only one dot has no namespace segment left to strip — the short-name extraction must fall through to the full name rather than truncate it into something misleading");
  }
}
