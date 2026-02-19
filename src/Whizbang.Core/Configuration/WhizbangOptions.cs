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
