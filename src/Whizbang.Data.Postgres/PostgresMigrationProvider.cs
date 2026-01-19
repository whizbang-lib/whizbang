using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Provides access to embedded SQL migration scripts.
/// Loads SQL files from the Migrations folder as embedded resources.
/// </summary>
/// <tests>No tests found</tests>
public class PostgresMigrationProvider {
  private readonly Assembly _assembly;
  private readonly string _resourcePrefix;

  /// <summary>
  /// Initializes a new instance of the PostgresMigrationProvider.
  /// Defaults to loading from Whizbang.Data.Postgres assembly.
  /// </summary>
  /// <tests>No tests found</tests>
  public PostgresMigrationProvider()
    : this(typeof(PostgresMigrationProvider).Assembly) {
  }

  /// <summary>
  /// Initializes a new instance with a specific assembly.
  /// Useful for loading migrations from different assemblies.
  /// </summary>
  /// <param name="assembly">Assembly containing embedded SQL resources</param>
  /// <tests>No tests found</tests>
  public PostgresMigrationProvider(Assembly assembly) {
    _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
    _resourcePrefix = $"{_assembly.GetName().Name}.Migrations.";
  }

  /// <summary>
  /// Gets all migration scripts in order (sorted by filename).
  /// Migration scripts are named 001_*.sql, 002_*.sql, etc.
  /// </summary>
  /// <returns>List of migration scripts with names and SQL content</returns>
  /// <tests>No tests found</tests>
  public List<MigrationScript> GetAllMigrations() {
    var resourceNames = _assembly
      .GetManifestResourceNames()
      .Where(name => name.StartsWith(_resourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
      .OrderBy(name => name)
      .ToList();

    var migrations = new List<MigrationScript>();

    foreach (var resourceName in resourceNames) {
      var scriptName = resourceName
        .Substring(_resourcePrefix.Length)
        .Replace(".sql", string.Empty);

      var sql = _readEmbeddedResource(resourceName);

      migrations.Add(new MigrationScript {
        Name = scriptName,
        Sql = sql
      });
    }

    return migrations;
  }

  /// <summary>
  /// Gets a specific migration script by name.
  /// </summary>
  /// <param name="scriptName">Name of the script (without .sql extension)</param>
  /// <returns>Migration script, or null if not found</returns>
  /// <tests>No tests found</tests>
  public MigrationScript? GetMigration(string scriptName) {
    var resourceName = $"{_resourcePrefix}{scriptName}.sql";

    if (!_assembly.GetManifestResourceNames().Contains(resourceName)) {
      return null;
    }

    var sql = _readEmbeddedResource(resourceName);

    return new MigrationScript {
      Name = scriptName,
      Sql = sql
    };
  }

  /// <summary>
  /// Executes all migrations against a database connection.
  /// Each migration is executed in a separate transaction.
  /// </summary>
  /// <param name="connectionString">PostgreSQL connection string</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task representing the async operation</returns>
  /// <tests>No tests found</tests>
  public async Task ExecuteAllMigrationsAsync(
    string connectionString,
    CancellationToken cancellationToken = default
  ) {
    var migrations = GetAllMigrations();

    using var connection = new Npgsql.NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    foreach (var migration in migrations) {
      await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
      try {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = migration.Sql;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
      } catch {
        await transaction.RollbackAsync(cancellationToken);
        throw;
      }
    }
  }

  private string _readEmbeddedResource(string resourceName) {
    using var stream = _assembly.GetManifestResourceStream(resourceName)
      ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");

    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }
}

/// <summary>
/// Represents a single SQL migration script.
/// </summary>
/// <tests>No tests found</tests>
public class MigrationScript {
  /// <summary>
  /// Name of the migration (e.g., "001_AlterOutboxTableForLeasing")
  /// </summary>
  /// <tests>No tests found</tests>
  public required string Name { get; init; }

  /// <summary>
  /// SQL content of the migration script
  /// </summary>
  /// <tests>No tests found</tests>
  public required string Sql { get; init; }
}
