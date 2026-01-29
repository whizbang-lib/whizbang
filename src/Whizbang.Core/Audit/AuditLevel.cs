namespace Whizbang.Core.Audit;

/// <summary>
/// Audit severity level for categorizing audit entries.
/// </summary>
/// <docs>core-concepts/audit-logging#levels</docs>
public enum AuditLevel {
  /// <summary>
  /// Informational audit entry (default).
  /// </summary>
  Info,

  /// <summary>
  /// Warning-level audit entry indicating potential concern.
  /// </summary>
  Warning,

  /// <summary>
  /// Critical audit entry requiring attention.
  /// </summary>
  Critical
}
