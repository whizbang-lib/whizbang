using System.Diagnostics.CodeAnalysis;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Generated;
using ECommerce.InventoryWorker.Generated;
using ECommerce.InventoryWorker.Lenses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Testcontainers.ServiceBus;
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
  private readonly Guid _sharedInstanceId = Guid.CreateVersion7(); // Shared across both services for partition claiming

  public SharedIntegrationFixture() {
    _postgresContainer = new PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
      .WithDatabase("whizbang_integration_test")
      .WithUsername("whizbang_user")
      .WithPassword("whizbang_pass")
      .Build();

    // Mount config file to pre-create topics and subscriptions
    var configPath = Path.Combine(AppContext.BaseDirectory, "servicebus-config.json");
    _serviceBusContainer = new ServiceBusBuilder()
      .WithImage("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
      .WithAcceptLicenseAgreement(true)
      .WithBindMount(configPath, "/ServiceBus_Emulator/ConfigFiles/Config.json")
      .Build();
  }

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
  public string ConnectionString => _postgresContainer.GetConnectionString();

  /// <summary>
  /// Gets a logger instance for use in test scenarios.
  /// </summary>
  public ILogger<T> GetLogger<T>() {
    return _inventoryHost?.Services.GetRequiredService<ILogger<T>>()
      ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");
  }

  /// <summary>
  /// Initializes the test fixture: starts containers, initializes schemas, and starts service hosts.
  /// This is called ONCE for all tests in the test run.
  /// </summary>
  [RequiresDynamicCode("EF Core in tests may use dynamic code")]
  [RequiresUnreferencedCode("EF Core in tests may use unreferenced code")]
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

    Console.WriteLine("[SharedFixture] Containers started. Creating service hosts...");

    // Get connection strings
    var postgresConnection = _postgresContainer.GetConnectionString();
    var serviceBusConnection = _serviceBusContainer.GetConnectionString();

    // Create service hosts (but don't start them yet)
    _inventoryHost = CreateInventoryHost(postgresConnection, serviceBusConnection);
    _bffHost = CreateBffHost(postgresConnection, serviceBusConnection);

    Console.WriteLine("[SharedFixture] Service hosts created. Initializing schema...");

    // Initialize PostgreSQL schema using EFCore DbContexts
    await InitializeSchemaAsync(cancellationToken);

    Console.WriteLine("[SharedFixture] Schema initialized. Starting service hosts...");

    // Start service hosts
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
      options.DebugMode = false;
      options.PartitionCount = 10000;
    });

    // Register perspective invoker for scoped event processing (use InventoryWorker's generated invoker)
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddWhizbangPerspectiveInvoker(builder.Services);

    // Register Whizbang dispatcher with outbox and transport support
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddWhizbangDispatcher(builder.Services);

    // Register lenses for querying materialized views
    builder.Services.AddSingleton<IProductLens, ProductLens>();
    builder.Services.AddSingleton<IInventoryLens, InventoryLens>();

    // Register InventoryWorker perspectives manually (avoid ambiguity with BFF perspectives)
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.ProductCreatedEvent>, ECommerce.InventoryWorker.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.InventoryRestockedEvent>, ECommerce.InventoryWorker.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.InventoryReservedEvent>, ECommerce.InventoryWorker.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.InventoryAdjustedEvent>, ECommerce.InventoryWorker.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.OrderCreatedEvent>, ECommerce.InventoryWorker.Perspectives.OrderInventoryPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.ProductCreatedEvent>, ECommerce.InventoryWorker.Perspectives.ProductCatalogPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.ProductUpdatedEvent>, ECommerce.InventoryWorker.Perspectives.ProductCatalogPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.ProductDeletedEvent>, ECommerce.InventoryWorker.Perspectives.ProductCatalogPerspective>();

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

    // Register SignalR (required by BFF perspectives)
    builder.Services.AddSignalR();

    // Register perspectives
    _ = ECommerce.BFF.API.Generated.PerspectiveRegistrationExtensions.AddWhizbangPerspectives(builder.Services);

    // Configure WorkCoordinatorPublisherWorker with faster polling for integration tests
    builder.Services.Configure<WorkCoordinatorPublisherOptions>(options => {
      options.PollingIntervalMilliseconds = 100;  // Fast polling for tests
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = false;
      options.PartitionCount = 10000;
    });

    // Register dispatcher (needed for event consumption)
    ECommerce.BFF.API.Generated.DispatcherRegistrations.AddWhizbangDispatcher(builder.Services);

    // Register BFF perspectives manually (avoid ambiguity with InventoryWorker perspectives)
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.ProductCreatedEvent>, ECommerce.BFF.API.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.InventoryRestockedEvent>, ECommerce.BFF.API.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.InventoryReservedEvent>, ECommerce.BFF.API.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.InventoryReleasedEvent>, ECommerce.BFF.API.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.InventoryAdjustedEvent>, ECommerce.BFF.API.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.InventoryReservedEvent>, ECommerce.BFF.API.Perspectives.InventoryPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.OrderCreatedEvent>, ECommerce.BFF.API.Perspectives.OrderPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.PaymentFailedEvent>, ECommerce.BFF.API.Perspectives.PaymentFailedPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.PaymentProcessedEvent>, ECommerce.BFF.API.Perspectives.PaymentPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.ProductCreatedEvent>, ECommerce.BFF.API.Perspectives.ProductCatalogPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.ProductUpdatedEvent>, ECommerce.BFF.API.Perspectives.ProductCatalogPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.ProductDeletedEvent>, ECommerce.BFF.API.Perspectives.ProductCatalogPerspective>();
    builder.Services.AddScoped<IPerspectiveOf<ECommerce.Contracts.Events.ShipmentCreatedEvent>, ECommerce.BFF.API.Perspectives.ShippingPerspective>();

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
  /// Waits for asynchronous event processing to complete.
  /// Gives the Service Bus consumer and perspectives time to process published events.
  /// Default increased to 10 seconds to ensure reliable event processing in Docker containers.
  /// </summary>
  public async Task WaitForEventProcessingAsync(int millisecondsDelay = 10000) {
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

      // Stop and dispose containers
      await Task.WhenAll(
        _postgresContainer.DisposeAsync().AsTask(),
        _serviceBusContainer.DisposeAsync().AsTask()
      );
    }
  }
}

/// <summary>
/// Test service instance provider with fixed instance ID and service name.
/// </summary>
internal sealed class TestServiceInstanceProvider : IServiceInstanceProvider {
  public TestServiceInstanceProvider(Guid instanceId, string serviceName) {
    InstanceId = instanceId;
    ServiceName = serviceName;
  }

  public Guid InstanceId { get; }
  public string ServiceName { get; }
  public string HostName => "test-host";
  public int ProcessId => 12345;

  public ServiceInstanceInfo ToInfo() => new() {
    InstanceId = InstanceId,
    ServiceName = ServiceName,
    HostName = HostName,
    ProcessId = ProcessId
  };
}
