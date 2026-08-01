using Whizbang.Core.Attributes;

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
/// <docs>fundamentals/dispatcher/routing#system-commands</docs>

/// <summary>
/// Command to rebuild one or more perspectives. Supports multiple modes and optional stream filtering.
/// </summary>
/// <param name="PerspectiveNames">Perspectives to rebuild. Null = all registered perspectives.</param>
/// <param name="Mode">Rebuild mode: BlueGreen (new table + swap), InPlace (truncate + replay).</param>
/// <param name="IncludeStreamIds">Optional: only rebuild these specific streams. Null = all streams.</param>
/// <param name="ExcludeStreamIds">Optional: exclude these streams from rebuild. Null = no exclusions.</param>
/// <param name="FromEventId">Optional: start replaying from this event ID. Null = from beginning.</param>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
[PinnedId("69430ea4-edbe-4a5f-890d-cd25cfe66766")]
public record RebuildPerspectiveCommand(
    string[]? PerspectiveNames = null,
    Perspectives.RebuildMode Mode = Perspectives.RebuildMode.BlueGreen,
    Guid[]? IncludeStreamIds = null,
    Guid[]? ExcludeStreamIds = null,
    long? FromEventId = null
) : ICommand, Messaging.IControlPlaneMessage;

/// <summary>
/// Command to cancel an in-progress perspective rebuild.
/// </summary>
/// <param name="PerspectiveName">Name of the perspective whose rebuild should be cancelled.</param>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
[PinnedId("5b8636e8-5c28-4520-8013-bf1e95b7f783")]
public record CancelPerspectiveRebuildCommand(
    string PerspectiveName
) : ICommand;

/// <summary>
/// Command to clear cached data across all services.
/// </summary>
/// <param name="CacheKey">Optional specific cache key to clear. If null, clears all caches.</param>
/// <param name="CacheRegion">Optional cache region/namespace to target.</param>
/// <docs>data/caching#clear-cache</docs>
[PinnedId("db190b57-50ca-4748-9929-0f090dba9e28")]
public record ClearCacheCommand(
    string? CacheKey = null,
    string? CacheRegion = null
) : ICommand;

/// <summary>
/// Command to collect and report diagnostics from all services.
/// </summary>
/// <param name="Type">Type of diagnostics to collect.</param>
/// <param name="CorrelationId">Optional correlation ID for tracking diagnostic responses.</param>
/// <docs>operations/observability/diagnostics#system-diagnostics</docs>
[PinnedId("48bd69ed-628a-4a04-b9fc-5fa6ecd13899")]
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
/// <docs>fundamentals/lifecycle/lifecycle#pause-resume</docs>
[PinnedId("7475daa0-8b26-40fe-af25-215ac42e2526")]
public record PauseProcessingCommand(
    int? DurationSeconds = null,
    string? Reason = null
) : ICommand;

/// <summary>
/// Command to resume message processing across all services.
/// </summary>
/// <param name="Reason">Reason for resuming (for logging/audit).</param>
/// <docs>fundamentals/lifecycle/lifecycle#pause-resume</docs>
[PinnedId("c50126f7-aa4f-4cb8-b46f-36f589e0c36b")]
public record ResumeProcessingCommand(
    string? Reason = null
) : ICommand;
