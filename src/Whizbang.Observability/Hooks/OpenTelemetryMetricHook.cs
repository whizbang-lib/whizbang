using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Whizbang.Core.Attributes;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;

namespace Whizbang.Observability.Hooks;

/// <summary>
/// Message tag hook that records OpenTelemetry metrics for events
/// marked with <see cref="MetricTagAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// This hook integrates with OpenTelemetry's Metrics API to record
/// counters, histograms, and gauges based on message processing.
/// The metric configuration is derived from <see cref="MetricTagAttribute"/>.
/// </para>
/// <para>
/// Registration example:
/// <code>
/// services.AddWhizbang(options => {
///   options.Tags.UseHook&lt;MetricTagAttribute, OpenTelemetryMetricHook&gt;();
/// });
/// </code>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Counter metric - increments on each event
/// [MetricTag(Tag = "order-created", MetricName = "orders.created", Type = MetricType.Counter)]
/// public record OrderCreatedEvent(Guid OrderId, string TenantId) : IEvent;
///
/// // Histogram metric - records a value from the event
/// [MetricTag(Tag = "order-amount", MetricName = "orders.amount", Type = MetricType.Histogram, ValueProperty = "Amount")]
/// public record OrderCompletedEvent(Guid OrderId, decimal Amount, string TenantId) : IEvent;
/// </code>
/// </example>
/// <docs>operations/observability/opentelemetry-integration#metrics</docs>
/// <tests>Whizbang.Observability.Tests/Hooks/OpenTelemetryMetricHookTests.cs</tests>
public sealed class OpenTelemetryMetricHook : IMessageTagHook<MetricTagAttribute> {
  /// <summary>
  /// The Meter used for creating metrics.
  /// </summary>
  public static readonly Meter Meter = new("Whizbang.MessageTags", "1.0.0");

  // Cache for instruments to avoid recreation
  private static readonly ConcurrentDictionary<string, Counter<long>> _counters = new();
  private static readonly ConcurrentDictionary<string, Histogram<double>> _histograms = new();

  /// <summary>
  /// Records an OpenTelemetry metric for the tagged message.
  /// </summary>
  public ValueTask<JsonElement?> OnTaggedMessageAsync(
      TagContext<MetricTagAttribute> context,
      CancellationToken ct) {
    var attribute = context.Attribute;
    var metricName = attribute.MetricName;

    // Build dimension tags from Properties
    var tags = _buildTags(context.Payload, attribute.Properties, context.Scope);

    switch (attribute.Type) {
      case MetricType.Counter:
        _recordCounter(metricName, tags);
        break;

      case MetricType.Histogram:
        var value = _extractValue(context.Payload, attribute.ValueProperty);
        if (value.HasValue) {
          _recordHistogram(metricName, value.Value, tags, attribute.Unit);
        }
        break;

      case MetricType.Gauge:
        // Gauges typically need external state management
        // For now, treat like histogram (record current value)
        var gaugeValue = _extractValue(context.Payload, attribute.ValueProperty);
        if (gaugeValue.HasValue) {
          _recordHistogram(metricName, gaugeValue.Value, tags, attribute.Unit);
        }
        break;
    }

    // Return null to pass original payload to next hook
    return ValueTask.FromResult<JsonElement?>(null);
  }

  private static void _recordCounter(string name, TagList tags) {
    var counter = _counters.GetOrAdd(name, n => Meter.CreateCounter<long>(n));
    counter.Add(1, tags);
  }

  private static void _recordHistogram(string name, double value, TagList tags, string? unit) {
    var histogram = _histograms.GetOrAdd(name, n => Meter.CreateHistogram<double>(n, unit));
    histogram.Record(value, tags);
  }

  private static TagList _buildTags(
      JsonElement payload,
      string[]? properties,
      IScopeContext? scope) {
    var tags = new TagList();

    _addPayloadPropertyTags(ref tags, payload, properties);
    _addScopeTags(ref tags, scope);

    return tags;
  }

  private static void _addPayloadPropertyTags(ref TagList tags, JsonElement payload, string[]? properties) {
    if (properties is not { Length: > 0 } || payload.ValueKind != JsonValueKind.Object) {
      return;
    }

    foreach (var propName in properties) {
      if (payload.TryGetProperty(propName, out var propValue)) {
        var tagValue = propValue.ValueKind switch {
          JsonValueKind.String => propValue.GetString(),
          JsonValueKind.Number => propValue.GetRawText(),
          JsonValueKind.True => "true",
          JsonValueKind.False => "false",
          _ => propValue.GetRawText()
        };
        tags.Add(propName.ToLowerInvariant(), tagValue);
      }
    }
  }

  private static void _addScopeTags(ref TagList tags, IScopeContext? scope) {
    if (scope?.Scope is null) {
      return;
    }

    var perspectiveScope = scope.Scope;
    _addScopeTagIfPresent(ref tags, "tenantid", perspectiveScope.TenantId);
    _addScopeTagIfPresent(ref tags, "userid", perspectiveScope.UserId);
    _addScopeTagIfPresent(ref tags, "customerid", perspectiveScope.CustomerId);
    _addScopeTagIfPresent(ref tags, "organizationid", perspectiveScope.OrganizationId);
  }

  private static void _addScopeTagIfPresent(ref TagList tags, string key, string? value) {
    if (!string.IsNullOrEmpty(value) && !tags.Any(t => string.Equals(t.Key, key, StringComparison.Ordinal))) {
      tags.Add(key, value);
    }
  }

  private static double? _extractValue(JsonElement payload, string? valueProperty) {
    if (string.IsNullOrEmpty(valueProperty)) {
      return 1.0; // Default to 1 for counters
    }

    if (payload.ValueKind != JsonValueKind.Object) {
      return null;
    }

    if (!payload.TryGetProperty(valueProperty, out var propValue)) {
      return null;
    }

    return propValue.ValueKind switch {
      JsonValueKind.Number when propValue.TryGetDouble(out var d) => d,
      JsonValueKind.String when double.TryParse(propValue.GetString(), out var d) => d,
      _ => null
    };
  }
}
