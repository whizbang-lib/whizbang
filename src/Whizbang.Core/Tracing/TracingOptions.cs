namespace Whizbang.Core.Tracing;

/// <summary>
/// Configuration options for Whizbang tracing behavior.
/// Supports both programmatic configuration and IConfiguration binding (appsettings.json).
/// </summary>
/// <remarks>
/// <para>
/// Configure via <c>AddWhizbang()</c> options or bind from configuration:
/// </para>
/// <example>
/// <code>
/// // Programmatic configuration
/// services.AddWhizbang(options => {
///   options.Tracing.Verbosity = TraceVerbosity.Verbose;
///   options.Tracing.Components = TraceComponents.Handlers | TraceComponents.Lifecycle;
/// });
///
/// // Or via appsettings.json:
/// {
///   "Whizbang": {
///     "Tracing": {
///       "Verbosity": "Verbose",
///       "Components": "All",
///       "EnableOpenTelemetry": true,
///       "EnableStructuredLogging": true,
///       "TracedHandlers": {
///         "OrderReceptor": "Debug"
///       },
///       "TracedMessages": {
///         "ReseedSystemEvent": "Debug"
///       }
///     }
///   }
/// }
/// </code>
/// </example>
/// </remarks>
/// <docs>observability/tracing#configuration</docs>
public sealed class TracingOptions {
  /// <summary>
  /// Gets or sets the global verbosity level.
  /// Traces at or below this level are emitted.
  /// Default: <see cref="TraceVerbosity.Off"/> (no tracing).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Verbosity levels from lowest to highest:
  /// </para>
  /// <list type="bullet">
  /// <item><description><see cref="TraceVerbosity.Off"/> - No tracing</description></item>
  /// <item><description><see cref="TraceVerbosity.Minimal"/> - Errors and explicit markers only</description></item>
  /// <item><description><see cref="TraceVerbosity.Normal"/> - + Lifecycle transitions</description></item>
  /// <item><description><see cref="TraceVerbosity.Verbose"/> - + Handler discovery, Outbox/Inbox</description></item>
  /// <item><description><see cref="TraceVerbosity.Debug"/> - + Full payload, timing, perspectives</description></item>
  /// </list>
  /// </remarks>
  public TraceVerbosity Verbosity { get; set; } = TraceVerbosity.Off;

  /// <summary>
  /// Gets or sets which components emit traces.
  /// Only components included in this flags value will trace.
  /// Default: <see cref="TraceComponents.None"/> (no components).
  /// </summary>
  public TraceComponents Components { get; set; } = TraceComponents.None;

  /// <summary>
  /// Gets or sets whether to emit OpenTelemetry spans via ActivitySource.
  /// When true, traces appear in OpenTelemetry collectors (Aspire, App Insights, Jaeger, etc.).
  /// Default: true.
  /// </summary>
  public bool EnableOpenTelemetry { get; set; } = true;

  /// <summary>
  /// Gets or sets whether to emit structured log messages via ILogger.
  /// When true, trace information is also logged using source-generated LoggerMessage.
  /// Default: true.
  /// </summary>
  public bool EnableStructuredLogging { get; set; } = true;

  /// <summary>
  /// Gets handlers that should always be traced regardless of global verbosity.
  /// Key: handler name or pattern (e.g., "OrderReceptor", "Payment*", "MyApp.Orders.*").
  /// Value: verbosity level for that handler.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Pattern matching supports:
  /// </para>
  /// <list type="bullet">
  /// <item><description>Exact match: "OrderReceptor"</description></item>
  /// <item><description>Wildcard: "Payment*" matches PaymentHandler, PaymentValidator, etc.</description></item>
  /// <item><description>Namespace: "MyApp.Orders.*" matches all handlers in namespace</description></item>
  /// </list>
  /// </remarks>
  public Dictionary<string, TraceVerbosity> TracedHandlers { get; } = [];

  /// <summary>
  /// Gets messages that should always be traced regardless of global verbosity.
  /// Key: message type name or pattern (e.g., "CreateOrderCommand", "*Event").
  /// Value: verbosity level for handlers receiving that message.
  /// </summary>
  public Dictionary<string, TraceVerbosity> TracedMessages { get; } = [];

  /// <summary>
  /// Gets or sets whether to emit batch-level parent spans for background workers.
  /// When true, PerspectiveWorker emits a "PerspectiveWorker ProcessBatch" parent span
  /// that groups all perspective spans processed in the same polling cycle.
  /// Default: false (reduces noise in trace UI).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Enable this when debugging perspective processing to see which perspectives
  /// are processed together and understand batch boundaries.
  /// </para>
  /// </remarks>
  public bool EnableWorkerBatchSpans { get; set; }

  /// <summary>
  /// Gets or sets whether to emit per-event spans when processing perspectives.
  /// When true, each event applied to a perspective creates a child span showing
  /// the event type and processing time. Also adds summary tags to the RunAsync span.
  /// Default: false (reduces noise in trace UI).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Enable this when debugging perspective processing to see exactly which events
  /// are applied and in what order. This can generate many spans if a perspective
  /// processes large batches of events.
  /// </para>
  /// </remarks>
  public bool EnablePerspectiveEventSpans { get; set; }

  /// <summary>
  /// Checks whether tracing is enabled for a specific component.
  /// Returns true only if both verbosity is not Off AND the component is included.
  /// </summary>
  /// <param name="component">The component to check.</param>
  /// <returns>True if tracing should occur for this component.</returns>
  public bool IsEnabled(TraceComponents component) {
    return Verbosity != TraceVerbosity.Off && Components.HasFlag(component);
  }

  /// <summary>
  /// Checks whether a trace at the specified verbosity level should be emitted.
  /// Returns true if the current verbosity meets or exceeds the required level.
  /// </summary>
  /// <param name="requiredVerbosity">The minimum verbosity level needed for this trace.</param>
  /// <returns>True if the trace should be emitted.</returns>
  public bool ShouldTrace(TraceVerbosity requiredVerbosity) {
    return Verbosity != TraceVerbosity.Off && Verbosity >= requiredVerbosity;
  }
}
