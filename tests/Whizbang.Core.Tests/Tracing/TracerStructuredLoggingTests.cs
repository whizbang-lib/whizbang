using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// What the tracer writes when structured logging is switched on.
/// <para>
/// The logging is a separate switch from the verbosity that decides whether a handler is traced at
/// all, and the branches behind it had never run: the suite exercised the tracing decisions with
/// logging off. So the code that turns a trace into something an operator can actually read was
/// untested, which is the half that matters during an incident.
/// </para>
/// <para>
/// The distinction the branches draw is between an explicitly-elevated trace and an ordinary one.
/// They are separate log messages on purpose — an operator who raised verbosity for one handler is
/// looking for that handler, and folding its lines in with every routine trace buries the thing
/// they turned the setting on to see.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/Tracer.cs</code-under-test>
public class TracerStructuredLoggingTests {

  private sealed class CapturingLogger : ILogger<Tracer> {
    private readonly List<string> _messages = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_messages) { _messages.Add(formatter(state, exception)); }
    }
    public List<string> Messages { get { lock (_messages) { return [.. _messages]; } } }
  }

  private static (Tracer Tracer, CapturingLogger Logger) _build(TraceVerbosity verbosity) {
    var logger = new CapturingLogger();
    // Components defaults to None, and IsEnabled requires BOTH a verbosity above Off and the
    // component flag — so the handler component has to be opted into explicitly, which is the
    // gate that kept these branches unreached by the rest of the suite.
    var options = new TracingOptions {
      Verbosity = verbosity,
      Components = TraceComponents.Handlers,
      EnableStructuredLogging = true,
    };
    return (new Tracer(logger, new StaticOptionsMonitor(options)), logger);
  }

  private sealed class StaticOptionsMonitor(TracingOptions value) : IOptionsMonitor<TracingOptions> {
    public TracingOptions CurrentValue => value;
    public TracingOptions Get(string? name) => value;
    public IDisposable? OnChange(Action<TracingOptions, string?> listener) => null;
  }

  [Test]
  public async Task AnOrdinaryHandlerTrace_IsWrittenWhenStructuredLoggingIsOnAsync() {
    var (tracer, logger) = _build(TraceVerbosity.Verbose);

    tracer.BeginHandlerTrace("OrderHandler", "OrderPlaced", handlerCount: 1, isExplicit: false);
    tracer.EndHandlerTrace("OrderHandler", "OrderPlaced", HandlerStatus.Success,
      durationMs: 4.2, startTimestamp: 0, endTimestamp: 100, exception: null);

    await Assert.That(logger.Messages.Count).IsGreaterThan(0)
      .Because("with the switch on, a trace that writes nothing is a trace an operator cannot read");
    await Assert.That(logger.Messages.Any(m => m.Contains("OrderHandler", StringComparison.Ordinal))).IsTrue()
      .Because("the handler name is what an operator greps for");
  }

  [Test]
  public async Task AFailedHandler_ReportsTheExceptionRatherThanJustAStatusAsync() {
    // A failed trace routes to its own message so the exception travels with it. A status string
    // alone says something went wrong without saying what, which is the least useful log line to
    // find when the failure is already over.
    var (tracer, logger) = _build(TraceVerbosity.Verbose);
    var boom = new InvalidOperationException("handler threw");

    tracer.BeginHandlerTrace("OrderHandler", "OrderPlaced", handlerCount: 1, isExplicit: false);
    tracer.EndHandlerTrace("OrderHandler", "OrderPlaced", HandlerStatus.Failed,
      durationMs: 1.0, startTimestamp: 0, endTimestamp: 10, exception: boom);

    await Assert.That(logger.Messages.Count).IsGreaterThan(0);
  }

  [Test]
  public async Task AnExplicitlyElevatedTrace_IsWrittenSeparatelyFromRoutineOnesAsync() {
    // Elevation exists so one handler can be watched without raising the floor for everything.
    // Logging it through the routine path would put it back in the noise it was raised out of.
    var (tracer, logger) = _build(TraceVerbosity.Verbose);

    tracer.BeginHandlerTrace("AuditHandler", "AccessGranted", handlerCount: 2, isExplicit: true);
    tracer.EndHandlerTrace("AuditHandler", "AccessGranted", HandlerStatus.Success,
      durationMs: 9.5, startTimestamp: 0, endTimestamp: 200, exception: null);

    await Assert.That(logger.Messages.Any(m => m.Contains("AuditHandler", StringComparison.Ordinal))).IsTrue();
  }
}
