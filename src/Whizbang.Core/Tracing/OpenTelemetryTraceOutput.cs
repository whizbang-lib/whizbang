using System.Diagnostics;

namespace Whizbang.Core.Tracing;

/// <summary>
/// Trace output that emits OpenTelemetry spans via System.Diagnostics.ActivitySource.
/// </summary>
/// <remarks>
/// <para>
/// Uses the standard .NET <see cref="ActivitySource"/> and <see cref="Activity"/> APIs
/// which are the foundation for OpenTelemetry in .NET. Activities are automatically
/// picked up by OpenTelemetry exporters when configured.
/// </para>
/// <para>
/// Standard tags are set on activities:
/// <list type="bullet">
///   <item><c>whizbang.message.id</c> - Message ID from envelope</item>
///   <item><c>whizbang.correlation_id</c> - Correlation ID for distributed tracing</item>
///   <item><c>whizbang.trace.explicit</c> - Whether this is an explicit trace</item>
///   <item><c>whizbang.duration_ms</c> - Operation duration in milliseconds</item>
///   <item><c>whizbang.status</c> - Result status (Completed, Failed, EarlyReturn)</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Registration
/// services.AddSingleton&lt;ITraceOutput, OpenTelemetryTraceOutput&gt;();
///
/// // Configure OpenTelemetry to listen
/// services.AddOpenTelemetry()
///   .WithTracing(builder => builder.AddSource("Whizbang.Tracing"));
/// </code>
/// </example>
/// <docs>tracing/custom-outputs</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/OpenTelemetryTraceOutputTests.cs</tests>
public sealed class OpenTelemetryTraceOutput : ITraceOutput {
  private static readonly ActivitySource _activitySource = new("Whizbang.Tracing");

  /// <summary>
  /// Gets the ActivitySource name used by this output.
  /// </summary>
  public static string ActivitySourceName => _activitySource.Name;

  /// <inheritdoc />
  public void BeginTrace(TraceContext context) {
    var activity = _activitySource.StartActivity(
        $"{context.Component}.{context.MessageType}",
        ActivityKind.Internal);

    if (activity != null) {
      activity.SetTag("whizbang.message.id", context.MessageId.ToString());
      activity.SetTag("whizbang.correlation_id", context.CorrelationId);
      activity.SetTag("whizbang.trace.explicit", context.IsExplicit);
      activity.SetTag("whizbang.component", context.Component.ToString());
      activity.SetTag("whizbang.message_type", context.MessageType);

      if (context.HandlerName != null) {
        activity.SetTag("whizbang.handler", context.HandlerName);
      }

      if (context.CausationId != null) {
        activity.SetTag("whizbang.causation_id", context.CausationId);
      }

      if (context.HopCount > 0) {
        activity.SetTag("whizbang.hop_count", context.HopCount);
      }

      if (context.ExplicitSource != null) {
        activity.SetTag("whizbang.trace.source", context.ExplicitSource);
      }
    }
  }

  /// <inheritdoc />
  public void EndTrace(TraceContext context, TraceResult result) {
    var activity = Activity.Current;
    if (activity == null) {
      return;
    }

    activity.SetTag("whizbang.duration_ms", result.Duration.TotalMilliseconds);
    activity.SetTag("whizbang.status", result.Status);

    if (result.Success) {
      activity.SetStatus(ActivityStatusCode.Ok);
    } else {
      activity.SetStatus(ActivityStatusCode.Error, result.Exception?.Message);
      if (result.Exception != null) {
        activity.SetTag("whizbang.error.type", result.Exception.GetType().Name);
        activity.SetTag("whizbang.error.message", result.Exception.Message);
      }
    }

    activity.Stop();
  }
}
