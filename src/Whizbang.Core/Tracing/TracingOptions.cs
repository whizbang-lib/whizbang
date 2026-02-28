namespace Whizbang.Core.Tracing;

/// <summary>
/// Runtime configuration options for the tracing system.
/// </summary>
/// <remarks>
/// <para>
/// TracingOptions is bound to the <c>Whizbang:Tracing</c> configuration section.
/// Changes are picked up at runtime via <c>IOptionsMonitor&lt;TracingOptions&gt;</c>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // appsettings.json
/// {
///   "Whizbang": {
///     "Tracing": {
///       "Verbosity": "Normal",
///       "Components": ["Http", "Handlers"],
///       "TracedHandlers": {
///         "OrderReceptor": "Debug"
///       }
///     }
///   }
/// }
///
/// // Programmatic configuration
/// services.AddWhizbang(options => {
///   options.Tracing.Verbosity = TraceVerbosity.Normal;
///   options.Tracing.Components = TraceComponents.Handlers;
/// });
/// </code>
/// </example>
/// <docs>tracing/configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/TracingOptionsTests.cs</tests>
public sealed class TracingOptions {
  /// <summary>
  /// Gets or sets the global verbosity level for tracing.
  /// </summary>
  /// <remarks>
  /// Default is <see cref="TraceVerbosity.Off"/> for production safety.
  /// </remarks>
  public TraceVerbosity Verbosity { get; set; } = TraceVerbosity.Off;

  /// <summary>
  /// Gets or sets which components should emit trace output.
  /// </summary>
  /// <remarks>
  /// Default is <see cref="TraceComponents.None"/>. Use <see cref="TraceComponents.All"/>
  /// for full visibility in development.
  /// </remarks>
  public TraceComponents Components { get; set; } = TraceComponents.None;

  /// <summary>
  /// Gets or sets whether to emit OpenTelemetry spans.
  /// </summary>
  /// <remarks>
  /// Default is <c>true</c>. Spans are emitted via <c>System.Diagnostics.ActivitySource</c>.
  /// </remarks>
  public bool EnableOpenTelemetry { get; set; } = true;

  /// <summary>
  /// Gets or sets whether to emit structured log messages.
  /// </summary>
  /// <remarks>
  /// Default is <c>true</c>. Uses <c>ILogger</c> with semantic logging.
  /// </remarks>
  public bool EnableStructuredLogging { get; set; } = true;

  /// <summary>
  /// Gets or sets handlers to trace with specific verbosity levels.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Keys support pattern matching:
  /// <list type="bullet">
  ///   <item><c>OrderReceptor</c> - Exact match</item>
  ///   <item><c>Order*</c> - Wildcard prefix</item>
  ///   <item><c>*Handler</c> - Wildcard suffix</item>
  ///   <item><c>MyApp.Orders.*</c> - Namespace</item>
  /// </list>
  /// </para>
  /// </remarks>
  public Dictionary<string, TraceVerbosity> TracedHandlers { get; set; } = [];

  /// <summary>
  /// Gets or sets messages to trace with specific verbosity levels.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Keys support the same pattern matching as <see cref="TracedHandlers"/>.
  /// </para>
  /// </remarks>
  public Dictionary<string, TraceVerbosity> TracedMessages { get; set; } = [];

  /// <summary>
  /// Checks if tracing is enabled for a given component.
  /// </summary>
  /// <param name="component">The component to check.</param>
  /// <returns><c>true</c> if tracing is enabled for the component; otherwise, <c>false</c>.</returns>
  public bool IsEnabled(TraceComponents component) {
    return Verbosity > TraceVerbosity.Off && Components.HasFlag(component);
  }

  /// <summary>
  /// Checks if the current verbosity level meets the required level for tracing.
  /// </summary>
  /// <param name="requiredVerbosity">The minimum verbosity required.</param>
  /// <returns><c>true</c> if tracing should occur; otherwise, <c>false</c>.</returns>
  public bool ShouldTrace(TraceVerbosity requiredVerbosity) {
    return Verbosity >= requiredVerbosity;
  }
}
