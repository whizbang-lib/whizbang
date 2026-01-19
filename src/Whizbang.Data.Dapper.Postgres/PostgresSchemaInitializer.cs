using System.Data;
using Npgsql;
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
/// </summary>
public sealed class PostgresSchemaInitializer {
  private readonly string _connectionString;
  private readonly string? _perspectiveSchemaSql;

  public PostgresSchemaInitializer(string connectionString, string? perspectiveSchemaSql = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    _connectionString = connectionString;
    _perspectiveSchemaSql = perspectiveSchemaSql;
  }

  /// <summary>
  /// Initializes the Whizbang schema by generating SQL from C# schema definitions.
  /// Optionally executes perspective schema SQL if provided.
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

    // Execute migration SQL files (PostgreSQL functions)
    await _executeMigrationsAsync(connection, cancellationToken);

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
    // Generate schema SQL from C# schema definitions
    var schemaConfig = new SchemaConfiguration(
      InfrastructurePrefix: "wh_",
      PerspectivePrefix: "wh_per_"
    );
    var schemaSql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(schemaConfig);

    using var connection = new NpgsqlConnection(_connectionString);
    connection.Open();

    // Execute infrastructure schema
    using var command = connection.CreateCommand();
    command.CommandText = schemaSql;
    command.CommandTimeout = 30;
    command.ExecuteNonQuery();

    // Execute migration SQL files (PostgreSQL functions)
    _executeMigrations(connection);

    // Execute perspective schema if provided
    if (!string.IsNullOrWhiteSpace(_perspectiveSchemaSql)) {
      using var perspectiveCommand = connection.CreateCommand();
      perspectiveCommand.CommandText = _perspectiveSchemaSql;
      perspectiveCommand.CommandTimeout = 30;
      perspectiveCommand.ExecuteNonQuery();
    }
  }

  private static async Task _executeMigrationsAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) {
    var migrationPath = Path.Combine(
      AppContext.BaseDirectory,
      "..", "..", "..", "..", "..",
      "src", "Whizbang.Data.Postgres", "Migrations");

    var functionFiles = new[] {
      "001_CreateComputePartitionFunction.sql",
      "002_CreateAcquireReceptorProcessingFunction.sql",
      "003_CreateCompleteReceptorProcessingFunction.sql",
      "004_CreateAcquirePerspectiveCheckpointFunction.sql",
      "005_CreateCompletePerspectiveCheckpointFunction.sql",
      "006_CreateNormalizeEventTypeFunction.sql",
      "008_CreateMessageAssociationRegistry.sql",
      "008_1_CreateActiveStreamsTable.sql",
      "009_CreatePerspectiveEventsTable.sql",
      "010_RegisterInstanceHeartbeat.sql",
      "011_CleanupStaleInstances.sql",
      "012_CalculateInstanceRank.sql",
      "013_ProcessOutboxCompletions.sql",
      "014_ProcessInboxCompletions.sql",
      "015_ProcessPerspectiveEventCompletions.sql",
      "016_UpdatePerspectiveCheckpoints.sql",
      "017_ProcessOutboxFailures.sql",
      "018_ProcessInboxFailures.sql",
      "019_ProcessPerspectiveEventFailures.sql",
      "020_StoreOutboxMessages.sql",
      "021_StoreInboxMessages.sql",
      "022_StorePerspectiveEvents.sql",
      "023_CleanupCompletedStreams.sql",
      "024_ClaimOrphanedOutbox.sql",
      "025_ClaimOrphanedInbox.sql",
      "026_ClaimOrphanedReceptorWork.sql",
      "027_ClaimOrphanedPerspectiveEvents.sql",
      "028_EventStorageErrorTracking.sql",
      "029_ProcessWorkBatch.sql",
      "030_DecompositionComplete.sql"
    };

    foreach (var functionFile in functionFiles) {
      var functionFilePath = Path.Combine(migrationPath, functionFile);
      if (!File.Exists(functionFilePath)) {
        continue; // Skip missing migration files (may not exist in deployment)
      }

      var functionSql = await File.ReadAllTextAsync(functionFilePath, cancellationToken);

      // Replace __SCHEMA__ placeholder with "public" (default PostgreSQL schema)
      functionSql = functionSql.Replace("__SCHEMA__", "public");

      await using var functionCommand = connection.CreateCommand();
      functionCommand.CommandText = functionSql;
      functionCommand.CommandTimeout = 30;
      await functionCommand.ExecuteNonQueryAsync(cancellationToken);
    }
  }

  private static void _executeMigrations(NpgsqlConnection connection) {
    var migrationPath = Path.Combine(
      AppContext.BaseDirectory,
      "..", "..", "..", "..", "..",
      "src", "Whizbang.Data.Postgres", "Migrations");

    var functionFiles = new[] {
      "001_CreateComputePartitionFunction.sql",
      "002_CreateAcquireReceptorProcessingFunction.sql",
      "003_CreateCompleteReceptorProcessingFunction.sql",
      "004_CreateAcquirePerspectiveCheckpointFunction.sql",
      "005_CreateCompletePerspectiveCheckpointFunction.sql",
      "006_CreateNormalizeEventTypeFunction.sql",
      "008_CreateMessageAssociationRegistry.sql",
      "008_1_CreateActiveStreamsTable.sql",
      "009_CreatePerspectiveEventsTable.sql",
      "010_RegisterInstanceHeartbeat.sql",
      "011_CleanupStaleInstances.sql",
      "012_CalculateInstanceRank.sql",
      "013_ProcessOutboxCompletions.sql",
      "014_ProcessInboxCompletions.sql",
      "015_ProcessPerspectiveEventCompletions.sql",
      "016_UpdatePerspectiveCheckpoints.sql",
      "017_ProcessOutboxFailures.sql",
      "018_ProcessInboxFailures.sql",
      "019_ProcessPerspectiveEventFailures.sql",
      "020_StoreOutboxMessages.sql",
      "021_StoreInboxMessages.sql",
      "022_StorePerspectiveEvents.sql",
      "023_CleanupCompletedStreams.sql",
      "024_ClaimOrphanedOutbox.sql",
      "025_ClaimOrphanedInbox.sql",
      "026_ClaimOrphanedReceptorWork.sql",
      "027_ClaimOrphanedPerspectiveEvents.sql",
      "028_EventStorageErrorTracking.sql",
      "029_ProcessWorkBatch.sql",
      "030_DecompositionComplete.sql"
    };

    foreach (var functionFile in functionFiles) {
      var functionFilePath = Path.Combine(migrationPath, functionFile);
      if (!File.Exists(functionFilePath)) {
        continue; // Skip missing migration files (may not exist in deployment)
      }

      var functionSql = File.ReadAllText(functionFilePath);

      // Replace __SCHEMA__ placeholder with "public" (default PostgreSQL schema)
      functionSql = functionSql.Replace("__SCHEMA__", "public");

      using var functionCommand = connection.CreateCommand();
      functionCommand.CommandText = functionSql;
      functionCommand.CommandTimeout = 30;
      functionCommand.ExecuteNonQuery();
    }
  }
}
