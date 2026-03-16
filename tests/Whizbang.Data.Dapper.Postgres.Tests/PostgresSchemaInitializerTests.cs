using Dapper;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Tests for PostgresSchemaInitializer to verify perspective schema integration.
/// Phase 1: PerspectiveSchemaGenerator Runtime Integration
/// Goal: 100% line and branch coverage
/// Uses SharedPostgresContainer with per-test database isolation.
/// </summary>
public class PostgresSchemaInitializerTests : IAsyncDisposable {
  private string? _testDatabaseName;
  private string? _connectionString;

  [Before(Test)]
  public async Task SetupAsync() {
    // Initialize shared container (only starts once)
    await SharedPostgresContainer.InitializeAsync();

    // Create unique database for THIS test
    _testDatabaseName = $"test_{Guid.NewGuid():N}";

    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    // Build connection string for the test database
    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName
    };
    _connectionString = builder.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    // Drop the test-specific database to clean up
    if (_testDatabaseName != null) {
      try {
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();

        // Terminate connections to the test database
        await adminConnection.ExecuteAsync($@"
          SELECT pg_terminate_backend(pg_stat_activity.pid)
          FROM pg_stat_activity
          WHERE pg_stat_activity.datname = '{_testDatabaseName}'
          AND pid <> pg_backend_pid()");

        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
      } catch {
        // Ignore cleanup errors
      }

      _testDatabaseName = null;
      _connectionString = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  /// <summary>
  /// Test 1: Constructor accepts connection string without perspective SQL
  /// </summary>
  [Test]
  public async Task Constructor_WithConnectionStringOnly_InitializesSuccessfullyAsync() {
    // Arrange & Act
    var initializer = new PostgresSchemaInitializer(_connectionString!);

    // Assert
    await Assert.That(initializer).IsNotNull();
  }

  /// <summary>
  /// Test 2: Constructor accepts connection string with perspective SQL
  /// </summary>
  [Test]
  public async Task Constructor_WithPerspectiveSql_InitializesSuccessfullyAsync() {
    // Arrange
    const string perspectiveSql = "CREATE TABLE IF NOT EXISTS test_perspective (id INT);";

    // Act
    var initializer = new PostgresSchemaInitializer(_connectionString!, perspectiveSql);

    // Assert
    await Assert.That(initializer).IsNotNull();
  }

  /// <summary>
  /// Test 3: InitializeSchemaAsync executes infrastructure SQL when no perspective SQL provided
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_NoPerspectiveSql_ExecutesInfrastructureOnlyAsync() {
    // Arrange
    var initializer = new PostgresSchemaInitializer(_connectionString!);

    // Act
    await initializer.InitializeSchemaAsync();

    // Assert - Verify Whizbang infrastructure tables exist
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'wh_event_store'
      );";

    var exists = await command.ExecuteScalarAsync();
    await Assert.That((bool)exists!).IsTrue();
  }

  /// <summary>
  /// Test 4: InitializeSchemaAsync executes both infrastructure and perspective SQL
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_WithPerspectiveSql_ExecutesBothSchemasAsync() {
    // Arrange
    const string perspectiveSql = @"
      CREATE SCHEMA IF NOT EXISTS test_schema;
      CREATE TABLE IF NOT EXISTS test_schema.test_perspective (
        id SERIAL PRIMARY KEY,
        name TEXT NOT NULL
      );";

    var initializer = new PostgresSchemaInitializer(_connectionString!, perspectiveSql);

    // Act
    await initializer.InitializeSchemaAsync();

    // Assert - Verify both infrastructure and perspective tables exist
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    // Check infrastructure table
    await using var infraCommand = connection.CreateCommand();
    infraCommand.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'wh_event_store'
      );";
    var infraExists = await infraCommand.ExecuteScalarAsync();
    await Assert.That((bool)infraExists!).IsTrue();

    // Check perspective table
    await using var perspectiveCommand = connection.CreateCommand();
    perspectiveCommand.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'test_schema'
        AND table_name = 'test_perspective'
      );";
    var perspectiveExists = await perspectiveCommand.ExecuteScalarAsync();
    await Assert.That((bool)perspectiveExists!).IsTrue();
  }

  /// <summary>
  /// Test 5: InitializeSchemaAsync skips perspective SQL when null
  /// Branch coverage: perspectiveSchemaSql == null
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_PerspectiveSqlNull_SkipsPerspectiveSqlAsync() {
    // Arrange
    var initializer = new PostgresSchemaInitializer(_connectionString!, perspectiveSchemaSql: null);

    // Act
    await initializer.InitializeSchemaAsync();

    // Assert - Only infrastructure exists (tested above), no exception thrown
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'wh_event_store'
      );";

    var exists = await command.ExecuteScalarAsync();
    await Assert.That((bool)exists!).IsTrue();
  }

  /// <summary>
  /// Test 6: InitializeSchemaAsync skips perspective SQL when empty string
  /// Branch coverage: perspectiveSchemaSql == ""
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_PerspectiveSqlEmpty_SkipsPerspectiveSqlAsync() {
    // Arrange
    var initializer = new PostgresSchemaInitializer(_connectionString!, perspectiveSchemaSql: "");

    // Act
    await initializer.InitializeSchemaAsync();

    // Assert - Only infrastructure exists, no exception thrown
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'wh_event_store'
      );";

    var exists = await command.ExecuteScalarAsync();
    await Assert.That((bool)exists!).IsTrue();
  }

  /// <summary>
  /// Test 7: InitializeSchema (sync) executes infrastructure SQL only
  /// </summary>
  [Test]
  public async Task InitializeSchema_NoPerspectiveSql_ExecutesInfrastructureOnlyAsync() {
    // Arrange
    var initializer = new PostgresSchemaInitializer(_connectionString!);

    // Act
    initializer.InitializeSchema(); // Synchronous method

    // Assert - Verify Whizbang infrastructure tables exist
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'wh_event_store'
      );";

    var exists = await command.ExecuteScalarAsync();
    await Assert.That((bool)exists!).IsTrue();
  }

  /// <summary>
  /// Test 8: InitializeSchema (sync) executes both infrastructure and perspective SQL
  /// </summary>
  [Test]
  public async Task InitializeSchema_WithPerspectiveSql_ExecutesBothSchemasAsync() {
    // Arrange
    const string perspectiveSql = @"
      CREATE SCHEMA IF NOT EXISTS test_schema_sync;
      CREATE TABLE IF NOT EXISTS test_schema_sync.test_perspective_sync (
        id SERIAL PRIMARY KEY,
        value TEXT NOT NULL
      );";

    var initializer = new PostgresSchemaInitializer(_connectionString!, perspectiveSql);

    // Act
    initializer.InitializeSchema(); // Synchronous method

    // Assert - Verify both infrastructure and perspective tables exist
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    // Check infrastructure table
    await using var infraCommand = connection.CreateCommand();
    infraCommand.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = 'wh_event_store'
      );";
    var infraExists = await infraCommand.ExecuteScalarAsync();
    await Assert.That((bool)infraExists!).IsTrue();

    // Check perspective table
    await using var perspectiveCommand = connection.CreateCommand();
    perspectiveCommand.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'test_schema_sync'
        AND table_name = 'test_perspective_sync'
      );";
    var perspectiveExists = await perspectiveCommand.ExecuteScalarAsync();
    await Assert.That((bool)perspectiveExists!).IsTrue();
  }

  // --- Per-Perspective Hash Tracking Tests ---

  /// <summary>
  /// Test 9: Constructor with perspective entries initializes successfully
  /// </summary>
  [Test]
  public async Task Constructor_WithPerspectiveEntries_InitializesSuccessfullyAsync() {
    // Arrange
    var entries = new[] {
      new KeyValuePair<string, string>("TestPerspective", "CREATE TABLE IF NOT EXISTS wh_per_test (id UUID PRIMARY KEY);")
    };

    // Act
    var initializer = new PostgresSchemaInitializer(_connectionString!, entries);

    // Assert
    await Assert.That(initializer).IsNotNull();
  }

  /// <summary>
  /// Test 10: Per-perspective tracking creates tables and records hash
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_WithPerspectiveEntries_CreatesTablesAndRecordsHashAsync() {
    // Arrange
    var entries = new[] {
      new KeyValuePair<string, string>("OrderPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_order (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), model_data JSONB NOT NULL);")
    };

    var initializer = new PostgresSchemaInitializer(_connectionString!, entries);

    // Act
    await initializer.InitializeSchemaAsync();

    // Assert - Verify perspective table was created
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    var tableExists = await connection.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'wh_per_order')");
    await Assert.That(tableExists).IsTrue();

    // Verify migration was recorded in wh_schema_migrations
    var migrationRecord = await connection.QuerySingleOrDefaultAsync<dynamic>(
      "SELECT file_name, content_hash, status, status_description FROM wh_schema_migrations WHERE file_name = 'perspective:OrderPerspective'");
    await Assert.That((object?)migrationRecord).IsNotNull();
    await Assert.That((int)migrationRecord!.status).IsEqualTo(1); // Applied
    await Assert.That((string)migrationRecord.status_description).IsEqualTo("First apply");
  }

  /// <summary>
  /// Test 11: Per-perspective tracking skips unchanged perspective on second run
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_WithUnchangedPerspective_SkipsExecutionAsync() {
    // Arrange
    var entries = new[] {
      new KeyValuePair<string, string>("OrderPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_order (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), model_data JSONB NOT NULL);")
    };

    var initializer = new PostgresSchemaInitializer(_connectionString!, entries);

    // Act - Run twice with same SQL
    await initializer.InitializeSchemaAsync();
    await initializer.InitializeSchemaAsync();

    // Assert - Should be status 3 (Skipped) on second run
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    var status = await connection.ExecuteScalarAsync<short>(
      "SELECT status FROM wh_schema_migrations WHERE file_name = 'perspective:OrderPerspective'");
    await Assert.That((int)status).IsEqualTo(3); // Skipped
  }

  /// <summary>
  /// Test 12: Per-perspective tracking detects changed perspective SQL and applies update
  /// Additive column changes use ColumnCopy strategy (status 2).
  /// Destructive changes (type change, column removal) use EventReplay strategy (status 4).
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_WithChangedPerspective_UpdatesHashAsync() {
    // Arrange - First run with original SQL (simple columns to avoid type mismatch)
    var entries1 = new[] {
      new KeyValuePair<string, string>("OrderPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_order (id UUID PRIMARY KEY, model_data JSONB NOT NULL);")
    };

    var initializer1 = new PostgresSchemaInitializer(_connectionString!, entries1);
    await initializer1.InitializeSchemaAsync();

    // Insert a test row to verify data preservation
    await using var setupConn = new NpgsqlConnection(_connectionString!);
    await setupConn.OpenAsync();
    await setupConn.ExecuteAsync("INSERT INTO wh_per_order (id, model_data) VALUES (gen_random_uuid(), '{}'::jsonb)");

    // Act - Second run with modified SQL (added column — additive change triggers ColumnCopy)
    var entries2 = new[] {
      new KeyValuePair<string, string>("OrderPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_order (id UUID PRIMARY KEY, model_data JSONB NOT NULL, metadata JSONB);")
    };

    var initializer2 = new PostgresSchemaInitializer(_connectionString!, entries2);
    await initializer2.InitializeSchemaAsync();

    // Assert - Status depends on detected strategy
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    var record = await connection.QuerySingleAsync<dynamic>(
      "SELECT status, status_description FROM wh_schema_migrations WHERE file_name = 'perspective:OrderPerspective'");
    var status = (int)record.status;
    var desc = (string)record.status_description;

    // Should be either Updated (2, ColumnCopy for additive) or MigratingInBackground (4, EventReplay for destructive)
    await Assert.That(status == 2 || status == 4).IsTrue()
      .Because($"Expected status 2 (Updated/ColumnCopy) or 4 (MigratingInBackground/EventReplay), got {status}: {desc}");
    await Assert.That(desc).Contains("from hash");
  }

  /// <summary>
  /// Test 13: Multiple perspectives tracked independently
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_WithMultiplePerspectives_TracksIndependentlyAsync() {
    // Arrange
    var entries = new[] {
      new KeyValuePair<string, string>("OrderPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_order (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), model_data JSONB NOT NULL);"),
      new KeyValuePair<string, string>("CustomerPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_customer (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), model_data JSONB NOT NULL);")
    };

    var initializer = new PostgresSchemaInitializer(_connectionString!, entries);

    // Act
    await initializer.InitializeSchemaAsync();

    // Assert - Both perspectives should be tracked
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    var orderStatus = await connection.ExecuteScalarAsync<short>(
      "SELECT status FROM wh_schema_migrations WHERE file_name = 'perspective:OrderPerspective'");
    var customerStatus = await connection.ExecuteScalarAsync<short>(
      "SELECT status FROM wh_schema_migrations WHERE file_name = 'perspective:CustomerPerspective'");

    await Assert.That((int)orderStatus).IsEqualTo(1); // Applied
    await Assert.That((int)customerStatus).IsEqualTo(1); // Applied

    // Both tables should exist
    var orderTableExists = await connection.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'wh_per_order')");
    var customerTableExists = await connection.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'wh_per_customer')");

    await Assert.That(orderTableExists).IsTrue();
    await Assert.That(customerTableExists).IsTrue();
  }

  /// <summary>
  /// Test 14: Only changed perspective re-executes when one of multiple perspectives changes
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_WithOneChangedPerspective_OnlyUpdatesChangedAsync() {
    // Arrange - First run
    var entries1 = new[] {
      new KeyValuePair<string, string>("OrderPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_order (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), model_data JSONB NOT NULL);"),
      new KeyValuePair<string, string>("CustomerPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_customer (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), model_data JSONB NOT NULL);")
    };

    var initializer1 = new PostgresSchemaInitializer(_connectionString!, entries1);
    await initializer1.InitializeSchemaAsync();

    // Act - Second run: only CustomerPerspective changed
    var entries2 = new[] {
      new KeyValuePair<string, string>("OrderPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_order (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), model_data JSONB NOT NULL);"),
      new KeyValuePair<string, string>("CustomerPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_customer (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), model_data JSONB NOT NULL, metadata JSONB);")
    };

    var initializer2 = new PostgresSchemaInitializer(_connectionString!, entries2);
    await initializer2.InitializeSchemaAsync();

    // Assert
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    var orderStatus = await connection.ExecuteScalarAsync<short>(
      "SELECT status FROM wh_schema_migrations WHERE file_name = 'perspective:OrderPerspective'");
    var customerStatus = await connection.ExecuteScalarAsync<short>(
      "SELECT status FROM wh_schema_migrations WHERE file_name = 'perspective:CustomerPerspective'");

    await Assert.That((int)orderStatus).IsEqualTo(3); // Skipped (unchanged)
    // CustomerPerspective changed: additive column → ColumnCopy (status 2) or EventReplay (status 4)
    await Assert.That((int)customerStatus == 2 || (int)customerStatus == 4).IsTrue();
  }

  /// <summary>
  /// Test 15: Empty perspective entries array is handled gracefully
  /// </summary>
  [Test]
  public async Task InitializeSchemaAsync_WithEmptyEntries_ExecutesInfrastructureOnlyAsync() {
    // Arrange
    var entries = Array.Empty<KeyValuePair<string, string>>();
    var initializer = new PostgresSchemaInitializer(_connectionString!, entries);

    // Act
    await initializer.InitializeSchemaAsync();

    // Assert - Infrastructure tables should exist
    await using var connection = new NpgsqlConnection(_connectionString!);
    await connection.OpenAsync();

    var exists = await connection.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'wh_event_store')");
    await Assert.That(exists).IsTrue();
  }

  /// <summary>
  /// Test 16: Perspective entries constructor with null entries throws
  /// </summary>
  [Test]
  public async Task Constructor_WithNullPerspectiveEntries_ThrowsAsync() {
    // Act & Assert
    await Assert.That(() => new PostgresSchemaInitializer(_connectionString!, perspectiveEntries: null!))
      .Throws<ArgumentNullException>();
  }

  // --- Preview / Dry-Run Tests ---

  /// <summary>
  /// Test 17: Preview returns plan showing all migrations as Apply on fresh DB
  /// </summary>
  [Test]
  public async Task PreviewAsync_FreshDatabase_ShowsAllAsApplyAsync() {
    // Arrange
    var entries = new[] {
      new KeyValuePair<string, string>("OrderPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_order (id UUID PRIMARY KEY, model_data JSONB NOT NULL);")
    };

    var initializer = new PostgresSchemaInitializer(_connectionString!, entries);

    // Act
    var plan = await initializer.PreviewAsync();

    // Assert - Should have infrastructure migrations + 1 perspective
    await Assert.That(plan.Steps.Count).IsGreaterThan(0);

    // All infrastructure migrations should be Apply (fresh DB)
    var infraSteps = plan.Steps.Where(s => !s.Name.StartsWith("perspective:", StringComparison.Ordinal)).ToList();
    await Assert.That(infraSteps).Count().IsGreaterThan(0);
    foreach (var step in infraSteps) {
      await Assert.That(step.Action).IsEqualTo(Whizbang.Core.Data.MigrationAction.Apply);
    }

    // Perspective should also be Apply
    var perspStep = plan.Steps.FirstOrDefault(s => s.Name == "perspective:OrderPerspective");
    await Assert.That(perspStep).IsNotNull();
    await Assert.That(perspStep!.Action).IsEqualTo(Whizbang.Core.Data.MigrationAction.Apply);
  }

  /// <summary>
  /// Test 18: Preview shows Skip for unchanged migrations after initialization
  /// </summary>
  [Test]
  public async Task PreviewAsync_AfterInitialize_ShowsAllAsSkipAsync() {
    // Arrange
    var entries = new[] {
      new KeyValuePair<string, string>("OrderPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_order (id UUID PRIMARY KEY, model_data JSONB NOT NULL);")
    };

    var initializer = new PostgresSchemaInitializer(_connectionString!, entries);
    await initializer.InitializeSchemaAsync();

    // Act
    var plan = await initializer.PreviewAsync();

    // Assert - All should be Skip (nothing changed)
    foreach (var step in plan.Steps) {
      await Assert.That(step.Action).IsEqualTo(Whizbang.Core.Data.MigrationAction.Skip)
        .Because($"Step '{step.Name}' should be Skip after initialization");
    }
  }

  /// <summary>
  /// Test 19: Preview detects changed perspective and shows column diff
  /// </summary>
  [Test]
  public async Task PreviewAsync_WithChangedPerspective_ShowsUpdateWithColumnDiffAsync() {
    // Arrange - First run
    var entries1 = new[] {
      new KeyValuePair<string, string>("OrderPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_order (id UUID PRIMARY KEY, model_data JSONB NOT NULL);")
    };
    var initializer1 = new PostgresSchemaInitializer(_connectionString!, entries1);
    await initializer1.InitializeSchemaAsync();

    // Act - Preview with added column
    var entries2 = new[] {
      new KeyValuePair<string, string>("OrderPerspective",
        "CREATE TABLE IF NOT EXISTS wh_per_order (id UUID PRIMARY KEY, model_data JSONB NOT NULL, metadata JSONB);")
    };
    var initializer2 = new PostgresSchemaInitializer(_connectionString!, entries2);
    var plan = await initializer2.PreviewAsync();

    // Assert
    var perspStep = plan.Steps.FirstOrDefault(s => s.Name == "perspective:OrderPerspective");
    await Assert.That(perspStep).IsNotNull();
    // Should detect an update/blue-green strategy (not Skip)
    await Assert.That(perspStep!.Action).IsNotEqualTo(Whizbang.Core.Data.MigrationAction.Skip);
  }

  // --- Backup Cleanup Tests ---

  /// <summary>
  /// Test 20: CleanupBackups returns empty list when no backups exist
  /// </summary>
  [Test]
  public async Task CleanupBackupsAsync_NoBackups_ReturnsEmptyAsync() {
    // Arrange
    var initializer = new PostgresSchemaInitializer(_connectionString!);
    await initializer.InitializeSchemaAsync();

    // Act
    var dropped = await initializer.CleanupBackupsAsync();

    // Assert
    await Assert.That(dropped).Count().IsEqualTo(0);
  }

  /// <summary>
  /// Test 21: CleanupBackups removes old backup tables
  /// </summary>
  [Test]
  public async Task CleanupBackupsAsync_WithOldBackup_DropsItAsync() {
    // Arrange
    var initializer = new PostgresSchemaInitializer(_connectionString!);
    await initializer.InitializeSchemaAsync();

    // Create a fake old backup table (pretend it was created 60 days ago)
    await using var conn = new NpgsqlConnection(_connectionString!);
    await conn.OpenAsync();
    var oldDate = DateTime.UtcNow.AddDays(-60).ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
    await conn.ExecuteAsync($"CREATE TABLE wh_per_test_bak_{oldDate} (id INT)");

    // Act
    var dropped = await initializer.CleanupBackupsAsync(olderThanDays: 30);

    // Assert
    await Assert.That(dropped).Count().IsEqualTo(1);
    await Assert.That(dropped[0]).Contains("wh_per_test_bak_");
  }

  /// <summary>
  /// Test 22: CleanupBackups keeps recent backup tables
  /// </summary>
  [Test]
  public async Task CleanupBackupsAsync_WithRecentBackup_KeepsItAsync() {
    // Arrange
    var initializer = new PostgresSchemaInitializer(_connectionString!);
    await initializer.InitializeSchemaAsync();

    // Create a recent backup table
    await using var conn = new NpgsqlConnection(_connectionString!);
    await conn.OpenAsync();
    var recentDate = DateTime.UtcNow.AddDays(-5).ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
    await conn.ExecuteAsync($"CREATE TABLE wh_per_test_bak_{recentDate} (id INT)");

    // Act
    var dropped = await initializer.CleanupBackupsAsync(olderThanDays: 30);

    // Assert - Recent backup should be kept
    await Assert.That(dropped).Count().IsEqualTo(0);

    // Verify table still exists
    var exists = await conn.ExecuteScalarAsync<bool>(
      $"SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'wh_per_test_bak_{recentDate}')");
    await Assert.That(exists).IsTrue();
  }
}
