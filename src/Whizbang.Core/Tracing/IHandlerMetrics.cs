namespace Whizbang.Core.Tracing;

/// <summary>
/// Interface for recording handler metrics.
/// Implementations emit metrics to OpenTelemetry or other monitoring systems.
/// </summary>
/// <remarks>
/// <para>
/// This interface is resolved from DI during handler invocation when [MetricHandler]
/// is present on a receptor. Metrics include:
/// </para>
/// <list type="bullet">
/// <item><description><c>whizbang.handler.invocations</c> - Total invocations</description></item>
/// <item><description><c>whizbang.handler.successes</c> - Successful completions</description></item>
/// <item><description><c>whizbang.handler.failures</c> - Failed invocations</description></item>
/// <item><description><c>whizbang.handler.duration</c> - Execution time histogram</description></item>
/// <item><description><c>whizbang.handler.active</c> - Currently executing count</description></item>
/// </list>
/// </remarks>
/// <docs>tracing/attributes</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/IHandlerMetricsTests.cs</tests>
public interface IHandlerMetrics {
  /// <summary>
  /// Records a handler invocation with timing information.
  /// </summary>
  /// <param name="handlerName">Fully qualified handler class name.</param>
  /// <param name="messageTypeName">Simple name of the message type.</param>
  /// <param name="status">Completion status of the handler.</param>
  /// <param name="durationMs">Duration in milliseconds.</param>
  /// <param name="startTimestamp">Start timestamp from Stopwatch.GetTimestamp().</param>
  /// <param name="endTimestamp">End timestamp from Stopwatch.GetTimestamp().</param>
  void RecordInvocation(
      string handlerName,
      string messageTypeName,
      HandlerStatus status,
      double durationMs,
      long startTimestamp,
      long endTimestamp);
}
