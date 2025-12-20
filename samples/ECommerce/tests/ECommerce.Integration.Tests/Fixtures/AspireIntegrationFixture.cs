using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Generated;
using ECommerce.InventoryWorker.Generated;
using ECommerce.InventoryWorker.Lenses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres;
using Whizbang.Transports.AzureServiceBus;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Aspire-based integration test fixture that manages PostgreSQL and Azure Service Bus via Aspire hosting.
/// This fixture provides reliable container orchestration using Aspire's proven infrastructure.
/// Tests are isolated using unique product IDs and database cleanup between test classes.
/// </summary>
public sealed class AspireIntegrationFixture : IAsyncDisposable {
  private DistributedApplication? _app;
  private bool _isInitialized;
  private IHost? _inventoryHost;
  private IHost? _bffHost;
  private readonly Guid _sharedInstanceId = Guid.CreateVersion7(); // Shared across both services for partition claiming
  private string? _postgresConnection;
  private string? _serviceBusConnection;

  /// <summary>
  /// Gets the IDispatcher instance for sending commands (from InventoryWorker host).
  /// The Dispatcher creates its own scope internally when publishing events.
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
  public string ConnectionString => _postgresConnection
    ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets a logger instance for use in test scenarios.
  /// </summary>
  public ILogger<T> GetLogger<T>() {
    return _inventoryHost?.Services.GetRequiredService<ILogger<T>>()
      ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");
  }

  /// <summary>
  /// Initializes the test fixture: starts Aspire app, gets connection strings, and starts service hosts.
  /// This is called ONCE for all tests in the test run.
  /// </summary>
  [RequiresDynamicCode("EF Core in tests may use dynamic code")]
  [RequiresUnreferencedCode("EF Core in tests may use unreferenced code")]
  public async Task InitializeAsync(CancellationToken cancellationToken = default) {
    if (_isInitialized) {
      return;
    }

    Console.WriteLine("[AspireFixture] Starting Aspire test infrastructure...");

    // Create and start the Aspire distributed application
    var appHost = await DistributedApplicationTestingBuilder
      .CreateAsync<Projects.ECommerce_Integration_Tests_AppHost>(cancellationToken);

    _app = await appHost.BuildAsync(cancellationToken);
    await _app.StartAsync(cancellationToken);

    Console.WriteLine("[AspireFixture] Aspire app started. Getting connection strings...");

    // Get connection strings from Aspire resources
    _postgresConnection = await _app.GetConnectionStringAsync("whizbang-integration-test", cancellationToken)
      ?? throw new InvalidOperationException("Failed to get PostgreSQL connection string");

    _serviceBusConnection = await _app.GetConnectionStringAsync("servicebus", cancellationToken)
      ?? throw new InvalidOperationException("Failed to get Service Bus connection string");

    Console.WriteLine($"[AspireFixture] PostgreSQL Connection: {_postgresConnection}");
    Console.WriteLine($"[AspireFixture] Service Bus Connection: {_serviceBusConnection}");

    // Create service hosts (but don't start them yet)
    _inventoryHost = CreateInventoryHost(_postgresConnection, _serviceBusConnection);
    _bffHost = CreateBffHost(_postgresConnection, _serviceBusConnection);

    Console.WriteLine("[AspireFixture] Service hosts created. Initializing schema...");

    // Initialize PostgreSQL schema using EFCore DbContexts
    await InitializeSchemaAsync(cancellationToken);

    Console.WriteLine("[AspireFixture] Schema initialized. Starting service hosts...");

    // Start service hosts
    await Task.WhenAll(
      _inventoryHost.StartAsync(cancellationToken),
      _bffHost.StartAsync(cancellationToken)
    );

    // Aspire manages emulator initialization, so no need for manual health checks!
    Console.WriteLine("[AspireFixture] Service hosts started and ready!");

    _isInitialized = true;
  }

