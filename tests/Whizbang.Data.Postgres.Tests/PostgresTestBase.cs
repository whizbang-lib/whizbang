using System.Data;
using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;
using TUnit.Core;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Data.Schema;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Base class for PostgreSQL integration tests using Testcontainers.
/// Each test gets its own isolated PostgreSQL container for maximum isolation and parallel execution.
/// </summary>
public abstract class PostgresTestBase : IAsyncDisposable {
  static PostgresTestBase() {
    // Configure Npgsql to use DateTimeOffset for TIMESTAMPTZ columns globally
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

    // Register Dapper type handler to convert DateTime to DateTimeOffset
    SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
  }

  private class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset> {
    public override DateTimeOffset Parse(object value) {
      if (value is DateTime dt) {
        return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
      }
      return (DateTimeOffset)value;
    }

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) {
      parameter.Value = value;
    }
  }
  private PostgreSqlContainer? _postgresContainer;
  private PostgresConnectionFactory? _connectionFactory;

  public DapperDbExecutor Executor { get; private set; } = null!;
  public PostgresConnectionFactory ConnectionFactory { get; private set; } = null!;
  protected string ConnectionString { get; private set; } = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    var setupSucceeded = false;
    try {
      // Create fresh container for THIS test
      _postgresContainer = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("whizbang_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

      await _postgresContainer.StartAsync();

      // Create connection factory with DateTimeOffset support
      var baseConnectionString = _postgresContainer.GetConnectionString();
      // Add Timezone=UTC to ensure TIMESTAMPTZ columns map to DateTimeOffset
      // Add Include Error Detail=true to get detailed PostgreSQL error messages
      var connectionString = $"{baseConnectionString};Timezone=UTC;Include Error Detail=true";
      _connectionFactory = new PostgresConnectionFactory(connectionString);

      // Setup per-test instances
      Executor = new DapperDbExecutor();
      ConnectionFactory = _connectionFactory;
      ConnectionString = connectionString;

      // Initialize database schema
      await InitializeDatabaseAsync();

      setupSucceeded = true;
    } finally {
      // Ensure container is cleaned up if setup fails
      if (!setupSucceeded) {
        await TeardownAsync();
      }
    }
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_postgresContainer != null) {
      await _postgresContainer.StopAsync();
      await _postgresContainer.DisposeAsync();
      _postgresContainer = null;
      _connectionFactory = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private async Task InitializeDatabaseAsync() {
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

    var functionFiles = new[] {
      "001_CreateComputePartitionFunction.sql",
      "002_CreateAcquireReceptorProcessingFunction.sql",
      "003_CreateCompleteReceptorProcessingFunction.sql",
      "004_CreateAcquirePerspectiveCheckpointFunction.sql",
      "005_CreateCompletePerspectiveCheckpointFunction.sql",
      "006_CreateProcessWorkBatchFunction.sql"  // Table moved to C# schema
    };

    foreach (var functionFile in functionFiles) {
      var functionFilePath = Path.Combine(migrationPath, functionFile);
      var functionSql = await File.ReadAllTextAsync(functionFilePath);

      using var functionCommand = (NpgsqlCommand)connection.CreateCommand();
      functionCommand.CommandText = functionSql;
      await functionCommand.ExecuteNonQueryAsync();
    }
  }
}
