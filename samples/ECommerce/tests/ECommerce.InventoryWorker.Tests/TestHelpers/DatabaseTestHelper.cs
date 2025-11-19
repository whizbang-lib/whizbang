using System.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Postgres;

namespace ECommerce.InventoryWorker.Tests.TestHelpers;

/// <summary>
/// Helper class for managing PostgreSQL test containers and database setup for InventoryWorker tests.
/// </summary>
public sealed class DatabaseTestHelper : IAsyncDisposable {
  private readonly PostgreSqlContainer _container;
  private bool _isInitialized;

  public DatabaseTestHelper() {
    _container = new PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
      .WithDatabase("whizbang_test")
      .WithUsername("whizbang_user")
      .WithPassword("whizbang_pass")
      .Build();
  }

  public async Task<IDbConnectionFactory> CreateConnectionFactoryAsync(CancellationToken cancellationToken = default) {
    if (!_isInitialized) {
      await _container.StartAsync(cancellationToken);
      await InitializeSchemaAsync(cancellationToken);
      _isInitialized = true;
    }

    var connectionString = _container.GetConnectionString();
    return new PostgresConnectionFactory(connectionString);
  }

  public async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken = default) {
    if (!_isInitialized) {
      await _container.StartAsync(cancellationToken);
      await InitializeSchemaAsync(cancellationToken);
      _isInitialized = true;
    }

    return _container.GetConnectionString();
  }

  private async Task InitializeSchemaAsync(CancellationToken cancellationToken = default) {
    var connectionString = _container.GetConnectionString();
    var initializer = new PostgresSchemaInitializer(connectionString);
    await initializer.InitializeSchemaAsync(cancellationToken);

    // Create InventoryWorker-specific schema and tables
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var schemaSql = @"
-- Create InventoryWorker schema
CREATE SCHEMA IF NOT EXISTS inventoryworker;

-- ProductCatalog table - stores product information
CREATE TABLE IF NOT EXISTS inventoryworker.product_catalog (
  product_id VARCHAR(50) PRIMARY KEY,
  name VARCHAR(200) NOT NULL,
  description TEXT,
  price DECIMAL(18, 2) NOT NULL,
  image_url VARCHAR(500),
  created_at TIMESTAMPTZ NOT NULL,
  updated_at TIMESTAMPTZ,
  deleted_at TIMESTAMPTZ
);

-- InventoryLevels table - tracks inventory quantities
CREATE TABLE IF NOT EXISTS inventoryworker.inventory_levels (
  product_id VARCHAR(50) PRIMARY KEY,
  quantity INTEGER NOT NULL DEFAULT 0,
  reserved INTEGER NOT NULL DEFAULT 0,
  available INTEGER GENERATED ALWAYS AS (quantity - reserved) STORED,
  last_updated TIMESTAMPTZ NOT NULL
);

-- Create indices
CREATE INDEX IF NOT EXISTS idx_product_catalog_deleted_at ON inventoryworker.product_catalog(deleted_at);
CREATE INDEX IF NOT EXISTS idx_inventory_levels_available ON inventoryworker.inventory_levels(available);
";

    await using var command = connection.CreateCommand();
    command.CommandText = schemaSql;
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  public async Task CleanupDatabaseAsync(CancellationToken cancellationToken = default) {
    if (!_isInitialized) {
      return;
    }

    var connectionString = _container.GetConnectionString();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var cleanupSql = @"
TRUNCATE TABLE inventoryworker.product_catalog CASCADE;
TRUNCATE TABLE inventoryworker.inventory_levels CASCADE;
TRUNCATE TABLE whizbang_outbox CASCADE;
TRUNCATE TABLE whizbang_inbox CASCADE;
TRUNCATE TABLE whizbang_event_store CASCADE;
";

    await using var command = connection.CreateCommand();
    command.CommandText = cleanupSql;
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  public async ValueTask DisposeAsync() {
    if (_isInitialized) {
      await _container.DisposeAsync();
    }
  }
}