  /// <summary>
  /// Creates the IHost for InventoryWorker with all required services and background workers.
  /// </summary>
  [RequiresUnreferencedCode("Calls Npgsql.NpgsqlDataSourceBuilder.EnableDynamicJson(Type[], Type[])")]
  [RequiresDynamicCode("Calls Npgsql.NpgsqlDataSourceBuilder.EnableDynamicJson(Type[], Type[])")]
  private IHost CreateInventoryHost(string postgresConnection, string serviceBusConnection) {
    var builder = Host.CreateApplicationBuilder();

    // Register service instance provider (uses shared instance ID for partition claiming compatibility)
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp => new TestServiceInstanceProvider(_sharedInstanceId, "InventoryWorker"));

    // Register Azure Service Bus transport
    var jsonOptions = ECommerce.Contracts.Generated.WhizbangJsonContext.CreateOptions();
    builder.Services.AddAzureServiceBusTransport(serviceBusConnection);

    // Add trace store for observability
    builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

    // Register OrderedStreamProcessor for message ordering
    builder.Services.AddSingleton<OrderedStreamProcessor>();

    // Register JsonSerializerOptions for Npgsql JSONB serialization
    builder.Services.AddSingleton(jsonOptions);

    // Register EF Core DbContext with NpgsqlDataSource (required for EnableDynamicJson)
    // IMPORTANT: Npgsql 9.0+ requires EnableDynamicJson() for JSONB serialization of complex types
    var inventoryDataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(postgresConnection);
    inventoryDataSourceBuilder.EnableDynamicJson();
    var inventoryDataSource = inventoryDataSourceBuilder.Build();
    builder.Services.AddSingleton(inventoryDataSource);

    builder.Services.AddDbContext<ECommerce.InventoryWorker.InventoryDbContext>(options =>
      options.UseNpgsql(inventoryDataSource));

    // Register Whizbang with EFCore infrastructure
    // IMPORTANT: Explicitly call module initializers for test assemblies (may not run automatically)
    ECommerce.InventoryWorker.Generated.GeneratedModelRegistration.Initialize();
    ECommerce.Contracts.Generated.WhizbangIdConverterInitializer.Initialize();

    _ = builder.Services
      .AddWhizbang()
      .WithEFCore<ECommerce.InventoryWorker.InventoryDbContext>()
      .WithDriver.Postgres;

    // Register Whizbang generated services
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddReceptors(builder.Services);
    builder.Services.AddWhizbangAggregateIdExtractor();

    // Configure WorkCoordinatorPublisherWorker with faster polling for integration tests
    builder.Services.Configure<WorkCoordinatorPublisherOptions>(options => {
      options.PollingIntervalMilliseconds = 100;  // Fast polling for tests
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = false;  // Disable diagnostic logging for cleaner test output
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;  // Require 2 empty polls to consider idle
    });

    // Register perspective invoker for scoped event processing (use InventoryWorker's generated invoker)
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddWhizbangPerspectiveInvoker(builder.Services);

    // Register Whizbang dispatcher with outbox and transport support
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddWhizbangDispatcher(builder.Services);

    // Register lenses for querying materialized views
    builder.Services.AddSingleton<IProductLens, ProductLens>();
    builder.Services.AddSingleton<IInventoryLens, InventoryLens>();

    // Register perspective runners (generated by PerspectiveRunnerRegistryGenerator)
    ECommerce.InventoryWorker.Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(builder.Services);

    // Register concrete perspective types for runner resolution
    builder.Services.AddScoped<ECommerce.InventoryWorker.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<ECommerce.InventoryWorker.Perspectives.ProductCatalogPerspective>();

    // Register Service Bus consumer subscriptions for InventoryWorker's own perspectives
    var consumerOptions = new ServiceBusConsumerOptions();
    consumerOptions.Subscriptions.Add(new TopicSubscription("products", "inventory-worker"));
    consumerOptions.Subscriptions.Add(new TopicSubscription("inventory", "inventory-worker"));
    builder.Services.AddSingleton(consumerOptions);

