using Dapper;
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
  private sealed class TurnkeyVectorTestDbContext : DbContext {
    public TurnkeyVectorTestDbContext(DbContextOptions<TurnkeyVectorTestDbContext> options)
        : base(options) { }
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

  // Dummy DbContext classes for negative tests
  private sealed class UnregisteredTestDbContext : DbContext {
    public UnregisteredTestDbContext(DbContextOptions<UnregisteredTestDbContext> options) : base(options) { }
  }

  private sealed class DoubleInvokeTestDbContext : DbContext {
    public DoubleInvokeTestDbContext(DbContextOptions<DoubleInvokeTestDbContext> options) : base(options) { }
  }
}
