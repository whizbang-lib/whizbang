using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Whizbang.Core.Data;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Data.Schema;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:Constructor_WithConnectionStringOnly_InitializesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:Constructor_WithPerspectiveSql_InitializesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_NoPerspectiveSql_ExecutesInfrastructureOnlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_WithPerspectiveSql_ExecutesBothSchemasAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_PerspectiveSqlNull_SkipsPerspectiveSqlAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_PerspectiveSqlEmpty_SkipsPerspectiveSqlAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchema_NoPerspectiveSql_ExecutesInfrastructureOnlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchema_WithPerspectiveSql_ExecutesBothSchemasAsync</tests>
/// Handles automatic initialization of the Whizbang PostgreSQL schema.
/// Uses hash-based change detection to skip unchanged migrations.
/// </summary>
public sealed class PostgresSchemaInitializer {
  private readonly string _connectionString;
  private readonly string? _perspectiveSchemaSql;
  private readonly IMigrationProvider _migrationProvider;

  public PostgresSchemaInitializer(string connectionString, string? perspectiveSchemaSql = null)
    : this(connectionString, perspectiveSchemaSql, new PostgresMigrationProvider()) {
  }

  public PostgresSchemaInitializer(string connectionString, string? perspectiveSchemaSql, IMigrationProvider migrationProvider) {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    _connectionString = connectionString;
    _perspectiveSchemaSql = perspectiveSchemaSql;
    _migrationProvider = migrationProvider ?? throw new ArgumentNullException(nameof(migrationProvider));
  }

  /// <summary>
  /// Initializes the Whizbang schema by generating SQL from C# schema definitions.
  /// Optionally executes perspective schema SQL if provided.
  /// Uses hash-based change detection to skip unchanged migrations.
  /// This method is idempotent - it can be called multiple times safely.
  /// </summary>
  public async Task InitializeSchemaAsync(CancellationToken cancellationToken = default) {
    // Generate schema SQL from C# schema definitions
    var schemaConfig = new SchemaConfiguration(
      InfrastructurePrefix: "wh_",
      PerspectivePrefix: "wh_per_"
    );
    var schemaSql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(schemaConfig);

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    // Execute infrastructure schema (event store, inbox, outbox, etc.)
    await using var command = connection.CreateCommand();
    command.CommandText = schemaSql;
    command.CommandTimeout = 30; // 30 seconds should be plenty for schema creation
    await command.ExecuteNonQueryAsync(cancellationToken);

    // Execute migration SQL files with hash-based change detection
    await _executeMigrationsWithHashDetectionAsync(connection, cancellationToken);

    // Execute perspective schema if provided
    if (!string.IsNullOrWhiteSpace(_perspectiveSchemaSql)) {
      await using var perspectiveCommand = connection.CreateCommand();
      perspectiveCommand.CommandText = _perspectiveSchemaSql;
      perspectiveCommand.CommandTimeout = 30;
      await perspectiveCommand.ExecuteNonQueryAsync(cancellationToken);
    }
  }

  /// <summary>
  /// Synchronous version of InitializeSchemaAsync for use during application startup.
  /// Optionally executes perspective schema SQL if provided.
  /// </summary>
  public void InitializeSchema() {
    InitializeSchemaAsync().GetAwaiter().GetResult();
  }

  private async Task _executeMigrationsWithHashDetectionAsync(NpgsqlConnection connection, CancellationToken cancellationToken) {
    var migrations = _migrationProvider.GetMigrations();
    if (migrations.Count == 0) {
      return;
    }

    // Step 1: Execute bootstrap migration (000) unconditionally — creates tracking tables
    var bootstrap = migrations.FirstOrDefault(m => m.Name.StartsWith("000", StringComparison.Ordinal));
    if (bootstrap != null) {
      await using var bootstrapCmd = connection.CreateCommand();
      bootstrapCmd.CommandText = bootstrap.Sql;
      bootstrapCmd.CommandTimeout = 30;
      await bootstrapCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // Step 2: Upsert version row
    var versionId = await _upsertVersionAsync(connection, cancellationToken);

    // Step 3: Hash-check remaining migrations
    foreach (var migration in migrations.Where(m => !m.Name.StartsWith("000", StringComparison.Ordinal))) {
      var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(migration.Sql)));

      var existingHash = await _getExistingHashAsync(connection, migration.Name, cancellationToken);

      if (existingHash == hash) {
        // Hash matches — skip execution, record as Skipped
        await _updateMigrationStatusAsync(connection, migration.Name, versionId, 3, "Skipped (hash unchanged)", cancellationToken);
        continue;
      }

      var isUpdate = existingHash != null;
      try {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = migration.Sql;
        cmd.CommandTimeout = 30;
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        var status = isUpdate ? 2 : 1; // Updated vs Applied
        var desc = isUpdate
          ? $"Updated from hash {existingHash![..8]}..."
          : "First apply";

        await _upsertMigrationAsync(connection, migration.Name, hash, versionId, status, desc, cancellationToken);
      } catch (Exception ex) {
        // Record failure
        await _upsertMigrationAsync(connection, migration.Name, hash, versionId, -1, $"Failed: {ex.Message}", cancellationToken);
        throw; // Re-throw to halt migration
      }
    }
  }

  private async Task<int> _upsertVersionAsync(NpgsqlConnection connection, CancellationToken cancellationToken) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_schema_versions (library_version, notes)
      VALUES (@version, @notes)
      ON CONFLICT (library_version) DO UPDATE SET notes = EXCLUDED.notes
      RETURNING id";
    cmd.Parameters.AddWithValue("version", _migrationProvider.Version);
    cmd.Parameters.AddWithValue("notes", (object?)_migrationProvider.ReleaseNotes ?? DBNull.Value);
    cmd.CommandTimeout = 10;

    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return (int)result!;
  }

  private static async Task<string?> _getExistingHashAsync(NpgsqlConnection connection, string fileName, CancellationToken cancellationToken) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT content_hash FROM wh_schema_migrations WHERE file_name = @name";
    cmd.Parameters.AddWithValue("name", fileName);
    cmd.CommandTimeout = 10;

    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result as string;
  }

  private static async Task _updateMigrationStatusAsync(NpgsqlConnection connection, string fileName, int versionId, int status, string desc, CancellationToken cancellationToken) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      UPDATE wh_schema_migrations
      SET status = @status, status_description = @desc, version_id = @versionId, updated_at = NOW()
      WHERE file_name = @name";
    cmd.Parameters.AddWithValue("name", fileName);
    cmd.Parameters.AddWithValue("status", (short)status);
    cmd.Parameters.AddWithValue("desc", desc);
    cmd.Parameters.AddWithValue("versionId", versionId);
    cmd.CommandTimeout = 10;
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private static async Task _upsertMigrationAsync(NpgsqlConnection connection, string fileName, string hash, int versionId, int status, string desc, CancellationToken cancellationToken) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_schema_migrations (file_name, content_hash, version_id, status, status_description)
      VALUES (@name, @hash, @versionId, @status, @desc)
      ON CONFLICT (file_name) DO UPDATE SET
        content_hash = EXCLUDED.content_hash, version_id = EXCLUDED.version_id,
        status = EXCLUDED.status, status_description = EXCLUDED.status_description, updated_at = NOW()";
    cmd.Parameters.AddWithValue("name", fileName);
    cmd.Parameters.AddWithValue("hash", hash);
    cmd.Parameters.AddWithValue("versionId", versionId);
    cmd.Parameters.AddWithValue("status", (short)status);
    cmd.Parameters.AddWithValue("desc", desc);
    cmd.CommandTimeout = 10;
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }
}