    // Register IMessagePublishStrategy for WorkCoordinatorPublisherWorker
    builder.Services.AddSingleton<IMessagePublishStrategy>(sp =>
      new TransportPublishStrategy(
        sp.GetRequiredService<ITransport>(),
        new DefaultTransportReadinessCheck()
      )
    );

    // Register IWorkChannelWriter for communication between strategy and worker
    builder.Services.AddSingleton<IWorkChannelWriter, WorkChannelWriter>();

    // Register background workers
    builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();
    builder.Services.AddHostedService<ServiceBusConsumerWorker>(sp =>
      new ServiceBusConsumerWorker(
        sp.GetRequiredService<IServiceInstanceProvider>(),
        sp.GetRequiredService<ITransport>(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        jsonOptions,  // Pass JSON options for event deserialization
        sp.GetRequiredService<ILogger<ServiceBusConsumerWorker>>(),
        sp.GetRequiredService<OrderedStreamProcessor>(),
        consumerOptions
      )
    );

    return builder.Build();
  }

  /// <summary>
  /// Creates the IHost for BFF with all required services and background workers.
  /// </summary>
  [RequiresUnreferencedCode("Calls Npgsql.NpgsqlDataSourceBuilder.EnableDynamicJson(Type[], Type[])")]
  [RequiresDynamicCode("Calls Npgsql.NpgsqlDataSourceBuilder.EnableDynamicJson(Type[], Type[])")]
  private IHost CreateBffHost(string postgresConnection, string serviceBusConnection) {
    var builder = Host.CreateApplicationBuilder();

    // Register service instance provider (uses shared instance ID for partition claiming compatibility)
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp => new TestServiceInstanceProvider(_sharedInstanceId, "BFF.API"));

    var jsonOptions = ECommerce.Contracts.Generated.WhizbangJsonContext.CreateOptions();

    // Register Azure Service Bus transport
    builder.Services.AddAzureServiceBusTransport(serviceBusConnection);

    // Add trace store for observability
    builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

    // Register OrderedStreamProcessor for message ordering
    builder.Services.AddSingleton<OrderedStreamProcessor>();

    // Register JsonSerializerOptions for Npgsql JSONB serialization
    builder.Services.AddSingleton(jsonOptions);

    // Register EF Core DbContext with NpgsqlDataSource (required for EnableDynamicJson)
    // IMPORTANT: Npgsql 9.0+ requires EnableDynamicJson() for JSONB serialization of complex types
    var bffDataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(postgresConnection);
    bffDataSourceBuilder.EnableDynamicJson();
    var bffDataSource = bffDataSourceBuilder.Build();
    builder.Services.AddSingleton(bffDataSource);

    builder.Services.AddDbContext<ECommerce.BFF.API.BffDbContext>(options =>
      options.UseNpgsql(bffDataSource));

    // Register Whizbang with EFCore infrastructure
    // IMPORTANT: Explicitly call module initializers for test assemblies (may not run automatically)
    ECommerce.BFF.API.Generated.GeneratedModelRegistration.Initialize();
    ECommerce.Contracts.Generated.WhizbangIdConverterInitializer.Initialize();

    _ = builder.Services
      .AddWhizbang()
      .WithEFCore<ECommerce.BFF.API.BffDbContext>()
      .WithDriver.Postgres;

    // Register SignalR (required by BFF lenses)
    builder.Services.AddSignalR();

    // Register perspective runners (generated by PerspectiveRunnerRegistryGenerator)
    ECommerce.BFF.API.Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(builder.Services);

    // Configure WorkCoordinatorPublisherWorker with faster polling for integration tests
    builder.Services.Configure<WorkCoordinatorPublisherOptions>(options => {
      options.PollingIntervalMilliseconds = 100;  // Fast polling for tests
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = false;  // Disable diagnostic logging for cleaner test output
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;  // Require 2 empty polls to consider idle
    });

    // NOTE: BFF.API doesn't have receptors, so no DispatcherRegistrations is generated
    // BFF only materializes perspectives - it doesn't send commands

