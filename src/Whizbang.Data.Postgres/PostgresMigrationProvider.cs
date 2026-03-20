using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Whizbang.Core.Data;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Provides access to embedded SQL migration scripts.
/// Loads SQL files from the Migrations folder as embedded resources.
/// Implements IMigrationProvider for hash-based change detection.
/// </summary>
public class PostgresMigrationProvider : IMigrationProvider {
  private readonly Assembly _assembly;
  private readonly string _resourcePrefix;
  private readonly string _schemaName;

  /// <summary>
  /// Initializes a new instance of the PostgresMigrationProvider.
  /// Defaults to loading from Whizbang.Data.Postgres assembly with "public" schema.
  /// </summary>
  public PostgresMigrationProvider()
    : this(typeof(PostgresMigrationProvider).Assembly, "public") {
  }

  /// <summary>
  /// Initializes a new instance with a specific assembly and schema name.
  /// </summary>
  /// <param name="assembly">Assembly containing embedded SQL resources</param>
  /// <param name="schemaName">PostgreSQL schema name to replace __SCHEMA__ placeholders with</param>
  public PostgresMigrationProvider(Assembly assembly, string schemaName = "public") {
    _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
    _schemaName = schemaName ?? throw new ArgumentNullException(nameof(schemaName));
    _resourcePrefix = $"{_assembly.GetName().Name}.Migrations.";
  }

  /// <summary>
  /// Library version baked in at compile time from Directory.Build.props.
  /// AOT-safe: no reflection needed.
  /// </summary>
  public string Version => BuildInfo.Version;

  /// <summary>
  /// Release notes for this version. Set manually for releases.
  /// </summary>
  public string? ReleaseNotes { get; init; }

  /// <summary>
  /// Gets all migration scripts in order (sorted by filename) with __SCHEMA__ replaced.
  /// </summary>
  public IReadOnlyList<MigrationScript> GetMigrations() {
    var resourceNames = _assembly
      .GetManifestResourceNames()
      .Where(name => name.StartsWith(_resourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
      .OrderBy(name => name)
      .ToList();

    var migrations = new List<MigrationScript>();

    foreach (var resourceName in resourceNames) {
      var scriptName = resourceName[_resourcePrefix.Length..]
        .Replace(".sql", string.Empty);

      var sql = _readEmbeddedResource(resourceName);

      // Replace __SCHEMA__ placeholder with configured schema name
      sql = sql.Replace("__SCHEMA__", _schemaName);

      migrations.Add(new MigrationScript(scriptName, sql));
    }

    return migrations;
  }

  /// <summary>
  /// Gets all migration scripts in order (sorted by filename).
  /// </summary>
  [Obsolete("Use GetMigrations() instead")]
  public List<Core.Data.MigrationScript> GetAllMigrations() => [.. GetMigrations()];

  /// <summary>
  /// Gets a specific migration script by name.
  /// </summary>
  /// <param name="scriptName">Name of the script (without .sql extension)</param>
  /// <returns>Migration script, or null if not found</returns>
  public MigrationScript? GetMigration(string scriptName) {
    var resourceName = $"{_resourcePrefix}{scriptName}.sql";

    if (!_assembly.GetManifestResourceNames().Contains(resourceName)) {
      return null;
    }

    var sql = _readEmbeddedResource(resourceName);
    sql = sql.Replace("__SCHEMA__", _schemaName);

    return new MigrationScript(scriptName, sql);
  }

  private string _readEmbeddedResource(string resourceName) {
    using var stream = _assembly.GetManifestResourceStream(resourceName)
      ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");

    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }
}
