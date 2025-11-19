using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.ServiceBus;
using Testcontainers.PostgreSql;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Transports.AzureServiceBus;
using ECommerce.Contracts.Generated;
using ECommerce.InventoryWorker.Lenses;
using ECommerce.BFF.API.Lenses;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Shared integration test fixture that manages PostgreSQL and Azure Service Bus Testcontainers.
/// This fixture is shared across ALL integration tests to avoid container startup overhead.
/// Tests are isolated using unique product IDs and database cleanup between test classes.
/// </summary>
public sealed class SharedIntegrationFixture : IAsyncDisposable {
  private readonly PostgreSqlContainer _postgresContainer;
  private readonly ServiceBusContainer _serviceBusContainer;
  private bool _isInitialized;
  private IHost? _inventoryHost;
  private IHost? _bffHost;

  public SharedIntegrationFixture() {
    _postgresContainer = new PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
      .WithDatabase("whizbang_integration_test")
      .WithUsername("whizbang_user")
      .WithPassword("whizbang_pass")
      .Build();

    _serviceBusContainer = new ServiceBusBuilder()
      .WithImage("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
      .WithAcceptLicenseAgreement(true)
      .Build();
  }

  /// <summary>
  /// Gets the IDispatcher instance for sending commands (from InventoryWorker host).
  /// </summary>
  public IDispatcher Dispatcher => _inventoryHost?.Services.GetRequiredService<IDispatcher>()
    ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the IProductLens instance for querying product catalog (from InventoryWorker host).
  /// </summary>
  public IProductLens InventoryProductLens => _inventoryHost?.Services.GetRequiredService<IProductLens>()
    ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the IInventoryLens instance for querying inventory levels (from InventoryWorker host).
  /// </summary>
  public IInventoryLens InventoryLens => _inventoryHost?.Services.GetRequiredService<IInventoryLens>()
    ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the IProductCatalogLens instance for querying product catalog (from BFF host).
  /// </summary>
  public IProductCatalogLens BffProductLens => _bffHost?.Services.GetRequiredService<IProductCatalogLens>()
    ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the IInventoryLevelsLens instance for querying inventory levels (from BFF host).
  /// </summary>
  public IInventoryLevelsLens BffInventoryLens => _bffHost?.Services.GetRequiredService<IInventoryLevelsLens>()
    ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the PostgreSQL connection string for direct database operations.
  /// </summary>
  public string ConnectionString => _postgresContainer.GetConnectionString();

  /// <summary>
  /// Initializes the test fixture: starts containers, initializes schemas, and starts service hosts.
  /// This is called ONCE for all tests in the test run.
  /// </summary>
  public async Task InitializeAsync(CancellationToken cancellationToken = default) {
    if (_isInitialized) {
      return;
    }

    Console.WriteLine("[SharedFixture] Starting containers...");

    // Start containers in parallel
    await Task.WhenAll(
      _postgresContainer.StartAsync(cancellationToken),
      _serviceBusContainer.StartAsync(cancellationToken)
    );

    Console.WriteLine("[SharedFixture] Containers started. Initializing schema...");

    // Initialize PostgreSQL schema
    await InitializeSchemaAsync(cancellationToken);

    Console.WriteLine("[SharedFixture] Schema initialized. Creating service hosts...");

    // Get connection strings
    var postgresConnection = _postgresContainer.GetConnectionString();
    var serviceBusConnection = _serviceBusContainer.GetConnectionString();

    // Create and start service hosts
    _inventoryHost = CreateInventoryHost(postgresConnection, serviceBusConnection);
    _bffHost = CreateBffHost(postgresConnection, serviceBusConnection);

    await Task.WhenAll(
      _inventoryHost.StartAsync(cancellationToken),
      _bffHost.StartAsync(cancellationToken)
    );

    // Give the hosts a moment to fully start and subscribe to topics
    await Task.Delay(2000, cancellationToken);

    Console.WriteLine("[SharedFixture] Service hosts started and ready!");

    _isInitialized = true;
  }

  /// <summary>
  /// Creates the IHost for InventoryWorker with all required services and background workers.
  /// </summary>
  private IHost CreateInventoryHost(string postgresConnection, string serviceBusConnection) {
    var builder = Host.CreateApplicationBuilder();

    // Register Whizbang Postgres stores
    var jsonOptions = ECommerce.Contracts.Generated.WhizbangJsonContext.CreateOptions();
    builder.Services.AddWhizbangPostgres(postgresConnection, jsonOptions, initializeSchema: false);

    // Register Azure Service Bus transport
    builder.Services.AddAzureServiceBusTransport(serviceBusConnection, ECommerce.Contracts.Generated.WhizbangJsonContext.Default);

    // Add trace store for observability
    builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

    // Register Whizbang dispatcher with source-generated receptors
    builder.Services.AddReceptors();

    // Register Whizbang dispatcher with outbox and transport support
    builder.Services.AddWhizbangDispatcher();

    // Register lenses for querying materialized views
    builder.Services.AddSingleton<IProductLens, ProductLens>();
    builder.Services.AddSingleton<IInventoryLens, InventoryLens>();

    // Register perspectives for event processing
    builder.Services.AddSingleton<ECommerce.InventoryWorker.Perspectives.ProductCatalogPerspective>();
    builder.Services.AddSingleton<ECommerce.InventoryWorker.Perspectives.InventoryLevelsPerspective>();

    // Register Service Bus consumer subscriptions for InventoryWorker's own perspectives
    var consumerOptions = new ServiceBusConsumerOptions();
    consumerOptions.Subscriptions.Add(new TopicSubscription("products", "inventory-worker"));
    consumerOptions.Subscriptions.Add(new TopicSubscription("inventory", "inventory-worker"));
    builder.Services.AddSingleton(consumerOptions);

    // Register background workers
    builder.Services.AddHostedService<OutboxPublisherWorker>();
    builder.Services.AddHostedService<ServiceBusConsumerWorker>();

    return builder.Build();
  }

  /// <summary>
  /// Creates the IHost for BFF with all required services and background workers.
  /// </summary>
  private IHost CreateBffHost(string postgresConnection, string serviceBusConnection) {
    var builder = Host.CreateApplicationBuilder();

    // Register Whizbang Postgres stores
    var jsonOptions = ECommerce.Contracts.Generated.WhizbangJsonContext.CreateOptions();
    builder.Services.AddWhizbangPostgres(postgresConnection, jsonOptions, initializeSchema: false);

    // Register Azure Service Bus transport
    builder.Services.AddAzureServiceBusTransport(serviceBusConnection, ECommerce.Contracts.Generated.WhizbangJsonContext.Default);

    // Add trace store for observability
    builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

    // Register Whizbang dispatcher (needed by ServiceBusConsumerWorker)
    // Note: BFF doesn't send commands in production, but needs dispatcher for event consumption
    builder.Services.AddWhizbangDispatcher();

    // Register perspectives for event processing
    builder.Services.AddScoped<ECommerce.BFF.API.Perspectives.ProductCatalogPerspective>();
    builder.Services.AddScoped<ECommerce.BFF.API.Perspectives.InventoryLevelsPerspective>();

    // Register lenses (readonly repositories)
    builder.Services.AddScoped<IProductCatalogLens, ProductCatalogLens>();
    builder.Services.AddScoped<IInventoryLevelsLens, InventoryLevelsLens>();

    // Register Service Bus consumer to receive events
    var consumerOptions = new ServiceBusConsumerOptions();
    consumerOptions.Subscriptions.Add(new TopicSubscription("products", "bff-service"));
    consumerOptions.Subscriptions.Add(new TopicSubscription("inventory", "bff-service"));
    builder.Services.AddSingleton(consumerOptions);
    builder.Services.AddHostedService<ServiceBusConsumerWorker>();

    return builder.Build();
  }

  /// <summary>
  /// Initializes the PostgreSQL schema: Whizbang core tables + InventoryWorker schema + BFF schema.
  /// </summary>
  private async Task InitializeSchemaAsync(CancellationToken cancellationToken = default) {
    var connectionString = _postgresContainer.GetConnectionString();

    // Initialize Whizbang core schema (event_store, inbox, outbox)
    var initializer = new PostgresSchemaInitializer(connectionString);
    await initializer.InitializeSchemaAsync(cancellationToken);

    // Create service-specific schemas and tables
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

-- Create indices for InventoryWorker
CREATE INDEX IF NOT EXISTS idx_product_catalog_deleted_at ON inventoryworker.product_catalog(deleted_at);
CREATE INDEX IF NOT EXISTS idx_inventory_levels_available ON inventoryworker.inventory_levels(available);

-- Create BFF schema
CREATE SCHEMA IF NOT EXISTS bff;

-- BFF ProductCatalog perspective
CREATE TABLE IF NOT EXISTS bff.product_catalog (
  product_id VARCHAR(50) PRIMARY KEY,
  name VARCHAR(200) NOT NULL,
  description TEXT,
  price DECIMAL(18, 2) NOT NULL,
  image_url VARCHAR(500),
  created_at TIMESTAMPTZ NOT NULL,
  updated_at TIMESTAMPTZ,
  deleted_at TIMESTAMPTZ
);

-- BFF InventoryLevels perspective
CREATE TABLE IF NOT EXISTS bff.inventory_levels (
  product_id VARCHAR(50) PRIMARY KEY,
  quantity INTEGER NOT NULL DEFAULT 0,
  reserved INTEGER NOT NULL DEFAULT 0,
  available INTEGER GENERATED ALWAYS AS (quantity - reserved) STORED,
  last_updated TIMESTAMPTZ NOT NULL
);

-- Create indices for BFF
CREATE INDEX IF NOT EXISTS idx_bff_product_catalog_deleted_at ON bff.product_catalog(deleted_at);
CREATE INDEX IF NOT EXISTS idx_bff_inventory_levels_available ON bff.inventory_levels(available);
";

    await using var command = connection.CreateCommand();
    command.CommandText = schemaSql;
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  /// <summary>
  /// Waits for asynchronous event processing to complete.
  /// Gives the Service Bus consumer and perspectives time to process published events.
  /// </summary>
  public async Task WaitForEventProcessingAsync(int millisecondsDelay = 3000) {
    await Task.Delay(millisecondsDelay);
  }

  /// <summary>
  /// Cleans up all test data from the database (truncates all tables).
  /// Call this between test classes to ensure isolation.
  /// </summary>
  public async Task CleanupDatabaseAsync(CancellationToken cancellationToken = default) {
    if (!_isInitialized) {
      return;
    }

    var connectionString = _postgresContainer.GetConnectionString();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var cleanupSql = @"
TRUNCATE TABLE inventoryworker.product_catalog CASCADE;
TRUNCATE TABLE inventoryworker.inventory_levels CASCADE;
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
      // Stop hosts
      if (_inventoryHost != null) {
        await _inventoryHost.StopAsync();
        _inventoryHost.Dispose();
      }

      if (_bffHost != null) {
        await _bffHost.StopAsync();
        _bffHost.Dispose();
      }

      // Stop and dispose containers
      await Task.WhenAll(
        _postgresContainer.DisposeAsync().AsTask(),
        _serviceBusContainer.DisposeAsync().AsTask()
      );
    }
  }
}
