using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests verifying that the turnkey setup correctly configures pgvector UseVector().
/// These tests simulate the callback registration pattern used by source generators.
/// </summary>
[Category("Integration")]
[Category("TurnkeyVector")]
public class TurnkeyVectorIntegrationTests : IAsyncDisposable {
  private string? _testDatabaseName;
  private string _connectionString = null!;
  private static readonly float[] _testVector = [1.0f, 2.0f, 3.0f];

  /// <summary>
  /// Minimal DbContext for turnkey vector tests.
  /// </summary>
  private sealed class TurnkeyVectorTestDbContext(DbContextOptions<TurnkeyVectorIntegrationTests.TurnkeyVectorTestDbContext> options) : DbContext(options) {
  }

  // ========================================
  // Setup
  // ========================================

  [Before(Test)]
  public async Task SetupAsync() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

    await SharedPostgresContainer.InitializeAsync();

    _testDatabaseName = $"turnkey_vector_test_{Guid.NewGuid():N}";

    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    };
    _connectionString = builder.ConnectionString;

    // Create pgvector extension
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector");
  }

  [After(Test)]
  public async Task TearDownAsync() {
    if (_testDatabaseName != null) {
      try {
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();

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

  public async ValueTask DisposeAsync() {
    await TearDownAsync();
    GC.SuppressFinalize(this);
  }

  // ========================================
  // Tests
  // ========================================

  /// <summary>
  /// Verifies that when we manually register a callback with UseVector() and invoke it,
  /// the resulting NpgsqlDataSource can handle pgvector types.
  /// This simulates what happens with the source-generated module initializer pattern.
  /// </summary>
  [Test]
  public async Task TurnkeySetup_WithManualCallback_ConfiguresUseVectorAsync() {
    // Arrange - Create services collection
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> {
          ["ConnectionStrings:test-db"] = _connectionString
        })
        .Build());

    // Register a callback that mimics what the source generator produces
    DbContextRegistrationRegistry.Register<TurnkeyVectorTestDbContext>((svc, connectionStringNameOverride) => {
      var connectionStringKey = connectionStringNameOverride ?? "test-db";

      // Remove any existing NpgsqlDataSource registration
      svc.RemoveAll<NpgsqlDataSource>();

      // Register NpgsqlDataSource with UseVector()
      svc.AddSingleton<NpgsqlDataSource>(sp => {
        var config = sp.GetRequiredService<IConfiguration>();
        var connectionString = config.GetConnectionString(connectionStringKey)
            ?? throw new InvalidOperationException($"Connection string '{connectionStringKey}' not found.");

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();  // This is the critical line
        return builder.Build();
      });

      // Register DbContext with UseVector()
      svc.AddDbContext<TurnkeyVectorTestDbContext>((sp, options) => {
        var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
        options.UseNpgsql(dataSource, npgsqlOptions => {
          npgsqlOptions.UseVector();  // This is also critical
        });
      });
    });

    // Act - Invoke the registration (like .WithDriver.Postgres does)
    var invoked = DbContextRegistrationRegistry.InvokeRegistration(
        services,
        typeof(TurnkeyVectorTestDbContext),
        "test-db");

    // Assert - Registration should have been found
    await Assert.That(invoked).IsTrue();

    // Build service provider and resolve NpgsqlDataSource
    var sp = services.BuildServiceProvider();
    var dataSource = sp.GetRequiredService<NpgsqlDataSource>();

    // Verify UseVector was applied by successfully creating and reading a vector
    await using var connection = await dataSource.OpenConnectionAsync();

    // Create test table with vector column
    await using var createCmd = connection.CreateCommand();
    createCmd.CommandText = "CREATE TABLE IF NOT EXISTS test_vector (id serial PRIMARY KEY, embedding vector(3))";
    await createCmd.ExecuteNonQueryAsync();

    // Insert a vector value
    await using var insertCmd = connection.CreateCommand();
    insertCmd.CommandText = "INSERT INTO test_vector (embedding) VALUES ($1)";
    insertCmd.Parameters.AddWithValue(new Vector(_testVector));
    await insertCmd.ExecuteNonQueryAsync();

    // Read the vector back - this will fail if UseVector() wasn't configured
    await using var selectCmd = connection.CreateCommand();
    selectCmd.CommandText = "SELECT embedding FROM test_vector LIMIT 1";
    var result = await selectCmd.ExecuteScalarAsync();

    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<Vector>();

    var vector = (Vector)result!;
    await Assert.That(vector.ToArray()).IsEquivalentTo(_testVector);
  }

  /// <summary>
  /// Verifies that when DbContextRegistrationRegistry.InvokeRegistration is called
  /// with a type that has no registration, it returns false.
  /// </summary>
  [Test]
  public async Task InvokeRegistration_WithNoMatchingRegistration_ReturnsFalseAsync() {
    // Arrange - Create services collection (no callback registered for this specific type)
    var services = new ServiceCollection();

    // Act - Try to invoke registration for a type we know isn't registered
    // Use a distinct type that won't be registered elsewhere
    var invoked = DbContextRegistrationRegistry.InvokeRegistration(
        services,
        typeof(UnregisteredTestDbContext),
        "test-db");

    // Assert - Should return false since no registration exists
    await Assert.That(invoked).IsFalse();
  }

  /// <summary>
  /// Verifies that invoking registration twice for the same DbContext on the same
  /// ServiceCollection only executes the callback once.
  /// </summary>
  [Test]
  public async Task InvokeRegistration_CalledTwice_OnlyExecutesOnceAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> {
          ["ConnectionStrings:test-db"] = _connectionString
        })
        .Build());

    var callCount = 0;

    DbContextRegistrationRegistry.Register<DoubleInvokeTestDbContext>((svc, _) => {
      callCount++;
      // Minimal registration - just track the call
      svc.AddSingleton<string>("marker");
    });

    // Act - Invoke twice
    var first = DbContextRegistrationRegistry.InvokeRegistration(services, typeof(DoubleInvokeTestDbContext));
    var second = DbContextRegistrationRegistry.InvokeRegistration(services, typeof(DoubleInvokeTestDbContext));

    // Assert
    await Assert.That(first).IsTrue();
    await Assert.That(second).IsFalse();  // Should skip second invocation
    await Assert.That(callCount).IsEqualTo(1);  // Callback only called once
  }

  // ========================================
  // Extension Check Tests (Azure PostgreSQL Compatibility)
  // ========================================

  /// <summary>
  /// Verifies that when the vector extension already exists, checking pg_extension
  /// correctly detects it and skips the CREATE attempt. This is critical for Azure
  /// PostgreSQL where CREATE permissions are checked before IF NOT EXISTS is evaluated.
  /// </summary>
  [Test]
  public async Task ExtensionCheck_WhenExtensionExists_DetectsCorrectlyAsync() {
    // Arrange - Extension was created in SetupAsync
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    // Act - Check if extension exists using the same query the generator uses
    await using var checkCmd = connection.CreateCommand();
    checkCmd.CommandText = "SELECT 1 FROM pg_extension WHERE extname = 'vector'";
    var exists = await checkCmd.ExecuteScalarAsync() != null;

    // Assert - Extension should be detected
    await Assert.That(exists).IsTrue()
      .Because("Extension was created in setup, check should detect it");
  }

  /// <summary>
  /// Verifies that when the vector extension doesn't exist, checking pg_extension
  /// correctly returns null, allowing CREATE to proceed.
  /// </summary>
  [Test]
  public async Task ExtensionCheck_WhenExtensionDoesNotExist_ReturnsNullAsync() {
    // Arrange - Create a fresh database without the vector extension
    var freshDbName = $"no_vector_test_{Guid.NewGuid():N}";

    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {freshDbName}");

    try {
      var freshBuilder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
        Database = freshDbName,
        Timezone = "UTC"
      };
      var freshConnectionString = freshBuilder.ConnectionString;

      await using var connection = new NpgsqlConnection(freshConnectionString);
      await connection.OpenAsync();

      // Act - Check if extension exists
      await using var checkCmd = connection.CreateCommand();
      checkCmd.CommandText = "SELECT 1 FROM pg_extension WHERE extname = 'vector'";
      var exists = await checkCmd.ExecuteScalarAsync() != null;

      // Assert - Extension should not be detected
      await Assert.That(exists).IsFalse()
        .Because("Extension was not created, check should return null");
    } finally {
      // Cleanup
      await adminConnection.ExecuteAsync($@"
        SELECT pg_terminate_backend(pg_stat_activity.pid)
        FROM pg_stat_activity
        WHERE pg_stat_activity.datname = '{freshDbName}'
        AND pid <> pg_backend_pid()");
      await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {freshDbName}");
    }
  }

  /// <summary>
  /// Verifies the full check-then-create flow: check pg_extension first,
  /// only CREATE if extension doesn't exist. This is the exact pattern
  /// used by the source generator for Azure PostgreSQL compatibility.
  /// </summary>
  [Test]
  public async Task ExtensionCheckThenCreate_FullFlow_WorksCorrectlyAsync() {
    // Arrange - Create a fresh database without the vector extension
    var freshDbName = $"check_create_test_{Guid.NewGuid():N}";

    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {freshDbName}");

    try {
      var freshBuilder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
        Database = freshDbName,
        Timezone = "UTC"
      };
      var freshConnectionString = freshBuilder.ConnectionString;

      await using var connection = new NpgsqlConnection(freshConnectionString);
      await connection.OpenAsync();

      // Act - Execute the exact check-then-create pattern from the generator
      await using var checkCmd = connection.CreateCommand();
      checkCmd.CommandText = "SELECT 1 FROM pg_extension WHERE extname = 'vector'";
      var extensionExists = await checkCmd.ExecuteScalarAsync() != null;

      if (!extensionExists) {
        await using var createCmd = connection.CreateCommand();
        createCmd.CommandText = "CREATE EXTENSION vector";
        await createCmd.ExecuteNonQueryAsync();
      }

      // Verify extension now exists
      await using var verifyCmd = connection.CreateCommand();
      verifyCmd.CommandText = "SELECT 1 FROM pg_extension WHERE extname = 'vector'";
      var nowExists = await verifyCmd.ExecuteScalarAsync() != null;

      // Assert
      await Assert.That(nowExists).IsTrue()
        .Because("Extension should exist after check-then-create flow");
    } finally {
      // Cleanup
      await adminConnection.ExecuteAsync($@"
        SELECT pg_terminate_backend(pg_stat_activity.pid)
        FROM pg_stat_activity
        WHERE pg_stat_activity.datname = '{freshDbName}'
        AND pid <> pg_backend_pid()");
      await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {freshDbName}");
    }
  }

  /// <summary>
  /// Verifies that when extension already exists, the check-then-create flow
  /// skips CREATE entirely. This is critical for environments where the service
  /// account lacks CREATE EXTENSION privileges (infrastructure pre-creates it).
  /// </summary>
  [Test]
  public async Task ExtensionCheckThenCreate_WhenExtensionExists_SkipsCreateAsync() {
    // Arrange - Extension was created in SetupAsync
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    var createExecuted = false;

    // Act - Execute the check-then-create pattern (CREATE should be skipped)
    await using var checkCmd = connection.CreateCommand();
    checkCmd.CommandText = "SELECT 1 FROM pg_extension WHERE extname = 'vector'";
    var extensionExists = await checkCmd.ExecuteScalarAsync() != null;

    if (!extensionExists) {
      createExecuted = true;
      await using var createCmd = connection.CreateCommand();
      createCmd.CommandText = "CREATE EXTENSION vector";
      await createCmd.ExecuteNonQueryAsync();
    }

    // Assert - CREATE should not have been executed
    await Assert.That(createExecuted).IsFalse()
      .Because("Extension already exists, CREATE should be skipped");
    await Assert.That(extensionExists).IsTrue()
      .Because("Check should have detected existing extension");
  }

  /// <summary>
  /// Verifies that the SQL DO block pattern for migrations works correctly.
  /// This is the pattern used in generated migration scripts for Azure compatibility.
  /// </summary>
  [Test]
  public async Task SqlDoBlock_CheckThenCreate_WorksForMigrationsAsync() {
    // Arrange - Create a fresh database without the vector extension
    var freshDbName = $"do_block_test_{Guid.NewGuid():N}";

    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {freshDbName}");

    try {
      var freshBuilder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
        Database = freshDbName,
        Timezone = "UTC"
      };
      var freshConnectionString = freshBuilder.ConnectionString;

      await using var connection = new NpgsqlConnection(freshConnectionString);
      await connection.OpenAsync();

      // Act - Execute the DO block pattern used in generated migrations
      const string doBlockSql = @"
        DO $$
        BEGIN
          IF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector') THEN
            CREATE EXTENSION vector;
          END IF;
        END$$;";

      await connection.ExecuteAsync(doBlockSql);

      // Verify extension now exists
      await using var verifyCmd = connection.CreateCommand();
      verifyCmd.CommandText = "SELECT 1 FROM pg_extension WHERE extname = 'vector'";
      var exists = await verifyCmd.ExecuteScalarAsync() != null;

      // Assert
      await Assert.That(exists).IsTrue()
        .Because("DO block should create extension when it doesn't exist");

      // Run DO block again - should be idempotent
      await connection.ExecuteAsync(doBlockSql);

      // Verify still exists (no error thrown)
      var stillExists = await verifyCmd.ExecuteScalarAsync() != null;
      await Assert.That(stillExists).IsTrue()
        .Because("DO block should be idempotent when extension exists");
    } finally {
      // Cleanup
      await adminConnection.ExecuteAsync($@"
        SELECT pg_terminate_backend(pg_stat_activity.pid)
        FROM pg_stat_activity
        WHERE pg_stat_activity.datname = '{freshDbName}'
        AND pid <> pg_backend_pid()");
      await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {freshDbName}");
    }
  }

  // Dummy DbContext classes for negative tests
  private sealed class UnregisteredTestDbContext(DbContextOptions<TurnkeyVectorIntegrationTests.UnregisteredTestDbContext> options) : DbContext(options) {
  }

  private sealed class DoubleInvokeTestDbContext(DbContextOptions<TurnkeyVectorIntegrationTests.DoubleInvokeTestDbContext> options) : DbContext(options) {
  }
}
