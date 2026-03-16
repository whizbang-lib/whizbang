namespace Whizbang.Core.Data;

/// <summary>
/// Provides migration scripts and version metadata for database schema management.
/// Database implementations must provide this for hash-based change detection.
/// </summary>
/// <docs>infrastructure/migrations</docs>
public interface IMigrationProvider {
  /// <summary>
  /// Library version (e.g., "0.9.4-local.61"). Must be AOT-safe (no reflection).
  /// </summary>
  string Version { get; }

  /// <summary>
  /// Release notes for this version. Stored in wh_schema_versions.
  /// </summary>
  string? ReleaseNotes { get; }

  /// <summary>
  /// Gets all migration scripts in execution order.
  /// </summary>
  IReadOnlyList<MigrationScript> GetMigrations();
}

/// <summary>
/// Represents a single SQL migration script.
/// </summary>
public sealed record MigrationScript(string Name, string Sql);

/// <summary>
/// A planned migration step from a dry-run/preview operation.
/// </summary>
public sealed record MigrationStep(
    string Name,
    MigrationAction Action,
    string? OldHash,
    string NewHash,
    string[]? AddedColumns,
    string[]? RemovedColumns);

/// <summary>
/// The action that would be taken for a migration step.
/// </summary>
public enum MigrationAction {
  /// <summary>Migration unchanged, will be skipped.</summary>
  Skip,
  /// <summary>New migration, will be applied.</summary>
  Apply,
  /// <summary>Migration changed, will be re-executed (functions, etc.).</summary>
  Update,
  /// <summary>Table migration via column-copy blue-green swap.</summary>
  BlueGreenColumnCopy,
  /// <summary>Table migration requiring full event replay.</summary>
  BlueGreenEventReplay
}

/// <summary>
/// Result of a migration preview/dry-run operation.
/// </summary>
public sealed record MigrationPlan(IReadOnlyList<MigrationStep> Steps);
