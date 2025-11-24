using System.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Postgres;

namespace ECommerce.BFF.API.Tests.TestHelpers;

/// <summary>
/// Helper class for managing PostgreSQL test containers and database setup.
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

    // Create BFF-specific schema and tables
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var schemaSql = @"
-- Create BFF schema
CREATE SCHEMA IF NOT EXISTS bff;

-- Orders table
CREATE TABLE IF NOT EXISTS bff.orders (
  order_id VARCHAR(50) PRIMARY KEY,
  customer_id VARCHAR(50) NOT NULL,
  tenant_id VARCHAR(50),
  status VARCHAR(50) NOT NULL,
  total_amount DECIMAL(18, 2) NOT NULL,
  line_items JSONB NOT NULL,
  item_count INT NOT NULL,
  shipping_address JSONB,
  tracking_number VARCHAR(100),
  carrier VARCHAR(100),
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Order status history table
CREATE TABLE IF NOT EXISTS bff.order_status_history (
  id SERIAL PRIMARY KEY,
  order_id VARCHAR(50) NOT NULL,
  status VARCHAR(50) NOT NULL,
  event_type VARCHAR(100) NOT NULL,
  timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  details JSONB,
  FOREIGN KEY (order_id) REFERENCES bff.orders(order_id) ON DELETE CASCADE
);

-- Product catalog table (BFF)
CREATE TABLE IF NOT EXISTS bff.product_catalog (
  product_id UUID PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT,
  price DECIMAL(10, 2) NOT NULL,
  image_url TEXT,
  created_at TIMESTAMP NOT NULL,
  updated_at TIMESTAMP,
  deleted_at TIMESTAMP
);

-- Inventory levels table (BFF)
CREATE TABLE IF NOT EXISTS bff.inventory_levels (
  product_id UUID PRIMARY KEY,
  quantity INTEGER NOT NULL DEFAULT 0,
  reserved INTEGER NOT NULL DEFAULT 0,
  available INTEGER NOT NULL DEFAULT 0,
  last_updated TIMESTAMP NOT NULL
);

-- Create indices
CREATE INDEX IF NOT EXISTS idx_orders_customer_id ON bff.orders(customer_id);
CREATE INDEX IF NOT EXISTS idx_orders_status ON bff.orders(status);
CREATE INDEX IF NOT EXISTS idx_order_status_history_order_id ON bff.order_status_history(order_id);
CREATE INDEX IF NOT EXISTS idx_product_catalog_created_at ON bff.product_catalog (created_at DESC) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_inventory_levels_available ON bff.inventory_levels (available);
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
TRUNCATE TABLE bff.order_status_history CASCADE;
TRUNCATE TABLE bff.orders CASCADE;
TRUNCATE TABLE bff.product_catalog CASCADE;
TRUNCATE TABLE bff.inventory_levels CASCADE;
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
