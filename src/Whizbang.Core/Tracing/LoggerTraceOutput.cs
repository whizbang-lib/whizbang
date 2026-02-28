using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Tracing;

/// <summary>
/// Trace output that writes to ILogger with structured logging.
/// </summary>
/// <remarks>
/// <para>
/// Explicit traces (via attributes or config) are logged at <see cref="LogLevel.Information"/>
/// with a <c>[TRACE]</c> prefix. Non-explicit traces are logged at <see cref="LogLevel.Debug"/>
/// with a <c>[trace]</c> prefix.
/// </para>
/// <para>
/// Failed operations are always logged at <see cref="LogLevel.Error"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Registration
/// services.AddSingleton&lt;ITraceOutput, LoggerTraceOutput&gt;();
///
/// // Output examples:
/// // [INF] [TRACE] Handlers: OrderCreatedEvent started (CorrelationId: abc-123)
/// // [DBG] [trace] Handlers: PaymentEvent started (CorrelationId: xyz-789)
/// // [INF] [TRACE] Handlers: OrderCreatedEvent Completed in 45.2ms
/// // [ERR] [TRACE] Handlers: PaymentEvent Failed in 12.1ms - InvalidOperationException: Payment failed
/// </code>
/// </example>
/// <docs>tracing/custom-outputs</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/LoggerTraceOutputTests.cs</tests>
public sealed partial class LoggerTraceOutput : ITraceOutput {
  private readonly ILogger<LoggerTraceOutput> _logger;

  /// <summary>
  /// Creates a new LoggerTraceOutput.
  /// </summary>
  /// <param name="logger">The logger to write traces to.</param>
  public LoggerTraceOutput(ILogger<LoggerTraceOutput> logger) {
    _logger = logger;
  }

  /// <inheritdoc />
  public void BeginTrace(TraceContext context) {
    var prefix = context.IsExplicit ? "[TRACE]" : "[trace]";

    if (context.IsExplicit) {
      LogBeginTraceExplicit(_logger, prefix, context.Component, context.MessageType, context.CorrelationId);
    } else {
      LogBeginTraceImplicit(_logger, prefix, context.Component, context.MessageType, context.CorrelationId);
    }

    if (context.HandlerName != null) {
      if (context.IsExplicit) {
        LogHandlerExplicit(_logger, prefix, context.HandlerName);
      } else {
        LogHandlerImplicit(_logger, prefix, context.HandlerName);
      }
    }
  }

  /// <inheritdoc />
  public void EndTrace(TraceContext context, TraceResult result) {
    var prefix = context.IsExplicit ? "[TRACE]" : "[trace]";
    var durationMs = result.Duration.TotalMilliseconds;

    if (!result.Success) {
      LogEndTraceError(_logger, result.Exception, prefix, context.Component, context.MessageType, result.Status, durationMs);
      return;
    }

    if (context.IsExplicit) {
      LogEndTraceExplicit(_logger, prefix, context.Component, context.MessageType, result.Status, durationMs);
    } else {
      LogEndTraceImplicit(_logger, prefix, context.Component, context.MessageType, result.Status, durationMs);
    }
  }

  [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Information,
      Message = "{Prefix} {Component}: {MessageType} started (CorrelationId: {CorrelationId})")]
  private static partial void LogBeginTraceExplicit(
      ILogger logger,
      string prefix,
      TraceComponents component,
      string messageType,
      string correlationId);

  [LoggerMessage(
      EventId = 2,
      Level = LogLevel.Debug,
      Message = "{Prefix} {Component}: {MessageType} started (CorrelationId: {CorrelationId})")]
  private static partial void LogBeginTraceImplicit(
      ILogger logger,
      string prefix,
      TraceComponents component,
      string messageType,
      string correlationId);

  [LoggerMessage(
      EventId = 3,
      Level = LogLevel.Information,
      Message = "{Prefix}   Handler: {HandlerName}")]
  private static partial void LogHandlerExplicit(
      ILogger logger,
      string prefix,
      string handlerName);

  [LoggerMessage(
      EventId = 4,
      Level = LogLevel.Debug,
      Message = "{Prefix}   Handler: {HandlerName}")]
  private static partial void LogHandlerImplicit(
      ILogger logger,
      string prefix,
      string handlerName);

  [LoggerMessage(
      EventId = 5,
      Level = LogLevel.Information,
      Message = "{Prefix} {Component}: {MessageType} {Status} in {Duration}ms")]
  private static partial void LogEndTraceExplicit(
      ILogger logger,
      string prefix,
      TraceComponents component,
      string messageType,
      string status,
      double duration);

  [LoggerMessage(
      EventId = 6,
      Level = LogLevel.Debug,
      Message = "{Prefix} {Component}: {MessageType} {Status} in {Duration}ms")]
  private static partial void LogEndTraceImplicit(
      ILogger logger,
      string prefix,
      TraceComponents component,
      string messageType,
      string status,
      double duration);

  [LoggerMessage(
      EventId = 7,
      Level = LogLevel.Error,
      Message = "{Prefix} {Component}: {MessageType} {Status} in {Duration}ms")]
  private static partial void LogEndTraceError(
      ILogger logger,
      Exception? exception,
      string prefix,
      TraceComponents component,
      string messageType,
      string status,
      double duration);
}
