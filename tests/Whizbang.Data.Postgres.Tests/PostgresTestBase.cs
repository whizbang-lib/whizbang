using System.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using TUnit.Core;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Base class for PostgreSQL integration tests using Testcontainers.
/// Sets up a PostgreSQL container and database with the Whizbang schema.
/// </summary>
public abstract class PostgresTestBase : IAsyncDisposable {
  private static PostgreSqlContainer? _postgresContainer;
  private static PostgresConnectionFactory? _connectionFactory;
  private static readonly SemaphoreSlim _containerLock = new(1, 1);
  private static int _testCount = 0;

  public DapperDbExecutor Executor { get; private set; } = null!;
  public PostgresConnectionFactory ConnectionFactory { get; private set; } = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await _containerLock.WaitAsync();
    try {
      // Initialize container once for all tests
      if (_postgresContainer == null) {
        _postgresContainer = new PostgreSqlBuilder()
          .WithImage("postgres:17-alpine")
          .WithDatabase("whizbang_test")
          .WithUsername("postgres")
          .WithPassword("postgres")
          .Build();

        await _postgresContainer.StartAsync();

        // Create connection factory
        _connectionFactory = new PostgresConnectionFactory(_postgresContainer.GetConnectionString());

        // Initialize database schema
        await InitializeDatabaseAsync();
      }

      _testCount++;
    } finally {
      _containerLock.Release();
    }

    // Setup per-test instances
    Executor = new DapperDbExecutor();
    ConnectionFactory = _connectionFactory!;

    // Cleanup tables before each test
    await CleanupTablesAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    await _containerLock.WaitAsync();
    try {
      _testCount--;

      // If no more tests are running, stop the container
      if (_testCount == 0 && _postgresContainer != null) {
        await _postgresContainer.StopAsync();
        await _postgresContainer.DisposeAsync();
        _postgresContainer = null;
        _connectionFactory = null;
      }
    } finally {
      _containerLock.Release();
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private static async Task InitializeDatabaseAsync() {
    using var connection = await _connectionFactory!.CreateConnectionAsync();
    // Connection is already opened by PostgresConnectionFactory

    // First, drop all existing tables for clean slate
    var dropSql = @"
      DROP TABLE IF EXISTS whizbang_event_store CASCADE;
      DROP TABLE IF EXISTS whizbang_inbox CASCADE;
      DROP TABLE IF EXISTS whizbang_outbox CASCADE;
      DROP TABLE IF EXISTS whizbang_request_response CASCADE;
      DROP TABLE IF EXISTS whizbang_sequences CASCADE;
    ";

    using (var dropCommand = (NpgsqlCommand)connection.CreateCommand()) {
      dropCommand.CommandText = dropSql;
      await dropCommand.ExecuteNonQueryAsync();
    }

    // Read and execute schema SQL
    var schemaPath = Path.Combine(
      AppContext.BaseDirectory,
      "..", "..", "..", "..", "..",
      "src", "Whizbang.Data.Dapper.Postgres", "whizbang-schema.sql");

    var schemaSql = await File.ReadAllTextAsync(schemaPath);

    using var command = (NpgsqlCommand)connection.CreateCommand();
    command.CommandText = schemaSql;
    await command.ExecuteNonQueryAsync();
  }

  private static async Task CleanupTablesAsync() {
    using var connection = await _connectionFactory!.CreateConnectionAsync();
    // Connection is already opened by PostgresConnectionFactory

    var cleanupSql = @"
      TRUNCATE TABLE whizbang_inbox RESTART IDENTITY CASCADE;
      TRUNCATE TABLE whizbang_outbox RESTART IDENTITY CASCADE;
      TRUNCATE TABLE whizbang_request_response RESTART IDENTITY CASCADE;
      TRUNCATE TABLE whizbang_event_store RESTART IDENTITY CASCADE;
      TRUNCATE TABLE whizbang_sequences RESTART IDENTITY CASCADE;
    ";

    using var command = (NpgsqlCommand)connection.CreateCommand();
    command.CommandText = cleanupSql;
    await command.ExecuteNonQueryAsync();
  }
}
