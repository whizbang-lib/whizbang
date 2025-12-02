using System.Data;
using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;
using TUnit.Core;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Dapper.Postgres;

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
    var connectionString = $"{baseConnectionString};Timezone=UTC";
    _connectionFactory = new PostgresConnectionFactory(connectionString);

    // Setup per-test instances
    Executor = new DapperDbExecutor();
    ConnectionFactory = _connectionFactory;
    ConnectionString = connectionString;

    // Initialize database schema
    await InitializeDatabaseAsync();
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
}