    // Register concrete perspective types for runner resolution
    builder.Services.AddScoped<ECommerce.BFF.API.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<ECommerce.BFF.API.Perspectives.ProductCatalogPerspective>();

    // Register lenses (readonly repositories)
    builder.Services.AddScoped<IProductCatalogLens, ProductCatalogLens>();
    builder.Services.AddScoped<IInventoryLevelsLens, InventoryLevelsLens>();

    // Register IMessagePublishStrategy for WorkCoordinatorPublisherWorker
    builder.Services.AddSingleton<IMessagePublishStrategy>(sp =>
      new TransportPublishStrategy(
        sp.GetRequiredService<ITransport>(),
        new DefaultTransportReadinessCheck()
      )
    );

    // Register IWorkChannelWriter for communication between strategy and worker
    builder.Services.AddSingleton<IWorkChannelWriter, WorkChannelWriter>();

    // Register background workers
    builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();

    // Register Service Bus consumer to receive events
    var consumerOptions = new ServiceBusConsumerOptions();
    consumerOptions.Subscriptions.Add(new TopicSubscription("products", "bff-service"));
    consumerOptions.Subscriptions.Add(new TopicSubscription("inventory", "bff-service"));
    builder.Services.AddSingleton(consumerOptions);
    builder.Services.AddHostedService<ServiceBusConsumerWorker>(sp =>
      new ServiceBusConsumerWorker(
        sp.GetRequiredService<IServiceInstanceProvider>(),
        sp.GetRequiredService<ITransport>(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        jsonOptions,  // Pass JSON options for event deserialization
        sp.GetRequiredService<ILogger<ServiceBusConsumerWorker>>(),
        sp.GetRequiredService<OrderedStreamProcessor>(),
        consumerOptions
      )
    );

    return builder.Build();
  }

  /// <summary>
  /// Initializes the PostgreSQL schema: Whizbang core tables + InventoryWorker schema + BFF schema.
  /// </summary>
  private async Task InitializeSchemaAsync(CancellationToken cancellationToken = default) {
    // Initialize InventoryWorker schema using EFCore
    // Creates Inbox/Outbox/EventStore + PostgreSQL functions + perspective tables for InventoryWorker
    using (var scope = _inventoryHost!.Services.CreateScope()) {
      var inventoryDbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();
      await ECommerce.InventoryWorker.Generated.InventoryDbContextSchemaExtensions.EnsureWhizbangDatabaseInitializedAsync(inventoryDbContext, logger: null, cancellationToken);
    }

    // Initialize BFF schema using EFCore
    // Creates Inbox/Outbox/EventStore + PostgreSQL functions + perspective tables for BFF
    using (var scope = _bffHost!.Services.CreateScope()) {
      var bffDbContext = scope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();
      await ECommerce.BFF.API.Generated.BffDbContextSchemaExtensions.EnsureWhizbangDatabaseInitializedAsync(bffDbContext, logger: null, cancellationToken);
    }
  }

