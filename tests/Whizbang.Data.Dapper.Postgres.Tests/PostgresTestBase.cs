using Dapper;
using Npgsql;
using TUnit.Core;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Data.Schema;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Base class for PostgreSQL integration tests using Testcontainers.
/// Uses a shared PostgreSQL container with per-test database isolation.
/// This approach avoids the previous issue where each test created its own container,
/// causing 60+ simultaneous container startups and Docker resource exhaustion.
/// </summary>
/// <remarks>
/// Dapper type handlers for TrackedGuid and DateTimeOffset are registered via
/// <see cref="DapperTypeHandlers"/> module initializer.
/// </remarks>
[NotInParallel("PostgreSQL")]
public abstract class PostgresTestBase : IAsyncDisposable {
  static PostgresTestBase() {
    // Configure Npgsql to use DateTimeOffset for TIMESTAMPTZ columns globally
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

    // Force Whizbang.Data.Dapper.Custom assembly to load, which triggers its module initializer
    // to register TrackedGuidHandler with Dapper. Without this, the assembly might not load
    // until too late, causing TrackedGuid parameters to fail.
    _ = typeof(TrackedGuidHandler).Assembly;
  }

  private string? _testDatabaseName;
  private PostgresConnectionFactory? _connectionFactory;

  public DapperDbExecutor Executor { get; private set; } = null!;
  public PostgresConnectionFactory ConnectionFactory { get; private set; } = null!;
  protected string ConnectionString { get; private set; } = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    var setupSucceeded = false;
    try {
      // Initialize shared container (skips tests if Docker/PostgreSQL unavailable)
      await SharedPostgresContainer.InitializeOrSkipAsync();

      // Create unique database for THIS test
      _testDatabaseName = $"test_{Guid.NewGuid():N}";

      // Connect to main database to create the test database
      await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await adminConnection.OpenAsync();
      await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

      // Build connection string for the test database
      var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
        Database = _testDatabaseName,
        // Add Timezone=UTC to ensure TIMESTAMPTZ columns map to DateTimeOffset
        Timezone = "UTC",
        // Add Include Error Detail=true to get detailed PostgreSQL error messages
        IncludeErrorDetail = true
      };
      var connectionString = builder.ConnectionString;
      _connectionFactory = new PostgresConnectionFactory(connectionString);

      // Setup per-test instances
      Executor = new DapperDbExecutor();
      ConnectionFactory = _connectionFactory;
      ConnectionString = connectionString;

      // Initialize database schema
      await _initializeDatabaseAsync();

      setupSucceeded = true;
    } finally {
      // Ensure cleanup if setup fails
      if (!setupSucceeded) {
        await TeardownAsync();
      }
    }
  }

  [After(Test)]
  public async Task TeardownAsync() {
    // Drop the test-specific database to clean up
    if (_testDatabaseName != null && SharedPostgresContainer.IsInitialized) {
      try {
        // Close all connections to the test database first
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();

        // Terminate connections to the test database
        await adminConnection.ExecuteAsync($@"
          SELECT pg_terminate_backend(pg_stat_activity.pid)
          FROM pg_stat_activity
          WHERE pg_stat_activity.datname = '{_testDatabaseName}'
          AND pid <> pg_backend_pid()");

        // Drop the database
        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
      } catch {
        // Ignore cleanup errors - the database will be cleaned up when the container stops
      }

      _testDatabaseName = null;
      _connectionFactory = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private async Task _initializeDatabaseAsync() {
    using var connection = await _connectionFactory!.CreateConnectionAsync();
    // Connection is already opened by PostgresConnectionFactory

    // Generate and execute base schema SQL from C# schema definitions
    var schemaConfig = new SchemaConfiguration(
      InfrastructurePrefix: "wh_",
      PerspectivePrefix: "wh_per_"
    );
    var schemaSql = PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(schemaConfig);

    using var schemaCommand = (NpgsqlCommand)connection.CreateCommand();
    schemaCommand.CommandText = schemaSql;
    await schemaCommand.ExecuteNonQueryAsync();

    // Read and execute all PostgreSQL functions from shared Whizbang.Data.Postgres migrations
    var migrationPath = Path.Combine(
      AppContext.BaseDirectory,
      "..", "..", "..", "..", "..",
      "src", "Whizbang.Data.Postgres", "Migrations");

    // Discover all migration files dynamically — avoids the bit-rot that hardcoded lists
    // accumulate as old migrations are deleted (002/003/004 were removed earlier; this
    // list previously referenced them and broke any test that tried to use the base
    // class). Sort by filename: PostgreSQL migration ordering is conventionally
    // alphanumeric on the prefix (000_*, 001_*, ...), and any same-prefix collision is
    // also handled deterministically by Ordinal sort.
    var functionFiles = Directory
      .GetFiles(migrationPath, "*.sql")
      .Select(Path.GetFileName)
      .Where(name => name is not null)
      .Cast<string>()
      .OrderBy(name => name, StringComparer.Ordinal)
      .ToArray();

    foreach (var functionFile in functionFiles) {
      var functionFilePath = Path.Combine(migrationPath, functionFile);
      var functionSql = await File.ReadAllTextAsync(functionFilePath);

      // Replace __SCHEMA__ placeholder with "public" (default PostgreSQL schema for tests)
      functionSql = functionSql.Replace("__SCHEMA__", "public");

      using var functionCommand = (NpgsqlCommand)connection.CreateCommand();
      functionCommand.CommandText = functionSql;
      try {
        await functionCommand.ExecuteNonQueryAsync();
      } catch (Exception ex) {
        Console.WriteLine($"MIGRATION ERROR in {functionFile}: {ex.Message}");
        Console.WriteLine($"ERROR DETAIL: {ex}");
        throw;
      }
    }
  }
}
