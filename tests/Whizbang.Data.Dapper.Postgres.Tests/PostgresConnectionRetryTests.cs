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
    const string invalidConnectionString = "Host=localhost;Port=9999;Database=nonexistent;Username=invalid;Password=invalid;Timeout=1;";

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
      .Because("Canceled operations should throw OperationCanceledException");
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
      await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {testDbName} WITH (FORCE)");
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

      // Act & Assert - Should keep retrying until canceled (schema never appears)
      // Note: TaskCanceledException inherits from OperationCanceledException
      await Assert.That(async () => await retry.WaitForSchemaReadyAsync(connectionString, cts.Token))
        .Throws<OperationCanceledException>()
        .Because("Should retry until canceled when schema is missing");
    } finally {
      // Cleanup
      await adminConnection.ExecuteAsync($@"
        SELECT pg_terminate_backend(pid) FROM pg_stat_activity
        WHERE datname = '{testDbName}' AND pid <> pg_backend_pid()");
      await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {testDbName} WITH (FORCE)");
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
      await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {testDbName} WITH (FORCE)");
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

    // Phase H step 3 replaced process_work_batch with claim_work as the canonical
    // "migrations done" function signal — _isSchemaReadyAsync polls for claim_work.
    const string createTablesSql = @"
      CREATE TABLE wh_inbox (id SERIAL PRIMARY KEY);
      CREATE TABLE wh_outbox (id SERIAL PRIMARY KEY);
      CREATE TABLE wh_event_store (id SERIAL PRIMARY KEY);

      -- Create claim_work function in public schema (matches production signal).
      CREATE OR REPLACE FUNCTION public.claim_work(
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

  #region Backoff and retry policy

  // The retry policy is what a service does while its database is still coming up. Both ends
  // matter: give up too early and a deploy fails because Postgres took a second longer than
  // usual; never give up and a genuinely wrong connection string looks like a hang instead of an
  // error, with nothing in the log to say which.

  [Test]
  public async Task CalculateNextDelay_AppliesTheBackoffMultiplierAsync() {
    var retry = new PostgresConnectionRetry(new PostgresOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromMinutes(5),
    });

    var next = retry.CalculateNextDelay(TimeSpan.FromSeconds(1));

    await Assert.That(next).IsEqualTo(TimeSpan.FromSeconds(2));
  }

  [Test]
  public async Task CalculateNextDelay_CapsAtTheMaximumAsync() {
    // Without the cap the delay doubles without limit, and a service that has been waiting an
    // hour would then wait two — long after an operator would have wanted to see it retry.
    var retry = new PostgresConnectionRetry(new PostgresOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromSeconds(30),
    });

    var next = retry.CalculateNextDelay(TimeSpan.FromSeconds(25));

    await Assert.That(next).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task CalculateNextDelay_AtTheCapStaysThereAsync() {
    var retry = new PostgresConnectionRetry(new PostgresOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromSeconds(30),
    });

    var next = retry.CalculateNextDelay(TimeSpan.FromSeconds(30));

    await Assert.That(next).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task CalculateNextDelay_WithAMultiplierOfOne_DoesNotGrowAsync() {
    // A flat retry is a legitimate configuration — a fixed poll rather than a backoff.
    var retry = new PostgresConnectionRetry(new PostgresOptions {
      BackoffMultiplier = 1.0,
      MaxRetryDelay = TimeSpan.FromMinutes(5),
    });

    var next = retry.CalculateNextDelay(TimeSpan.FromSeconds(3));

    await Assert.That(next).IsEqualTo(TimeSpan.FromSeconds(3));
  }

  [Test]
  [Timeout(60000)]
  public async Task WaitForConnection_WhenNotRetryingIndefinitely_GivesUpAfterTheInitialAttemptsAsync(
      CancellationToken testToken) {
    // The bounded mode exists so a wrong connection string surfaces as an error rather than a
    // hang. Giving up has to actually happen, and the exception has to be the connection failure
    // rather than something the retry loop invented.
    var retry = new PostgresConnectionRetry(new PostgresOptions {
      InitialRetryAttempts = 2,
      InitialRetryDelay = TimeSpan.FromMilliseconds(10),
      MaxRetryDelay = TimeSpan.FromMilliseconds(20),
      RetryIndefinitely = false,
    });

    await Assert.That(async () => await retry.WaitForConnectionAsync(
      "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nothing;Timeout=1",
      testToken)).ThrowsException();
  }

  [Test]
  [Timeout(60000)]
  public async Task WaitForConnection_WhenRetryingIndefinitely_KeepsGoingUntilCanceledAsync(
      CancellationToken testToken) {
    // The default. A database that is still starting must not fail the service, so the only way
    // out is cancellation — which is what host shutdown supplies.
    var retry = new PostgresConnectionRetry(new PostgresOptions {
      InitialRetryAttempts = 1,
      InitialRetryDelay = TimeSpan.FromMilliseconds(10),
      MaxRetryDelay = TimeSpan.FromMilliseconds(20),
      RetryIndefinitely = true,
    });

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    cts.CancelAfter(TimeSpan.FromMilliseconds(300));

    await Assert.That(async () => await retry.WaitForConnectionAsync(
      "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nothing;Timeout=1",
      cts.Token)).ThrowsException()
      .Because("indefinite retry ends only on cancellation — anything else would fail a service "
             + "whose database is merely slow to start");
  }

  [Test]
  [Timeout(60000)]
  public async Task WaitForSchemaReady_WhenTheServerIsUnreachable_KeepsRetryingAsync(
      CancellationToken testToken) {
    // Schema readiness and connection readiness are separate waits, and the schema wait has its
    // own transient-exception branch: the server can disappear between the two.
    var retry = new PostgresConnectionRetry(new PostgresOptions {
      InitialRetryAttempts = 1,
      InitialRetryDelay = TimeSpan.FromMilliseconds(10),
      MaxRetryDelay = TimeSpan.FromMilliseconds(20),
      RetryIndefinitely = true,
    });

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    cts.CancelAfter(TimeSpan.FromMilliseconds(300));

    await Assert.That(async () => await retry.WaitForSchemaReadyAsync(
      "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nothing;Timeout=1",
      cts.Token)).ThrowsException();
  }

  [Test]
  public async Task WaitForSchemaReady_WithNullConnectionString_ThrowsAsync() {
    var retry = new PostgresConnectionRetry(new PostgresOptions());

    await Assert.That(async () => await retry.WaitForSchemaReadyAsync(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_RejectsNullOptionsAsync() {
    await Assert.That(() => new PostgresConnectionRetry(null!))
      .ThrowsExactly<ArgumentNullException>()
      .WithParameterName("options");
  }

  #endregion
}
