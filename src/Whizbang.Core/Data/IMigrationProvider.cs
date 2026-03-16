namespace Whizbang.Core.Data;

/// <summary>
/// Provides migration scripts and version metadata for database schema management.
/// Database implementations must provide this for hash-based change detection.
/// </summary>
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
