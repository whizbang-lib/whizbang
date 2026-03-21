namespace Whizbang.Core.Configuration;

/// <summary>
/// Configuration options for Whizbang runtime behavior.
/// </summary>
public class WhizbangOptions {
  /// <summary>
  /// When true, TrackedGuid validation is disabled project-wide.
  /// Methods accept raw Guid without tracking metadata validation.
  /// Default: false
  /// </summary>
  public bool DisableGuidTracking { get; set; }

  /// <summary>
  /// Severity level for time-ordering violations in IDs.
  /// Default: Warning
  /// </summary>
  public GuidOrderingSeverity GuidOrderingViolationSeverity { get; set; } = GuidOrderingSeverity.Warning;

  /// <summary>
  /// When true, the Whizbang ASCII art banner is displayed on service startup.
  /// Default: true
  /// </summary>
  public bool ShowBanner { get; set; } = true;

  /// <summary>
  /// When true, Whizbang will automatically generate a StreamId for events that implement
  /// IHasStreamId when their StreamId is Guid.Empty. This prevents events from being stored
  /// with empty StreamIds, which can cause perspective worker issues.
  /// Default: true
  /// </summary>
  /// <docs>fundamentals/events/stream-id#auto-generation</docs>
  public bool AutoGenerateStreamIds { get; set; } = true;
}

/// <summary>
/// Severity levels for GUID ordering validation violations.
/// </summary>
public enum GuidOrderingSeverity {
  /// <summary>
  /// Suppress all validation messages.
  /// </summary>
  None,

  /// <summary>
  /// Log at Info level.
  /// </summary>
  Info,

  /// <summary>
  /// Log at Warning level (default).
  /// </summary>
  Warning,

  /// <summary>
  /// Log at Error level and throw exception.
  /// </summary>
  Error
}
