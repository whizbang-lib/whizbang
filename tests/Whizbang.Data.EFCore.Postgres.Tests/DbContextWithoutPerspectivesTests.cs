using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Custom;
using Whizbang.Data.EFCore.Postgres;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for DbContexts with [WhizbangDbContext] attribute but NO user-defined perspectives.
/// Verifies that core Whizbang tables (Inbox/Outbox/EventStore/ServiceInstances) are created
/// even when there are no custom perspectives.
/// </summary>
public class DbContextWithoutPerspectivesTests : IAsyncDisposable {
  private PostgreSqlContainer? _postgresContainer;
  private string _connectionString = null!;
  private DbContextOptions<MinimalDbContext> _dbContextOptions = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    // Ensure legacy timestamp behavior is disabled (UTC everywhere)
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

    _postgresContainer = new PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
      .WithDatabase("whizbang_test_minimal")
      .WithUsername("postgres")
      .WithPassword("postgres")
      .Build();

    await _postgresContainer.StartAsync();

    _connectionString = $"{_postgresContainer.GetConnectionString()};Timezone=UTC";

    var optionsBuilder = new DbContextOptionsBuilder<MinimalDbContext>();
    optionsBuilder.UseNpgsql(_connectionString);
    _dbContextOptions = optionsBuilder.Options;
  }

  [After(Test)]
  public async Task CleanupAsync() {
    if (_postgresContainer != null) {
      await _postgresContainer.DisposeAsync();
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
    if (_postgresContainer != null) {
      await _postgresContainer.DisposeAsync();
    }
  }
}

/// <summary>
/// Minimal DbContext with [WhizbangDbContext] attribute but NO user-defined perspectives.
/// Used to test that core Whizbang tables are created even without custom perspectives.
/// </summary>
[WhizbangDbContext]
public partial class MinimalDbContext : DbContext {
  public MinimalDbContext(DbContextOptions<MinimalDbContext> options) : base(options) { }

  // No user-defined DbSets or perspectives - only core Whizbang tables should be configured
}
