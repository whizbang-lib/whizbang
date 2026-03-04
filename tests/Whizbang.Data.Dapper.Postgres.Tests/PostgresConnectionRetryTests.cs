using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Tests for PostgresConnectionRetry - connection establishment with exponential backoff.
/// Follows TDD RED-GREEN-REFACTOR pattern.
/// </summary>
public class PostgresConnectionRetryTests {
  #region WaitForConnectionAsync Tests

  [Test]
  public async Task WaitForConnectionAsync_WithValidConnection_ReturnsImmediatelyAsync() {
    // Arrange
    await SharedPostgresContainer.InitializeAsync();
    var options = new PostgresOptions {
      InitialRetryAttempts = 3,
      InitialRetryDelay = TimeSpan.FromMilliseconds(100),
      RetryIndefinitely = false
    };
    var retry = new PostgresConnectionRetry(options, NullLogger<PostgresConnectionRetry>.Instance);

    // Act & Assert - should not throw and return quickly
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await retry.WaitForConnectionAsync(SharedPostgresContainer.ConnectionString);
    sw.Stop();

    await Assert.That(sw.ElapsedMilliseconds).IsLessThan(1000)
      .Because("Connection should succeed on first attempt without retries");
  }

  [Test]
  public async Task WaitForConnectionAsync_WithInvalidConnection_RetriesAndThrowsAsync() {
    // Arrange
    var options = new PostgresOptions {
      InitialRetryAttempts = 2,
      InitialRetryDelay = TimeSpan.FromMilliseconds(50),
      RetryIndefinitely = false
    };
    var retry = new PostgresConnectionRetry(options, NullLogger<PostgresConnectionRetry>.Instance);
    var invalidConnectionString = "Host=localhost;Port=9999;Database=nonexistent;Username=invalid;Password=invalid;Timeout=1;";

    // Act & Assert
    await Assert.That(async () => await retry.WaitForConnectionAsync(invalidConnectionString))
      .ThrowsException()
      .Because("Should throw after exhausting retry attempts");
  }

