using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;
using Whizbang.Observability.Hooks;

namespace Whizbang.Observability.DependencyInjection;

/// <summary>
/// Extension methods for registering OpenTelemetry hooks with the message tag system.
/// </summary>
/// <docs>observability/opentelemetry-integration#registration</docs>
public static class ObservabilityTagExtensions {
  /// <summary>
  /// Registers OpenTelemetry hooks for <see cref="TelemetryTagAttribute"/>
  /// and <see cref="MetricTagAttribute"/> message tags.
  /// </summary>
  /// <param name="options">The tag options to configure.</param>
  /// <returns>The tag options for method chaining.</returns>
  /// <remarks>
  /// <para>
  /// This is a convenience method that registers both:
  /// <list type="bullet">
  ///   <item><see cref="OpenTelemetrySpanHook"/> for <see cref="TelemetryTagAttribute"/> events</item>
  ///   <item><see cref="OpenTelemetryMetricHook"/> for <see cref="MetricTagAttribute"/> events</item>
  /// </list>
  /// </para>
  /// <para>
  /// For custom configuration, register hooks individually using
  /// <c>options.Tags.UseHook&lt;TAttribute, THook&gt;()</c>.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// services.AddWhizbang(options => {
  ///   // Register all OpenTelemetry hooks
  ///   options.Tags.UseOpenTelemetry();
  ///
  ///   // Or register individually with custom priority
  ///   options.Tags.UseHook&lt;TelemetryTagAttribute, OpenTelemetrySpanHook&gt;(priority: -50);
  /// });
  /// </code>
  /// </example>
  public static TagOptions UseOpenTelemetry(this TagOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    options.UseHook<TelemetryTagAttribute, OpenTelemetrySpanHook>();
    options.UseHook<MetricTagAttribute, OpenTelemetryMetricHook>();

    return options;
  }

  /// <summary>
  /// Registers only the OpenTelemetry span hook for tracing.
  /// </summary>
  /// <param name="options">The tag options to configure.</param>
  /// <param name="priority">Hook priority (default: -100, lower runs first).</param>
  /// <returns>The tag options for method chaining.</returns>
  public static TagOptions UseOpenTelemetryTracing(this TagOptions options, int priority = -100) {
    ArgumentNullException.ThrowIfNull(options);

    options.UseHook<TelemetryTagAttribute, OpenTelemetrySpanHook>(priority);

    return options;
  }

  /// <summary>
  /// Registers only the OpenTelemetry metric hook for metrics.
  /// </summary>
  /// <param name="options">The tag options to configure.</param>
  /// <param name="priority">Hook priority (default: -100, lower runs first).</param>
  /// <returns>The tag options for method chaining.</returns>
  public static TagOptions UseOpenTelemetryMetrics(this TagOptions options, int priority = -100) {
    ArgumentNullException.ThrowIfNull(options);

    options.UseHook<MetricTagAttribute, OpenTelemetryMetricHook>(priority);

    return options;
  }
}
