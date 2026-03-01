namespace Whizbang.Core.Tracing;

/// <summary>
/// Interface for tracing handler invocations and message processing.
/// Provides observability into the Whizbang message handling pipeline.
/// </summary>
public interface ITracer {
  /// <summary>
  /// Begins a trace span for handler invocation.
  /// </summary>
  /// <param name="handlerName">Fully qualified name of the handler.</param>
  /// <param name="messageTypeName">Name of the message type being handled.</param>
  /// <param name="handlerCount">Total number of handlers for this message type.</param>
  /// <param name="isExplicit">True if handler has [TraceHandler] attribute or message has [TraceMessage].</param>
  void BeginHandlerTrace(string handlerName, string messageTypeName, int handlerCount, bool isExplicit);

  /// <summary>
  /// Ends a trace span for handler invocation.
  /// </summary>
  /// <param name="handlerName">Fully qualified name of the handler.</param>
  /// <param name="messageTypeName">Name of the message type being handled.</param>
  /// <param name="status">Completion status of the handler.</param>
  /// <param name="durationMs">Duration of the handler execution in milliseconds.</param>
  /// <param name="startTimestamp">Start timestamp (from Stopwatch.GetTimestamp).</param>
  /// <param name="endTimestamp">End timestamp (from Stopwatch.GetTimestamp).</param>
  /// <param name="exception">Exception if handler failed, null otherwise.</param>
  void EndHandlerTrace(
    string handlerName,
    string messageTypeName,
    HandlerStatus status,
    double durationMs,
    long startTimestamp,
    long endTimestamp,
    Exception? exception);
}
