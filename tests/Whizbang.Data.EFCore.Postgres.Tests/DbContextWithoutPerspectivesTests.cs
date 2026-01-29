using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Custom;
using Whizbang.Data.EFCore.Postgres;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for DbContexts with [WhizbangDbContext] attribute but NO user-defined perspectives.
/// Verifies that core Whizbang tables (Inbox/Outbox/EventStore/ServiceInstances) are created
/// even when there are no custom perspectives.
/// Uses SharedPostgresContainer with per-test database isolation.
/// </summary>
public class DbContextWithoutPerspectivesTests : IAsyncDisposable {
  private string? _testDatabaseName;
  private string _connectionString = null!;
  private DbContextOptions<MinimalDbContext> _dbContextOptions = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    // Ensure legacy timestamp behavior is disabled (UTC everywhere)
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

    // Initialize shared container (only starts once)
    await SharedPostgresContainer.InitializeAsync();

    // Create unique database for THIS test
    _testDatabaseName = $"test_{Guid.NewGuid():N}";

    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    // Build connection string for the test database
    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC"
    };
    _connectionString = builder.ConnectionString;

    var optionsBuilder = new DbContextOptionsBuilder<MinimalDbContext>();
    optionsBuilder.UseNpgsql(_connectionString);
    _dbContextOptions = optionsBuilder.Options;
  }

  [After(Test)]
  public async Task CleanupAsync() {
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
    }
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_WithNoPerspectives_CreatesCoreTables() {
    // Arrange
    await using var dbContext = new MinimalDbContext(_dbContextOptions);

    // Act - Initialize database schema
    await MinimalDbContextSchemaExtensions.EnsureWhizbangDatabaseInitializedAsync(dbContext);

    // Assert - Verify core Whizbang tables exist
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    var tableNames = new[] { "wh_outbox", "wh_inbox", "wh_event_store", "wh_service_instances" };

    foreach (var tableName in tableNames) {
      await using var command = new NpgsqlCommand(
        $"SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = '{tableName}');",
        connection);

      var exists = (bool)(await command.ExecuteScalarAsync())!;
      await Assert.That(exists).IsTrue();
    }
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_WithNoPerspectives_CreatesProcessWorkBatchFunction() {
    // Arrange
    await using var dbContext = new MinimalDbContext(_dbContextOptions);

    // Act - Initialize database schema
    await MinimalDbContextSchemaExtensions.EnsureWhizbangDatabaseInitializedAsync(dbContext);

    // Assert - Verify process_work_batch function exists
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    await using var command = new NpgsqlCommand(
      "SELECT EXISTS (SELECT FROM pg_proc WHERE proname = 'process_work_batch');",
      connection);

    var exists = (bool)(await command.ExecuteScalarAsync())!;
    await Assert.That(exists).IsTrue();
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_WithNoPerspectives_IsIdempotent() {
    // Arrange
    await using var dbContext = new MinimalDbContext(_dbContextOptions);

    // Act - Initialize database schema twice
    await MinimalDbContextSchemaExtensions.EnsureWhizbangDatabaseInitializedAsync(dbContext);
    await MinimalDbContextSchemaExtensions.EnsureWhizbangDatabaseInitializedAsync(dbContext);

    // Assert - No exception should be thrown
    // Verify tables still exist
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    await using var command = new NpgsqlCommand(
      "SELECT COUNT(*) FROM information_schema.tables WHERE table_name LIKE 'wh_%';",
      connection);

    var tableCount = (long)(await command.ExecuteScalarAsync())!;
    await Assert.That(tableCount).IsGreaterThanOrEqualTo(4);
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_WithNoPerspectives_CreatesOutboxWithLeaseColumns() {
    // Arrange
    await using var dbContext = new MinimalDbContext(_dbContextOptions);

    // Act - Initialize database schema
    await MinimalDbContextSchemaExtensions.EnsureWhizbangDatabaseInitializedAsync(dbContext);

    // Assert - Verify wh_outbox has instance_id and lease_expiry columns (from migration 001)
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    var columnNames = new[] { "instance_id", "lease_expiry" };

    foreach (var columnName in columnNames) {
      await using var command = new NpgsqlCommand(
        $"SELECT EXISTS (SELECT FROM information_schema.columns WHERE table_name = 'wh_outbox' AND column_name = '{columnName}');",
        connection);

      var exists = (bool)(await command.ExecuteScalarAsync())!;
      await Assert.That(exists).IsTrue();
    }
  }

  [Test]
  public async Task EnsureWhizbangDatabaseInitialized_WithNoPerspectives_CreatesInboxWithLeaseColumns() {
    // Arrange
    await using var dbContext = new MinimalDbContext(_dbContextOptions);

    // Act - Initialize database schema
    await MinimalDbContextSchemaExtensions.EnsureWhizbangDatabaseInitializedAsync(dbContext);

    // Assert - Verify wh_inbox has instance_id, lease_expiry, and status columns (from migration 002)
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    var columnNames = new[] { "instance_id", "lease_expiry", "status" };

    foreach (var columnName in columnNames) {
      await using var command = new NpgsqlCommand(
        $"SELECT EXISTS (SELECT FROM information_schema.columns WHERE table_name = 'wh_inbox' AND column_name = '{columnName}');",
        connection);

      var exists = (bool)(await command.ExecuteScalarAsync())!;
      await Assert.That(exists).IsTrue();
    }
  }

  public async ValueTask DisposeAsync() {
    await CleanupAsync();
    GC.SuppressFinalize(this);
  }
}

/// <summary>
/// Minimal DbContext with [WhizbangDbContext] attribute but NO user-defined perspectives.
/// Used to test that core Whizbang tables are created even without custom perspectives.
/// </summary>
[WhizbangDbContext(Schema = "public")]
public partial class MinimalDbContext : DbContext {
  public MinimalDbContext(DbContextOptions<MinimalDbContext> options) : base(options) { }

  // No user-defined DbSets or perspectives - only core Whizbang tables should be configured
}
