namespace Whizbang.Core.Tracing;

/// <summary>
/// Runtime configuration options for the metrics system.
/// </summary>
/// <remarks>
/// <para>
/// MetricsOptions is bound to the <c>Whizbang:Metrics</c> configuration section.
/// Changes are picked up at runtime via <c>IOptionsMonitor&lt;MetricsOptions&gt;</c>.
/// </para>
/// <para>
/// Metrics are emitted via <c>System.Diagnostics.Metrics</c> API and can be
/// collected by OpenTelemetry, Prometheus, or any compatible metrics backend.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // appsettings.json
/// {
///   "Whizbang": {
///     "Metrics": {
///       "Enabled": true,
///       "Components": ["Handlers", "EventStore", "Errors"],
///       "MeterName": "Whizbang"
///     }
///   }
/// }
///
/// // Programmatic configuration
/// services.AddWhizbang(options => {
///   options.Metrics.Enabled = true;
///   options.Metrics.Components = MetricComponents.Handlers
///                              | MetricComponents.EventStore
///                              | MetricComponents.Errors;
/// });
/// </code>
/// </example>
/// <docs>metrics/configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/MetricsOptionsTests.cs</tests>
public sealed class MetricsOptions {
  /// <summary>
  /// Gets or sets whether metrics collection is enabled.
  /// </summary>
  /// <remarks>
  /// Default is <c>false</c> for production safety. When disabled, all metric
  /// recording operations are no-ops with minimal overhead.
  /// </remarks>
  public bool Enabled { get; set; }

  /// <summary>
  /// Gets or sets which components should emit metrics.
  /// </summary>
  /// <remarks>
  /// Default is <see cref="MetricComponents.None"/>. Use <see cref="MetricComponents.All"/>
  /// for full visibility in development.
  /// </remarks>
  public MetricComponents Components { get; set; } = MetricComponents.None;

  /// <summary>
  /// Gets or sets the meter name for OpenTelemetry.
  /// </summary>
  /// <remarks>
  /// Default is <c>"Whizbang"</c>. This name is used when registering
  /// with <c>IMeterFactory</c> and appears in metric exports.
  /// </remarks>
  public string MeterName { get; set; } = "Whizbang";

  /// <summary>
  /// Gets or sets the meter version for OpenTelemetry.
  /// </summary>
  /// <remarks>
  /// Default is <c>null</c>, which uses the assembly version.
  /// </remarks>
  public string? MeterVersion { get; set; }

  /// <summary>
  /// Gets or sets whether to include handler name as a metric tag.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Default is <c>true</c>. When enabled, handler metrics include
  /// a <c>handler</c> tag with the handler class name.
  /// </para>
  /// <para>
  /// <strong>Warning:</strong> High-cardinality tags can cause metric explosion
  /// in systems with many handlers. Consider disabling for large codebases.
  /// </para>
  /// </remarks>
  public bool IncludeHandlerNameTag { get; set; } = true;

  /// <summary>
  /// Gets or sets whether to include message type as a metric tag.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Default is <c>true</c>. When enabled, metrics include a <c>message_type</c>
  /// tag with the message class name.
  /// </para>
  /// <para>
  /// <strong>Warning:</strong> High-cardinality tags can cause metric explosion
  /// in systems with many message types.
  /// </para>
  /// </remarks>
  public bool IncludeMessageTypeTag { get; set; } = true;

  /// <summary>
  /// Gets or sets histogram bucket boundaries for duration metrics (in milliseconds).
  /// </summary>
  /// <remarks>
  /// Default boundaries are optimized for typical handler durations:
  /// 1ms, 5ms, 10ms, 25ms, 50ms, 100ms, 250ms, 500ms, 1s, 2.5s, 5s, 10s.
  /// </remarks>
  public double[] DurationBuckets { get; set; } = [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];

  /// <summary>
  /// Checks if metrics are enabled for a given component.
  /// </summary>
  /// <param name="component">The component to check.</param>
  /// <returns><c>true</c> if metrics are enabled for the component; otherwise, <c>false</c>.</returns>
  public bool IsEnabled(MetricComponents component) {
    return Enabled && Components.HasFlag(component);
  }
}
