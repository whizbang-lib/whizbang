using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Base class for EF Core PostgreSQL integration tests using Testcontainers.
/// Each test gets its own isolated PostgreSQL container for maximum isolation and parallel execution.
/// </summary>
public abstract class EFCoreTestBase : IAsyncDisposable {
  static EFCoreTestBase() {
    // Configure Npgsql to use DateTimeOffset for TIMESTAMPTZ columns globally
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private PostgreSqlContainer? _postgresContainer;

  protected string ConnectionString { get; private set; } = null!;
  protected DbContextOptions<WorkCoordinationDbContext> DbContextOptions { get; private set; } = null!;

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

    // Create connection string with DateTimeOffset support
    var baseConnectionString = _postgresContainer.GetConnectionString();
    // Add Timezone=UTC to ensure TIMESTAMPTZ columns map to DateTimeOffset
    ConnectionString = $"{baseConnectionString};Timezone=UTC";

    // Configure DbContext options
    var optionsBuilder = new DbContextOptionsBuilder<WorkCoordinationDbContext>();
    optionsBuilder.UseNpgsql(ConnectionString);
    DbContextOptions = optionsBuilder.Options;

    // Initialize database schema
    await InitializeDatabaseAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_postgresContainer != null) {
      await _postgresContainer.StopAsync();
      await _postgresContainer.DisposeAsync();
      _postgresContainer = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private async Task InitializeDatabaseAsync() {
    // Let EF Core create the schema from the entity model
    // This ensures column names match entity properties (PascalCase with quotes)
    await using var dbContext = CreateDbContext();
    await dbContext.Database.EnsureCreatedAsync();

    // Load and execute the EF Core-specific process_work_batch function
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    var functionPath = Path.Combine(
      AppContext.BaseDirectory,
      "..", "..", "..", "..", "..",
      "src", "Whizbang.Data.EFCore.Postgres.Generators", "Templates", "Migrations",
      "003_CreateProcessWorkBatchFunction.sql");

    var functionSql = await File.ReadAllTextAsync(functionPath);
    await using var functionCommand = new NpgsqlCommand(functionSql, connection);
    await functionCommand.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Creates a new DbContext instance for the current test.
  /// </summary>
  protected WorkCoordinationDbContext CreateDbContext() {
    return new WorkCoordinationDbContext(DbContextOptions);
  }
}
