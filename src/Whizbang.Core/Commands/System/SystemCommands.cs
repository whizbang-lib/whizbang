namespace Whizbang.Core.Commands.System;

/// <summary>
/// System commands that all services automatically subscribe to.
/// These framework-level commands are routed via the "whizbang.system.commands" namespace.
/// </summary>
/// <remarks>
/// <para>
/// All services using SharedTopicInboxStrategy automatically include system commands
/// in their subscription filter (whizbang.system.commands.#).
/// </para>
/// <para>
/// To send a system command to all services:
/// <code>
/// await dispatcher.SendAsync(new RebuildPerspectiveCommand("OrderSummary"));
/// </code>
/// </para>
/// </remarks>
/// <docs>core-concepts/routing#system-commands</docs>

/// <summary>
/// Command to rebuild a specific perspective across all services.
/// Services with the matching perspective will reprocess from their last checkpoint.
/// </summary>
/// <param name="PerspectiveName">Name of the perspective to rebuild.</param>
/// <param name="FromEventId">Optional event ID to start rebuilding from. If null, rebuilds from last checkpoint.</param>
/// <docs>core-concepts/perspectives#rebuild</docs>
public record RebuildPerspectiveCommand(
    string PerspectiveName,
    long? FromEventId = null
) : ICommand;

/// <summary>
/// Command to clear cached data across all services.
/// </summary>
/// <param name="CacheKey">Optional specific cache key to clear. If null, clears all caches.</param>
/// <param name="CacheRegion">Optional cache region/namespace to target.</param>
/// <docs>components/caching#clear-cache</docs>
public record ClearCacheCommand(
    string? CacheKey = null,
    string? CacheRegion = null
) : ICommand;

/// <summary>
/// Command to collect and report diagnostics from all services.
/// </summary>
/// <param name="Type">Type of diagnostics to collect.</param>
/// <param name="CorrelationId">Optional correlation ID for tracking diagnostic responses.</param>
/// <docs>observability/diagnostics#system-diagnostics</docs>
public record DiagnosticsCommand(
    DiagnosticType Type,
    Guid? CorrelationId = null
) : ICommand;

/// <summary>
/// Type of diagnostics to collect from services.
/// </summary>
public enum DiagnosticType {
  /// <summary>
  /// Basic health check - is the service responsive?
  /// </summary>
  HealthCheck,

  /// <summary>
  /// Memory usage, thread count, and resource metrics.
  /// </summary>
  ResourceMetrics,

  /// <summary>
  /// Current state of message processing pipelines.
  /// </summary>
  PipelineStatus,

  /// <summary>
  /// Perspective and projection state information.
  /// </summary>
  PerspectiveStatus,

  /// <summary>
  /// Full diagnostic dump including all above categories.
  /// </summary>
  Full
}

/// <summary>
/// Command to pause message processing across all services.
/// Used for coordinated maintenance operations.
/// </summary>
/// <param name="DurationSeconds">Optional duration in seconds after which processing resumes automatically.</param>
/// <param name="Reason">Reason for pausing (for logging/audit).</param>
/// <docs>core-concepts/lifecycle#pause-resume</docs>
public record PauseProcessingCommand(
    int? DurationSeconds = null,
    string? Reason = null
) : ICommand;

/// <summary>
/// Command to resume message processing across all services.
/// </summary>
/// <param name="Reason">Reason for resuming (for logging/audit).</param>
/// <docs>core-concepts/lifecycle#pause-resume</docs>
public record ResumeProcessingCommand(
    string? Reason = null
) : ICommand;
