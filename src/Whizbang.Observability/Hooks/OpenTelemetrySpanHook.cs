using System.Diagnostics;
using System.Text.Json;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.Observability.Hooks;

/// <summary>
/// Message tag hook that creates and enriches OpenTelemetry spans for events
/// marked with <see cref="TelemetryTagAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// This hook integrates with OpenTelemetry's Activity API to create spans
/// that capture message processing telemetry. The span name, kind, and attributes
/// are derived from the <see cref="TelemetryTagAttribute"/> configuration.
/// </para>
/// <para>
/// Registration example:
/// <code>
/// services.AddWhizbang(options => {
///   options.Tags.UseHook&lt;TelemetryTagAttribute, OpenTelemetrySpanHook&gt;();
/// });
/// </code>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Mark an event for telemetry
/// [TelemetryTag(Tag = "payment-processed", SpanName = "ProcessPayment", Kind = SpanKind.Internal)]
/// public record PaymentProcessedEvent(Guid PaymentId, decimal Amount, string Currency) : IEvent;
///
/// // The hook will automatically create a span when this event is processed
/// </code>
/// </example>
/// <docs>observability/opentelemetry-integration</docs>
/// <tests>Whizbang.Observability.Tests/Hooks/OpenTelemetrySpanHookTests.cs</tests>
public sealed class OpenTelemetrySpanHook : IMessageTagHook<TelemetryTagAttribute> {
  /// <summary>
  /// The ActivitySource used for creating spans.
  /// </summary>
  public static readonly ActivitySource ActivitySource = new("Whizbang.MessageTags", "1.0.0");

  /// <summary>
  /// Creates or enriches an OpenTelemetry span for the tagged message.
  /// </summary>
  public ValueTask<JsonElement?> OnTaggedMessageAsync(
      TagContext<TelemetryTagAttribute> context,
      CancellationToken ct) {
    var attribute = context.Attribute;

    // Determine span name (use SpanName if specified, else Tag)
    var spanName = !string.IsNullOrEmpty(attribute.SpanName)
      ? attribute.SpanName
      : attribute.Tag;

    // Map SpanKind to ActivityKind
    var activityKind = _mapSpanKind(attribute.Kind);

    // Start a new activity (span)
    using var activity = ActivitySource.StartActivity(spanName, activityKind);

    if (activity is not null) {
      // Add standard attributes
      activity.SetTag("messaging.system", "whizbang");
      activity.SetTag("messaging.operation", "process");
      activity.SetTag("whizbang.tag", attribute.Tag);
      activity.SetTag("whizbang.message_type", context.MessageType.FullName);

      // Add scope attributes
      if (context.Scope is not null) {
        foreach (var (key, value) in context.Scope) {
          if (value is not null) {
            activity.SetTag($"whizbang.scope.{key.ToLowerInvariant()}", value.ToString());
          }
        }
      }

      // Add payload properties as attributes (from Properties array)
      if (attribute.Properties is { Length: > 0 }) {
        _addPayloadAttributes(activity, context.Payload, attribute.Properties);
      }

      // Record as event if configured
      if (attribute.RecordAsEvent) {
        var eventTags = new ActivityTagsCollection {
          { "event.name", context.MessageType.Name }
        };
        activity.AddEvent(new ActivityEvent(attribute.Tag, tags: eventTags));
      }
    }

    // Return null to pass original payload to next hook
    return ValueTask.FromResult<JsonElement?>(null);
  }

  private static ActivityKind _mapSpanKind(SpanKind kind) {
    return kind switch {
      SpanKind.Internal => ActivityKind.Internal,
      SpanKind.Server => ActivityKind.Server,
      SpanKind.Client => ActivityKind.Client,
      SpanKind.Producer => ActivityKind.Producer,
      SpanKind.Consumer => ActivityKind.Consumer,
      _ => ActivityKind.Internal
    };
  }

  private static void _addPayloadAttributes(
      Activity activity,
      JsonElement payload,
      string[] properties) {
    if (payload.ValueKind != JsonValueKind.Object) {
      return;
    }

    foreach (var propName in properties) {
      if (payload.TryGetProperty(propName, out var propValue)) {
        var attrName = $"whizbang.payload.{propName.ToLowerInvariant()}";
        var attrValue = propValue.ValueKind switch {
          JsonValueKind.String => propValue.GetString(),
          JsonValueKind.Number => propValue.GetRawText(),
          JsonValueKind.True => "true",
          JsonValueKind.False => "false",
          _ => propValue.GetRawText()
        };
        activity.SetTag(attrName, attrValue);
      }
    }
  }
}
