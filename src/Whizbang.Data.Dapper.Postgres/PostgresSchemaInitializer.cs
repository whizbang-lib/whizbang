using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using Whizbang.Core.Data;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Data.Schema;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:Constructor_WithConnectionStringOnly_InitializesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:Constructor_WithPerspectiveSql_InitializesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_NoPerspectiveSql_ExecutesInfrastructureOnlyAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_WithPerspectiveSql_ExecutesBothSchemasAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_PerspectiveSqlNull_SkipsPerspectiveSqlAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_PerspectiveSqlEmpty_SkipsPerspectiveSqlAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchema_NoPerspectiveSql_ExecutesInfrastructureOnlyAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchema_WithPerspectiveSql_ExecutesBothSchemasAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:Constructor_WithPerspectiveEntries_InitializesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_WithPerspectiveEntries_CreatesTablesAndRecordsHashAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_WithUnchangedPerspective_SkipsExecutionAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_WithChangedPerspective_UpdatesHashAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_WithMultiplePerspectives_TracksIndependentlyAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_WithOneChangedPerspective_OnlyUpdatesChangedAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:InitializeSchemaAsync_WithEmptyEntries_ExecutesInfrastructureOnlyAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTests.cs:Constructor_WithNullPerspectiveEntries_ThrowsAsync</tests>
/// Handles automatic initialization of the Whizbang PostgreSQL schema.
/// Uses hash-based change detection to skip unchanged migrations and perspectives.
/// </summary>
public sealed class PostgresSchemaInitializer {
  private readonly string _connectionString;
  private readonly string? _perspectiveSchemaSql;
  private readonly KeyValuePair<string, string>[]? _perspectiveEntries;
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
  /// Creates an initializer with per-perspective hash tracking.
  /// Each perspective entry is individually tracked via content hash so unchanged perspectives are skipped.
  /// </summary>
  /// <param name="connectionString">PostgreSQL connection string.</param>
  /// <param name="perspectiveEntries">Per-perspective SQL entries from PerspectiveSchemas.Entries.</param>
  /// <param name="migrationProvider">Migration provider for infrastructure SQL scripts.</param>
  public PostgresSchemaInitializer(
      string connectionString,
      KeyValuePair<string, string>[] perspectiveEntries,
      IMigrationProvider? migrationProvider = null)
    : this(connectionString, perspectiveSchemaSql: null, migrationProvider ?? new PostgresMigrationProvider()) {
    _perspectiveEntries = perspectiveEntries ?? throw new ArgumentNullException(nameof(perspectiveEntries));
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

    // Execute perspective schema — per-perspective hash tracking if entries provided, else legacy single-string
    if (_perspectiveEntries is { Length: > 0 }) {
      await _executePerspectiveMigrationsAsync(connection, cancellationToken);
    } else if (!string.IsNullOrWhiteSpace(_perspectiveSchemaSql)) {
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

  /// <summary>
  /// Executes perspective DDL with per-perspective hash tracking.
  /// Each perspective is individually hash-checked and wrapped in its own transaction.
  /// Hash matches → skip (status 3). Hash differs or missing → execute DDL → record hash (status 1 or 2).
  /// </summary>
  private async Task _executePerspectiveMigrationsAsync(NpgsqlConnection connection, CancellationToken cancellationToken) {
    // Reuse the version ID from infrastructure migrations
    var versionId = await _upsertVersionAsync(connection, cancellationToken);

    foreach (var entry in _perspectiveEntries!) {
      var perspectiveName = $"perspective:{entry.Key}";
      var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Value)));

      var existingHash = await _getExistingHashAsync(connection, perspectiveName, cancellationToken);

      if (existingHash == hash) {
        // Hash matches — skip execution, record as Skipped
        await _updateMigrationStatusAsync(connection, perspectiveName, versionId, 3, "Skipped (hash unchanged)", cancellationToken);
        continue;
      }

      var isUpdate = existingHash != null;

      // Detect migration strategy when updating existing perspective
      var tableName = _extractTableName(entry.Value);
      var strategy = MigrationStrategy.DirectDdl;

      if (isUpdate && tableName != null) {
        strategy = await _detectStrategyAsync(connection, tableName, entry.Value, cancellationToken);
      }

      // Wrap DDL + hash recording in a transaction for atomicity
      await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
      try {
        if (strategy == MigrationStrategy.ColumnCopy && tableName != null) {
          // Blue-green column copy: create new table, copy shared columns, swap
          await _migrateTableColumnCopyAsync(connection, transaction, entry.Value, tableName, cancellationToken);
        } else if (strategy == MigrationStrategy.EventReplay) {
          // Event replay required — record status 4 (MigratingInBackground) for background worker
          // Execute DDL with IF NOT EXISTS to create new schema, background worker handles data migration
          await using var cmd = connection.CreateCommand();
          cmd.Transaction = transaction;
          cmd.CommandText = entry.Value;
          cmd.CommandTimeout = 30;
          await cmd.ExecuteNonQueryAsync(cancellationToken);

          await _upsertMigrationAsync(connection, perspectiveName, hash, versionId, 4,
            $"MigratingInBackground from hash {existingHash![..8]}... (destructive change detected, event replay required)",
            cancellationToken, transaction);
          await transaction.CommitAsync(cancellationToken);
          continue;
        } else {
          // Direct DDL execution (new table or identical structure)
          await using var cmd = connection.CreateCommand();
          cmd.Transaction = transaction;
          cmd.CommandText = entry.Value;
          cmd.CommandTimeout = 30;
          await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var status = isUpdate ? 2 : 1; // Updated vs Applied
        var desc = isUpdate
          ? $"Updated from hash {existingHash![..8]}... (strategy: {strategy})"
          : "First apply";

        await _upsertMigrationAsync(connection, perspectiveName, hash, versionId, status, desc, cancellationToken, transaction);

        await transaction.CommitAsync(cancellationToken);
      } catch (Exception ex) {
        await transaction.RollbackAsync(cancellationToken);
        // Record failure (outside transaction since it rolled back)
        await _upsertMigrationAsync(connection, perspectiveName, hash, versionId, -1, $"Failed: {ex.Message}", cancellationToken);
        throw;
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

  private static async Task _upsertMigrationAsync(NpgsqlConnection connection, string fileName, string hash, int versionId, int status, string desc, CancellationToken cancellationToken, NpgsqlTransaction? transaction = null) {
    await using var cmd = connection.CreateCommand();
    if (transaction != null) {
      cmd.Transaction = transaction;
    }
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

  // --- Blue-Green Migration Helpers ---

  /// <summary>
  /// Migration strategy for a table when its DDL hash changes.
  /// </summary>
  private enum MigrationStrategy { DirectDdl, ColumnCopy, EventReplay }

  /// <summary>
  /// Detects the appropriate migration strategy by comparing existing table columns
  /// against the new DDL definition.
  /// </summary>
  private static async Task<MigrationStrategy> _detectStrategyAsync(
      NpgsqlConnection connection, string tableName, string newDdl, CancellationToken ct) {

    var oldColumns = await _getTableColumnsAsync(connection, tableName, ct);
    var newColumns = _parseColumnsFromDdl(newDdl);

    if (oldColumns.Count == 0) {
      return MigrationStrategy.DirectDdl; // new table
    }

    // Check for destructive changes
    foreach (var (name, type) in oldColumns) {
      if (newColumns.TryGetValue(name, out var newType)) {
        if (!string.Equals(newType, type, StringComparison.OrdinalIgnoreCase)) {
          return MigrationStrategy.EventReplay; // type changed
        }
      } else {
        return MigrationStrategy.EventReplay; // column removed
      }
    }

    // Only additive changes (new columns/indexes added)
    return newColumns.Count > oldColumns.Count
        ? MigrationStrategy.ColumnCopy
        : MigrationStrategy.DirectDdl; // identical structure, just re-execute
  }

  /// <summary>
  /// Column-copy blue-green swap: create new table, copy shared columns, rename swap.
  /// Suitable for infrastructure tables and perspective tables with only additive changes.
  /// </summary>
  private static async Task _migrateTableColumnCopyAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction,
      string ddlSql, string tableName, CancellationToken ct) {

    var tempName = $"{tableName}_new";
    var backupName = $"{tableName}_bak_{DateTime.UtcNow:yyyyMMddHHmmss}";
    var (createTableSql, postTableSql) = _splitDdl(ddlSql, tableName);

    // 1. Create new table with temp name
    var tempCreateSql = createTableSql.Replace(tableName, tempName);
    await _executeSqlAsync(connection, transaction, tempCreateSql, ct);

    // 2. Get shared columns, copy data
    var oldCols = await _getTableColumnsAsync(connection, tableName, ct);
    var newCols = await _getTableColumnsAsync(connection, tempName, ct);
    var shared = oldCols.Keys.Intersect(newCols.Keys).ToList();

    if (shared.Count > 0) {
      var colList = string.Join(", ", shared.Select(c => $"\"{c}\""));
      await _executeSqlAsync(connection, transaction,
          $"INSERT INTO {tempName} ({colList}) SELECT {colList} FROM {tableName}", ct);
    }

    // 3. Swap: old → backup, new → active
    await _executeSqlAsync(connection, transaction,
        $"ALTER TABLE {tableName} RENAME TO {backupName}", ct);
    await _executeSqlAsync(connection, transaction,
        $"ALTER TABLE {tempName} RENAME TO {tableName}", ct);

    // 4. Execute post-table DDL (indexes reference final table name)
    if (!string.IsNullOrWhiteSpace(postTableSql)) {
      await _executeSqlAsync(connection, transaction, postTableSql, ct);
    }
  }

  /// <summary>
  /// Queries information_schema.columns for a table's column names and types.
  /// Returns empty dictionary if table does not exist.
  /// </summary>
  private static async Task<Dictionary<string, string>> _getTableColumnsAsync(
      NpgsqlConnection connection, string tableName, CancellationToken ct) {

    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      SELECT column_name, data_type
      FROM information_schema.columns
      WHERE table_schema = 'public' AND table_name = @tableName
      ORDER BY ordinal_position";
    cmd.Parameters.AddWithValue("tableName", tableName);
    cmd.CommandTimeout = 10;

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      result[reader.GetString(0)] = reader.GetString(1);
    }

    return result;
  }

  /// <summary>
  /// Parses column definitions from a CREATE TABLE DDL statement.
  /// Extracts column_name and type from "column_name TYPE" patterns.
  /// </summary>
  private static Dictionary<string, string> _parseColumnsFromDdl(string ddlSql) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Extract the content between the first ( and the matching )
    var match = Regex.Match(ddlSql,
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?\S+\s*\((.*?)\)\s*;",
        RegexOptions.Singleline | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    if (!match.Success) {
      return result;
    }

    var body = match.Groups[1].Value;

    // Split by commas, but not inside parentheses (e.g., DEFAULT gen_random_uuid())
    var columns = _splitColumnDefinitions(body);

    foreach (var colDef in columns) {
      var trimmed = colDef.Trim();

      // Skip constraints (PRIMARY KEY, UNIQUE, CHECK, FOREIGN KEY, CONSTRAINT)
      if (trimmed.StartsWith("PRIMARY", StringComparison.OrdinalIgnoreCase) ||
          trimmed.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
          trimmed.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase) ||
          trimmed.StartsWith("FOREIGN", StringComparison.OrdinalIgnoreCase) ||
          trimmed.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase)) {
        continue;
      }

      // Parse "column_name TYPE [modifiers...]"
      var parts = trimmed.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length >= 2) {
        var colName = parts[0].Trim('"');
        // Extract just the type (first word after column name, handling compound types like "DOUBLE PRECISION")
        var typeStr = _normalizeColumnType(parts[1]);
        result[colName] = typeStr;
      }
    }

