using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Tracing;

/// <summary>
/// Default implementation of <see cref="IHandlerMetrics"/> that emits metrics
/// via <see cref="WhizbangMetrics"/>.
/// </summary>
/// <remarks>
/// <para>
/// HandlerMetrics is registered as a singleton and uses <c>IOptionsMonitor</c>
/// for runtime configuration updates. Metrics are only recorded when
/// <see cref="MetricsOptions.Enabled"/> is true and
/// <see cref="MetricComponents.Handlers"/> is included in the components.
/// </para>
/// </remarks>
/// <docs>metrics/handlers</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/HandlerMetricsTests.cs</tests>
public sealed class HandlerMetrics : IHandlerMetrics {
  private readonly IOptionsMonitor<MetricsOptions> _options;

  /// <summary>
  /// Creates a new HandlerMetrics instance.
  /// </summary>
  /// <param name="options">Metrics configuration options.</param>
  public HandlerMetrics(IOptionsMonitor<MetricsOptions> options) {
    ArgumentNullException.ThrowIfNull(options);
    _options = options;
  }

  /// <inheritdoc/>
  public void RecordInvocation(
      string handlerName,
      string messageTypeName,
      HandlerStatus status,
      double durationMs,
      long startTimestamp,
      long endTimestamp) {
    var currentOptions = _options.CurrentValue;

    // Early return if metrics disabled for handlers
    if (!currentOptions.IsEnabled(MetricComponents.Handlers)) {
      return;
    }

    // Build tags based on configuration
    var tags = new TagList();

    if (currentOptions.IncludeHandlerNameTag && !string.IsNullOrEmpty(handlerName)) {
      tags.Add("handler", handlerName);
    }

    if (currentOptions.IncludeMessageTypeTag && !string.IsNullOrEmpty(messageTypeName)) {
      tags.Add("message_type", messageTypeName);
    }

    tags.Add("status", status.ToString().ToLowerInvariant());

    // Record invocation counter
    WhizbangMetrics.HandlerInvocations.Add(1, tags);

    // Record status-specific counters
    switch (status) {
      case HandlerStatus.Success:
        var successTags = new TagList();
        if (currentOptions.IncludeHandlerNameTag && !string.IsNullOrEmpty(handlerName)) {
          successTags.Add("handler", handlerName);
        }
        if (currentOptions.IncludeMessageTypeTag && !string.IsNullOrEmpty(messageTypeName)) {
          successTags.Add("message_type", messageTypeName);
        }
        WhizbangMetrics.HandlerSuccesses.Add(1, successTags);
        break;

      case HandlerStatus.Failed:
        var failureTags = new TagList();
        if (currentOptions.IncludeHandlerNameTag && !string.IsNullOrEmpty(handlerName)) {
          failureTags.Add("handler", handlerName);
        }
        if (currentOptions.IncludeMessageTypeTag && !string.IsNullOrEmpty(messageTypeName)) {
          failureTags.Add("message_type", messageTypeName);
        }
        WhizbangMetrics.HandlerFailures.Add(1, failureTags);
        break;

      case HandlerStatus.EarlyReturn:
        var earlyReturnTags = new TagList();
        if (currentOptions.IncludeHandlerNameTag && !string.IsNullOrEmpty(handlerName)) {
          earlyReturnTags.Add("handler", handlerName);
        }
        if (currentOptions.IncludeMessageTypeTag && !string.IsNullOrEmpty(messageTypeName)) {
          earlyReturnTags.Add("message_type", messageTypeName);
        }
        WhizbangMetrics.HandlerEarlyReturns.Add(1, earlyReturnTags);
        break;

      case HandlerStatus.Cancelled:
        // Cancellations are tracked via invocations with status=cancelled
        break;
    }

    // Record duration histogram
    WhizbangMetrics.HandlerDuration.Record(durationMs, tags);
  }
}

/// <summary>
/// No-op implementation of <see cref="IHandlerMetrics"/> used when metrics are disabled.
/// </summary>
/// <remarks>
/// This implementation has zero overhead and is used when
/// <see cref="MetricsOptions.Enabled"/> is false.
/// </remarks>
internal sealed class NullHandlerMetrics : IHandlerMetrics {
  /// <summary>
  /// Singleton instance.
  /// </summary>
  public static readonly NullHandlerMetrics Instance = new();

  private NullHandlerMetrics() { }

  /// <inheritdoc/>
  public void RecordInvocation(
      string handlerName,
      string messageTypeName,
      HandlerStatus status,
      double durationMs,
      long startTimestamp,
      long endTimestamp) {
    // No-op
  }
}