  [Test]
  public async Task WaitForConnectionAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    await SharedPostgresContainer.InitializeAsync();
    var options = new PostgresOptions();
    var retry = new PostgresConnectionRetry(options, NullLogger<PostgresConnectionRetry>.Instance);
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () => await retry.WaitForConnectionAsync(SharedPostgresContainer.ConnectionString, cts.Token))
      .ThrowsExactly<OperationCanceledException>()
      .Because("Cancelled operations should throw OperationCanceledException");
  }

  [Test]
  public async Task WaitForConnectionAsync_WithNullConnectionString_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = new PostgresOptions();
    var retry = new PostgresConnectionRetry(options);

    // Act & Assert
    await Assert.That(async () => await retry.WaitForConnectionAsync(null!))
      .ThrowsExactly<ArgumentNullException>()
      .Because("Null connection string should throw ArgumentNullException");
  }

  [Test]
  public async Task WaitForConnectionAsync_WithEmptyConnectionString_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = new PostgresOptions();
    var retry = new PostgresConnectionRetry(options);

    // Act & Assert
    await Assert.That(async () => await retry.WaitForConnectionAsync(""))
      .ThrowsExactly<ArgumentException>()
      .Because("Empty connection string should throw ArgumentException");
  }

  #endregion

  #region WaitForSchemaReadyAsync Tests

  [Test]
  public async Task WaitForSchemaReadyAsync_WithSchemaReady_ReturnsImmediatelyAsync() {
    // Arrange - Use a fully initialized test database
    await SharedPostgresContainer.InitializeAsync();

    // Create a test database with full schema
    var testDbName = $"test_{Guid.NewGuid():N}";
    await using var adminConnection = new Npgsql.NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {testDbName}");

    try {
      var builder = new Npgsql.NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
        Database = testDbName
      };
      var connectionString = builder.ConnectionString;

      // Initialize schema (tables and functions)
      await _initializeSchemaAsync(connectionString);

      var options = new PostgresOptions {
        InitialRetryAttempts = 3,
        InitialRetryDelay = TimeSpan.FromMilliseconds(100),
        RetryIndefinitely = false
      };
      var retry = new PostgresConnectionRetry(options, NullLogger<PostgresConnectionRetry>.Instance);

      // Act & Assert
      var sw = System.Diagnostics.Stopwatch.StartNew();
      await retry.WaitForSchemaReadyAsync(connectionString);
      sw.Stop();

      await Assert.That(sw.ElapsedMilliseconds).IsLessThan(1000)
        .Because("Schema check should succeed on first attempt when schema is ready");
    } finally {
      // Cleanup
      await adminConnection.ExecuteAsync($@"
        SELECT pg_terminate_backend(pid) FROM pg_stat_activity
        WHERE datname = '{testDbName}' AND pid <> pg_backend_pid()");
      await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {testDbName}");
    }
  }

  [Test]
  public async Task WaitForSchemaReadyAsync_WithMissingTables_RetriesAsync() {
    // Arrange - Database without schema
    await SharedPostgresContainer.InitializeAsync();

    var testDbName = $"test_{Guid.NewGuid():N}";
    await using var adminConnection = new Npgsql.NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {testDbName}");

    try {
      var builder = new Npgsql.NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
        Database = testDbName
      };
      var connectionString = builder.ConnectionString;

      var options = new PostgresOptions {
        InitialRetryAttempts = 2,
        InitialRetryDelay = TimeSpan.FromMilliseconds(50),
        RetryIndefinitely = false
      };
      var retry = new PostgresConnectionRetry(options, NullLogger<PostgresConnectionRetry>.Instance);

      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

      // Act & Assert - Should keep retrying until cancelled (schema never appears)
      // Note: TaskCanceledException inherits from OperationCanceledException
      await Assert.That(async () => await retry.WaitForSchemaReadyAsync(connectionString, cts.Token))
        .Throws<OperationCanceledException>()
        .Because("Should retry until cancelled when schema is missing");
    } finally {
      // Cleanup
      await adminConnection.ExecuteAsync($@"
        SELECT pg_terminate_backend(pid) FROM pg_stat_activity
        WHERE datname = '{testDbName}' AND pid <> pg_backend_pid()");
      await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {testDbName}");
    }
  }

  #endregion

  #region WaitForDatabaseReadyAsync Tests

  [Test]
  public async Task WaitForDatabaseReadyAsync_WithFullyReadyDatabase_SucceedsAsync() {
    // Arrange - Use a fully initialized test database
    await SharedPostgresContainer.InitializeAsync();

    var testDbName = $"test_{Guid.NewGuid():N}";
    await using var adminConnection = new Npgsql.NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {testDbName}");

    try {
      var builder = new Npgsql.NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
        Database = testDbName
      };
      var connectionString = builder.ConnectionString;

      // Initialize schema
      await _initializeSchemaAsync(connectionString);

      var options = new PostgresOptions();
      var retry = new PostgresConnectionRetry(options, NullLogger<PostgresConnectionRetry>.Instance);

      // Act - Should complete successfully (no exception = success)
      await retry.WaitForDatabaseReadyAsync(connectionString);

      // Assert - Verify database is actually ready by querying it
      await using var verifyConnection = new Npgsql.NpgsqlConnection(connectionString);
      await verifyConnection.OpenAsync();
      var tableCount = await verifyConnection.ExecuteScalarAsync<int>(@"
        SELECT COUNT(*) FROM information_schema.tables
        WHERE table_schema = 'public'
          AND table_name IN ('wh_inbox', 'wh_outbox', 'wh_event_store')");

      await Assert.That(tableCount).IsEqualTo(3)
        .Because("WaitForDatabaseReadyAsync should complete when all tables exist");
    } finally {
      // Cleanup
      await adminConnection.ExecuteAsync($@"
        SELECT pg_terminate_backend(pid) FROM pg_stat_activity
        WHERE datname = '{testDbName}' AND pid <> pg_backend_pid()");
      await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {testDbName}");
    }
  }

  #endregion

  #region PostgresOptions Tests

  [Test]
  public async Task PostgresOptions_DefaultValues_AreCorrectAsync() {
    // Arrange & Act
    var options = new PostgresOptions();

    // Assert
    await Assert.That(options.InitialRetryAttempts).IsEqualTo(5)
      .Because("Default initial retry attempts should be 5");
    await Assert.That(options.InitialRetryDelay).IsEqualTo(TimeSpan.FromSeconds(1))
      .Because("Default initial retry delay should be 1 second");
    await Assert.That(options.MaxRetryDelay).IsEqualTo(TimeSpan.FromSeconds(120))
      .Because("Default max retry delay should be 120 seconds");
    await Assert.That(options.BackoffMultiplier).IsEqualTo(2.0)
      .Because("Default backoff multiplier should be 2.0");
    await Assert.That(options.RetryIndefinitely).IsTrue()
      .Because("Default should retry indefinitely (critical infrastructure)");
  }

  #endregion

  #region Helper Methods

  private static async Task _initializeSchemaAsync(string connectionString) {
    await using var connection = new Npgsql.NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    // Create required tables and process_work_batch function (in public schema)
    const string createTablesSql = @"
      CREATE TABLE wh_inbox (id SERIAL PRIMARY KEY);
      CREATE TABLE wh_outbox (id SERIAL PRIMARY KEY);
      CREATE TABLE wh_event_store (id SERIAL PRIMARY KEY);

      -- Create process_work_batch function in public schema (matches production)
      CREATE OR REPLACE FUNCTION public.process_work_batch(
        p_instance_id UUID,
        p_batch_size INT
      ) RETURNS TABLE (
        work_id UUID,
        message_type TEXT,
        payload JSONB
      ) AS $$
      BEGIN
        RETURN;
      END;
      $$ LANGUAGE plpgsql;";

    await connection.ExecuteAsync(createTablesSql);
  }

  #endregion
}