    return result;
  }

  /// <summary>
  /// Extracts the table name from a CREATE TABLE DDL statement.
  /// </summary>
  private static string? _extractTableName(string ddlSql) {
    var match = Regex.Match(ddlSql,
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(\S+)\s*\(",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
    return match.Success ? match.Groups[1].Value : null;
  }

  /// <summary>
  /// Checks if a table exists in the public schema.
  /// </summary>
  private static async Task<bool> _tableExistsAsync(
      NpgsqlConnection connection, string tableName, CancellationToken ct) {

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public' AND table_name = @tableName
      )";
    cmd.Parameters.AddWithValue("tableName", tableName);
    cmd.CommandTimeout = 10;

    var result = await cmd.ExecuteScalarAsync(ct);
    return (bool)result!;
  }

  /// <summary>
  /// Splits DDL into CREATE TABLE statement and post-table DDL (indexes, comments).
  /// </summary>
  private static (string CreateTableSql, string PostTableSql) _splitDdl(string ddlSql, string tableName) {
    // Find the end of the CREATE TABLE statement (first ); after CREATE TABLE)
    var createTableMatch = Regex.Match(ddlSql,
        @"(CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?\S+\s*\(.*?\)\s*;)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    if (!createTableMatch.Success) {
      return (ddlSql, string.Empty);
    }

    var createTableSql = createTableMatch.Groups[1].Value;
    var postTableSql = ddlSql.Substring(createTableMatch.Index + createTableMatch.Length).Trim();

    return (createTableSql, postTableSql);
  }

  private static async Task _executeSqlAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct) {
    await using var cmd = connection.CreateCommand();
    cmd.Transaction = transaction;
    cmd.CommandText = sql;
    cmd.CommandTimeout = 30;
    await cmd.ExecuteNonQueryAsync(ct);
  }

  /// <summary>
  /// Splits column definitions by commas, respecting parenthesized expressions.
  /// </summary>
  private static List<string> _splitColumnDefinitions(string body) {
    var result = new List<string>();
    var current = new StringBuilder();
    int depth = 0;

    foreach (var ch in body) {
      if (ch == '(') {
        depth++;
      } else if (ch == ')') {
        depth--;
      } else if (ch == ',' && depth == 0) {
        result.Add(current.ToString());
        current.Clear();
        continue;
      }
      current.Append(ch);
    }

    if (current.Length > 0) {
      result.Add(current.ToString());
    }

    return result;
  }

  /// <summary>
  /// Normalizes a SQL column type string for comparison.
  /// Extracts the core type, handling compound types and modifiers.
  /// </summary>
  private static string _normalizeColumnType(string typeStr) {
    // Trim and take type up to first known modifier keyword
    var normalized = typeStr.Trim();

    // Handle compound types by extracting up to first modifier
    string[] modifiers = ["NOT", "NULL", "DEFAULT", "PRIMARY", "UNIQUE", "CHECK", "REFERENCES", "GENERATED", "CONSTRAINT"];

    foreach (var modifier in modifiers) {
      var idx = normalized.IndexOf($" {modifier}", StringComparison.OrdinalIgnoreCase);
      if (idx > 0) {
        normalized = normalized[..idx].Trim();
        break;
      }
    }

    return normalized.ToUpperInvariant();
  }
}
