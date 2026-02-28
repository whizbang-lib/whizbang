namespace Whizbang.Core.Tracing;

/// <summary>
/// Abstraction for trace output destinations.
/// </summary>
/// <remarks>
/// <para>
/// Implement this interface to create custom trace outputs. Built-in implementations
/// include <c>LoggerTraceOutput</c> (ILogger) and <c>OpenTelemetryTraceOutput</c>
/// (System.Diagnostics.ActivitySource).
/// </para>
/// <para>
/// Multiple outputs can be registered and all receive trace events simultaneously.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class ConsoleTraceOutput : ITraceOutput {
///   public void BeginTrace(TraceContext context) {
///     Console.WriteLine($"BEGIN: {context.MessageType}");
///   }
///
///   public void EndTrace(TraceContext context, TraceResult result) {
///     Console.WriteLine($"END: {context.MessageType} ({result.Status})");
///   }
/// }
///
/// // Registration
/// services.AddSingleton&lt;ITraceOutput, ConsoleTraceOutput&gt;();
/// </code>
/// </example>
/// <docs>tracing/custom-outputs</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/ITraceOutputTests.cs</tests>
public interface ITraceOutput {
  /// <summary>
  /// Called when a trace operation begins.
  /// </summary>
  /// <param name="context">Context containing trace metadata.</param>
  void BeginTrace(TraceContext context);

  /// <summary>
  /// Called when a trace operation ends.
  /// </summary>
  /// <param name="context">Context containing trace metadata.</param>
  /// <param name="result">Result of the traced operation.</param>
  void EndTrace(TraceContext context, TraceResult result);
}
