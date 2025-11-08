using System.Diagnostics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Central ActivitySource for Whizbang observability.
/// Provides distributed tracing and diagnostic instrumentation.
/// AOT-friendly, zero-allocation, cross-platform.
/// </summary>
/// <remarks>
/// Use dotnet-trace or OpenTelemetry SDK to collect activities:
/// <code>
/// dotnet-trace collect --providers Whizbang.Execution
/// </code>
///
/// OpenTelemetry integration:
/// <code>
/// services.AddOpenTelemetry()
///   .WithTracing(builder => builder
///     .AddSource("Whizbang.Execution")
///     .AddSource("Whizbang.Transport"));
/// </code>
/// </remarks>
public static class WhizbangActivitySource {
  /// <summary>
  /// ActivitySource for execution strategies (SerialExecutor, ParallelExecutor).
  /// </summary>
  public static readonly ActivitySource Execution = new("Whizbang.Execution", "1.0.0");

  /// <summary>
  /// ActivitySource for transport operations (InProcessTransport, etc.).
  /// </summary>
  public static readonly ActivitySource Transport = new("Whizbang.Transport", "1.0.0");

  /// <summary>
  /// Records a defensive exception that should never occur in normal operation.
  /// Sets activity status to Error and adds exception details.
  /// </summary>
  /// <param name="activity">The current activity (may be null if no listener).</param>
  /// <param name="exception">The unexpected exception.</param>
  /// <param name="description">Description of where the exception occurred.</param>
  public static void RecordDefensiveException(Activity? activity, Exception exception, string description) {
    if (activity == null) {
      return;
    }

    activity.SetStatus(ActivityStatusCode.Error, description);
    activity.AddTag("exception.type", exception.GetType().FullName);
    activity.AddTag("exception.message", exception.Message);
    activity.AddTag("exception.stacktrace", exception.StackTrace);
    activity.AddTag("defensive.code", true);
  }

  /// <summary>
  /// Records a defensive cancellation that should never occur in normal operation.
  /// Sets activity status to Error and adds cancellation context.
  /// </summary>
  /// <param name="activity">The current activity (may be null if no listener).</param>
  /// <param name="context">Description of where the cancellation occurred.</param>
  public static void RecordDefensiveCancellation(Activity? activity, string context) {
    if (activity == null) {
      return;
    }

    activity.SetStatus(ActivityStatusCode.Error, $"Unexpected cancellation: {context}");
    activity.AddTag("cancellation.unexpected", true);
    activity.AddTag("defensive.code", true);
  }
}
