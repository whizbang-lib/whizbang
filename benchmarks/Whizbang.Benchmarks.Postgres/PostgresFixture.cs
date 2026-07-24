using System.Globalization;
using Npgsql;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Data.Schema;
using Whizbang.Testing.Containers;

namespace Whizbang.Benchmarks.Postgres;

/// <summary>
/// Per-benchmark Postgres database. Boots the shared whizbang-test-postgres container,
/// creates a fresh test database, applies the C#-generated infrastructure schema and
/// the 33 SQL function migrations from <c>src/Whizbang.Data.Postgres/Migrations/</c>,
/// and returns a connection string ready for benchmarking.
/// </summary>
public sealed class PostgresFixture : IAsyncDisposable {
  private static readonly string[] _migrationFiles = {
    "000_MigrationTracking.sql",
    "001_CreateComputePartitionFunction.sql",
    "002_CreateAcquireReceptorProcessingFunction.sql",
    "003_CreateCompleteReceptorProcessingFunction.sql",
    "004_CreateAcquirePerspectiveCheckpointFunction.sql",
    "005_CreateCompletePerspectiveCheckpointFunction.sql",
    "006_CreateNormalizeEventTypeFunction.sql",
    "007_CreateActiveStreamsTable.sql",
    "008_CreateMessageAssociationRegistry.sql",
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
    "030_ReconcilePerspectiveRegistry.sql",
    "036_DeregisterInstance.sql",
    "037_CompletePerspectiveEvents.sql",
    "038_GetStreamEvents.sql",
    "039_CreateMessageTypeRegistryTable.sql",
    "040_ReconcileMessageTypeRegistry.sql",
    "041_RecomputePartitionNumbers.sql",
  };

  private string? _databaseName;

  /// <summary>Connection string to the per-fixture test database.</summary>
  public string ConnectionString { get; private set; } = null!;

  /// <summary>
  /// Boots the container if needed, creates a fresh test DB, applies schema + migrations.
  /// </summary>
  public async Task InitializeAsync() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

    await SharedPostgresContainer.InitializeAsync();
    _databaseName = "bench_" + Guid.NewGuid().ToString("N");

    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = admin.CreateCommand();
      create.CommandText = $"CREATE DATABASE {_databaseName}";
      await create.ExecuteNonQueryAsync();
    }

    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true,
    };
    ConnectionString = builder.ConnectionString;

    await _applySchemaAndMigrationsAsync();
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    if (_databaseName is null || !SharedPostgresContainer.IsInitialized) {
      return;
    }
    try {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await using (var term = admin.CreateCommand()) {
        term.CommandText = string.Format(CultureInfo.InvariantCulture,
          "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{0}' AND pid <> pg_backend_pid()",
          _databaseName);
        await term.ExecuteNonQueryAsync();
      }
      await using var drop = admin.CreateCommand();
      drop.CommandText = $"DROP DATABASE IF EXISTS {_databaseName}";
      await drop.ExecuteNonQueryAsync();
    } catch {
      // Best-effort: container teardown will reclaim leftover databases.
    }
    _databaseName = null;
  }

  private async Task _applySchemaAndMigrationsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var schemaSql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(
      new SchemaConfiguration(InfrastructurePrefix: "wh_", PerspectivePrefix: "wh_per_"));
    await using (var schemaCmd = conn.CreateCommand()) {
      schemaCmd.CommandText = schemaSql;
      await schemaCmd.ExecuteNonQueryAsync();
    }

    var migrationDir = _findMigrationDir();
    foreach (var file in _migrationFiles) {
      var path = Path.Combine(migrationDir, file);
      var sql = (await File.ReadAllTextAsync(path)).Replace("__SCHEMA__", "public", StringComparison.Ordinal);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = sql;
      try {
        await cmd.ExecuteNonQueryAsync();
      } catch (Exception ex) {
        throw new InvalidOperationException($"Failed migration {file}: {ex.Message}", ex);
      }
    }
  }

  private static string _findMigrationDir() {
    // Walk upward from the binary's directory looking for src/Whizbang.Data.Postgres/Migrations.
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 12; i++) {
      var candidate = Path.Combine(dir, "src", "Whizbang.Data.Postgres", "Migrations");
      if (Directory.Exists(candidate)) {
        return candidate;
      }
      var parent = Directory.GetParent(dir);
      if (parent is null) {
        break;
      }
      dir = parent.FullName;
    }
    throw new DirectoryNotFoundException(
      "Could not locate src/Whizbang.Data.Postgres/Migrations from " + AppContext.BaseDirectory);
  }
}
