namespace Whizbang.Core.Tracing;

/// <summary>
/// Main interface for the tracing system.
/// </summary>
/// <remarks>
/// <para>
/// ITracer coordinates tracing decisions and delegates output to registered
/// <see cref="ITraceOutput"/> implementations. Use via dependency injection.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class MyService {
///   private readonly ITracer _tracer;
///
///   public async Task ProcessAsync(IMessage message) {
///     if (_tracer.ShouldTrace(TraceComponents.Handlers, "MyHandler", "MyMessage")) {
///       using var scope = _tracer.BeginTrace(context);
///       try {
///         await DoWork();
///         scope.Complete();
///       } catch (Exception ex) {
///         scope.Fail(ex);
///         throw;
///       }
///     } else {
///       await DoWork();
///     }
///   }
/// }
/// </code>
/// </example>
/// <docs>tracing/custom-outputs</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/ITracerTests.cs</tests>
public interface ITracer {
  /// <summary>
  /// Checks if tracing should occur for the given component, handler, and message.
  /// </summary>
  /// <param name="component">The component to check.</param>
  /// <param name="handlerName">The handler name (for pattern matching), or null.</param>
  /// <param name="messageType">The message type name (for pattern matching), or null.</param>
  /// <returns><c>true</c> if tracing should occur; otherwise, <c>false</c>.</returns>
  bool ShouldTrace(TraceComponents component, string? handlerName = null, string? messageType = null);

  /// <summary>
  /// Gets the effective verbosity for a handler and message, considering attributes and config.
  /// </summary>
  /// <param name="handlerName">The handler name, or null.</param>
  /// <param name="messageType">The message type name, or null.</param>
  /// <param name="attributeVerbosity">Verbosity from attribute, or null.</param>
  /// <returns>The effective verbosity to use for tracing.</returns>
  TraceVerbosity GetEffectiveVerbosity(string? handlerName, string? messageType, TraceVerbosity? attributeVerbosity);

  /// <summary>
  /// Begins a trace operation and returns a scope for completion.
  /// </summary>
  /// <param name="context">The trace context.</param>
  /// <returns>A trace scope that should be disposed when the operation ends.</returns>
  ITraceScope BeginTrace(TraceContext context);

  /// <summary>
  /// Begins a handler trace for generated code.
  /// Called at the start of handler invocation when [TraceHandler] is present or global tracing is enabled.
  /// </summary>
  /// <param name="handlerName">Fully qualified handler class name.</param>
  /// <param name="messageTypeName">Simple name of the message type.</param>
  /// <param name="attributeVerbosity">Verbosity from [TraceHandler] attribute, or null if not present.</param>
  /// <param name="hasTraceAttribute">True if [TraceHandler] attribute is present.</param>
  void BeginHandlerTrace(
      string handlerName,
      string messageTypeName,
      int? attributeVerbosity,
      bool hasTraceAttribute);

  /// <summary>
  /// Ends a handler trace for generated code.
  /// Called at the end of handler invocation to record outcome and timing.
  /// </summary>
  /// <param name="handlerName">Fully qualified handler class name.</param>
  /// <param name="messageTypeName">Simple name of the message type.</param>
  /// <param name="status">Completion status of the handler.</param>
  /// <param name="durationMs">Duration in milliseconds.</param>
  /// <param name="startTimestamp">Start timestamp from Stopwatch.GetTimestamp().</param>
  /// <param name="endTimestamp">End timestamp from Stopwatch.GetTimestamp().</param>
  /// <param name="exception">Exception if handler failed, or null.</param>
  void EndHandlerTrace(
      string handlerName,
      string messageTypeName,
      HandlerStatus status,
      double durationMs,
      long startTimestamp,
      long endTimestamp,
      Exception? exception);
}

/// <summary>
/// Represents an active trace operation scope.
/// </summary>
/// <remarks>
/// <para>
/// ITraceScope tracks the duration and outcome of a traced operation.
/// Always call one of <see cref="Complete"/>, <see cref="Fail"/>, or
/// <see cref="EarlyReturn"/> before disposing, or disposal will record
/// an incomplete trace.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var scope = _tracer.BeginTrace(context);
/// try {
///   var result = await handler.HandleAsync(message, ct);
///   if (result == null) {
///     scope.EarlyReturn();
///   } else {
///     scope.Complete();
///   }
///   return result;
/// } catch (Exception ex) {
///   scope.Fail(ex);
///   throw;
/// }
/// </code>
/// </example>
public interface ITraceScope : IDisposable {
  /// <summary>
  /// Marks the trace as successfully completed.
  /// </summary>
  void Complete();

  /// <summary>
  /// Marks the trace as failed with an exception.
  /// </summary>
  /// <param name="exception">The exception that caused the failure.</param>
  void Fail(Exception exception);

  /// <summary>
  /// Marks the trace as completed with early return (handler skipped processing).
  /// </summary>
  void EarlyReturn();
}