  /// <summary>
  /// Waits for both InventoryWorker and BFF work processing to become idle.
  /// Uses event callbacks from WorkCoordinatorPublisherWorker to efficiently detect when all event processing is complete.
  /// Falls back to timeout if idle state is not reached within the specified time.
  /// </summary>
  public async Task WaitForEventProcessingAsync(int timeoutMilliseconds = 30000) {
    // Get worker instances
    var inventoryWorker = _inventoryHost!.Services.GetServices<IHostedService>()
      .OfType<WorkCoordinatorPublisherWorker>()
      .FirstOrDefault();

    var bffWorker = _bffHost!.Services.GetServices<IHostedService>()
      .OfType<WorkCoordinatorPublisherWorker>()
      .FirstOrDefault();

    // Create TaskCompletionSources for this wait
    var inventoryIdleTcs = new TaskCompletionSource<bool>();
    var bffIdleTcs = new TaskCompletionSource<bool>();

    // Wire up one-time idle callbacks
    WorkProcessingIdleHandler? inventoryHandler = null;
    WorkProcessingIdleHandler? bffHandler = null;

    inventoryHandler = () => {
      inventoryIdleTcs.TrySetResult(true);
      if (inventoryWorker != null && inventoryHandler != null) {
        inventoryWorker.OnWorkProcessingIdle -= inventoryHandler;
      }
    };

    bffHandler = () => {
      bffIdleTcs.TrySetResult(true);
      if (bffWorker != null && bffHandler != null) {
        bffWorker.OnWorkProcessingIdle -= bffHandler;
      }
    };

    if (inventoryWorker != null) {
      inventoryWorker.OnWorkProcessingIdle += inventoryHandler;
      // If already idle, set result immediately
      if (inventoryWorker.IsIdle) {
        inventoryIdleTcs.TrySetResult(true);
      }
    } else {
      inventoryIdleTcs.TrySetResult(true);  // No worker, consider idle
    }

    if (bffWorker != null) {
      bffWorker.OnWorkProcessingIdle += bffHandler;
      // If already idle, set result immediately
      if (bffWorker.IsIdle) {
        bffIdleTcs.TrySetResult(true);
      }
    } else {
      bffIdleTcs.TrySetResult(true);  // No worker, consider idle
    }

    // Wait for both to become idle (or timeout)
    using var cts = new CancellationTokenSource(timeoutMilliseconds);

    try {
      await Task.WhenAll(
        inventoryIdleTcs.Task,
        bffIdleTcs.Task
      ).WaitAsync(cts.Token);

      Console.WriteLine("[AspireFixture] Event processing idle - all workers have no pending work");
    } catch (OperationCanceledException) {
      Console.WriteLine($"[AspireFixture] WARNING: Event processing did not reach idle state within {timeoutMilliseconds}ms timeout");
      Console.WriteLine($"[AspireFixture] InventoryWorker idle: {inventoryWorker?.IsIdle ?? true}, BFF idle: {bffWorker?.IsIdle ?? true}");
    } finally {
      // Clean up handlers
      if (inventoryWorker != null && inventoryHandler != null) {
        inventoryWorker.OnWorkProcessingIdle -= inventoryHandler;
      }
      if (bffWorker != null && bffHandler != null) {
        bffWorker.OnWorkProcessingIdle -= bffHandler;
      }
    }
  }

  /// <summary>
  /// Cleans up all test data from the database (truncates all tables).
  /// Call this between test classes to ensure isolation.
  /// </summary>
  public async Task CleanupDatabaseAsync(CancellationToken cancellationToken = default) {
    if (!_isInitialized) {
      return;
    }

    // Truncate all Whizbang tables in the shared database
    // Both InventoryWorker and BFF share the same database, so we only need to truncate once
    using (var scope = _inventoryHost!.Services.CreateScope()) {
      var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();

      // Truncate Whizbang core tables, perspective tables, and checkpoints
      // CASCADE ensures all dependent data is cleared
      // Use DO block to gracefully handle case where tables don't exist
      await dbContext.Database.ExecuteSqlRawAsync(@"
        DO $$
        BEGIN
          -- Truncate core infrastructure tables
          TRUNCATE TABLE wh_event_store, wh_outbox, wh_inbox, wh_perspective_checkpoints, wh_receptor_processing CASCADE;

          -- Truncate all perspective tables (pattern: wh_per_*)
          -- This clears materialized views from both InventoryWorker and BFF
          TRUNCATE TABLE wh_per_inventory_level_dto CASCADE;
          TRUNCATE TABLE wh_per_order_read_model CASCADE;
          TRUNCATE TABLE wh_per_product_dto CASCADE;
        EXCEPTION
          WHEN undefined_table THEN
            -- Tables don't exist, nothing to clean up
            NULL;
        END $$;
      ", cancellationToken);
    }
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

      // Stop Aspire app (which will stop containers)
      if (_app != null) {
        await _app.StopAsync();
        await _app.DisposeAsync();
      }
    }
  }
}
