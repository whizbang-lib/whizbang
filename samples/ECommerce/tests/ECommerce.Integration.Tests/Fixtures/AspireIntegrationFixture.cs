using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Generated;
using ECommerce.InventoryWorker.Generated;
using ECommerce.InventoryWorker.Lenses;
using Medo;
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
  private readonly Guid _inventoryInstanceId = Uuid7.NewUuid7().ToGuid(); // Unique ID for InventoryWorker partition claiming
  private readonly Guid _bffInstanceId = Uuid7.NewUuid7().ToGuid(); // Unique ID for BFF partition claiming
  private readonly Guid _testPollerInstanceId = Uuid7.NewUuid7().ToGuid(); // Separate ID for test polling to avoid work conflicts
  private string? _postgresConnection;
  private string? _serviceBusConnection;
  private IServiceScope? _inventoryScope;  // Long-lived scope for lens access
  private IServiceScope? _bffScope;  // Long-lived scope for lens access

  /// <summary>
  /// Gets the IDispatcher instance for sending commands (from InventoryWorker host).
  /// The Dispatcher creates its own scope internally when publishing events.
  /// </summary>
  public IDispatcher Dispatcher => _inventoryHost?.Services.GetRequiredService<IDispatcher>()
    ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the IProductLens instance for querying product catalog (from InventoryWorker host).
  /// Resolves from a long-lived scope that persists for the lifetime of the fixture.
  /// </summary>
  public IProductLens InventoryProductLens => _inventoryScope?.ServiceProvider.GetRequiredService<IProductLens>()
    ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the IInventoryLens instance for querying inventory levels (from InventoryWorker host).
  /// Resolves from a long-lived scope that persists for the lifetime of the fixture.
  /// </summary>
  public IInventoryLens InventoryLens => _inventoryScope?.ServiceProvider.GetRequiredService<IInventoryLens>()
    ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the IProductCatalogLens instance for querying product catalog (from BFF host).
  /// Resolves from a long-lived scope that persists for the lifetime of the fixture.
  /// </summary>
  public IProductCatalogLens BffProductLens => _bffScope?.ServiceProvider.GetRequiredService<IProductCatalogLens>()
    ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the IInventoryLevelsLens instance for querying inventory levels (from BFF host).
  /// Resolves from a long-lived scope that persists for the lifetime of the fixture.
  /// </summary>
  public IInventoryLevelsLens BffInventoryLens => _bffScope?.ServiceProvider.GetRequiredService<IInventoryLevelsLens>()
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
    // IMPORTANT: Service Bus emulator takes 60-90 seconds to start on ARM64 (see GitHub issue #8818)
    // We need to give Aspire enough time to start the emulator before timing out
    var appHost = await DistributedApplicationTestingBuilder
      .CreateAsync<Projects.ECommerce_Integration_Tests_AppHost>(cancellationToken);

    _app = await appHost.BuildAsync(cancellationToken);

    // Use a 3-minute timeout for starting containers (default is much shorter)
    // Service Bus emulator needs ~60-90s to initialize SQL Server + create databases + provision topics
    using var startupCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    await _app.StartAsync(startupCts.Token);

    Console.WriteLine("[AspireFixture] Aspire app started. Getting connection strings...");

    // Get connection strings from Aspire resources
    _postgresConnection = await _app.GetConnectionStringAsync("whizbang-integration-test", cancellationToken)
      ?? throw new InvalidOperationException("Failed to get PostgreSQL connection string");

    _serviceBusConnection = await _app.GetConnectionStringAsync("sbemulatorns", cancellationToken)
      ?? throw new InvalidOperationException("Failed to get Service Bus connection string");

    Console.WriteLine($"[AspireFixture] PostgreSQL Connection: {_postgresConnection}");
    Console.WriteLine($"[AspireFixture] Service Bus Connection: {_serviceBusConnection}");

    // Wait for PostgreSQL to be ready before proceeding
    // Aspire starts containers but doesn't guarantee they're accepting connections
    Console.WriteLine("[AspireFixture] Waiting for PostgreSQL to be ready...");
    await _waitForPostgresReadyAsync(cancellationToken);

    // Create service hosts (but don't start them yet)
    _inventoryHost = _createInventoryHost(_postgresConnection, _serviceBusConnection);
    _bffHost = _createBffHost(_postgresConnection, _serviceBusConnection);

    Console.WriteLine("[AspireFixture] Service hosts created. Initializing schema...");

    // Initialize PostgreSQL schema using EFCore DbContexts
    await _initializeSchemaAsync(cancellationToken);

    Console.WriteLine("[AspireFixture] Schema initialized.");

    // Clean up any stale data from previous test runs BEFORE starting workers
    // This removes accumulated perspective checkpoints that cause timeouts
    Console.WriteLine("[AspireFixture] Cleaning stale data from previous test runs...");
    await CleanupDatabaseAsync(cancellationToken);
    Console.WriteLine("[AspireFixture] Database cleaned.");

    // WORKAROUND: Azure Service Bus Emulator reports ready before it can actually process messages
    // Wait for the emulator to be truly ready BEFORE starting service hosts
    // If we start hosts first, ServiceBusConsumerWorker will try to connect immediately and fail
    // See: https://github.com/Azure/azure-service-bus-emulator-installer/issues/35
    Console.WriteLine("[AspireFixture] Waiting for Azure Service Bus Emulator to be fully ready...");
    await _waitForServiceBusEmulatorReadyAsync(cancellationToken);

    // CRITICAL: Aspire does NOT automatically add $Default TrueFilter rules despite documentation claims
    // We must manually add them to ensure messages are delivered to all subscriptions
    Console.WriteLine("[AspireFixture] Manually adding $Default TrueFilter rules to all subscriptions...");
    await _addServiceBusSubscriptionRulesAsync(cancellationToken);
    Console.WriteLine("[AspireFixture] Subscription rules configured.");

    Console.WriteLine("[AspireFixture] Starting service hosts...");

    // Start service hosts (ServiceBusConsumerWorker can now connect successfully)
    await Task.WhenAll(
      _inventoryHost.StartAsync(cancellationToken),
      _bffHost.StartAsync(cancellationToken)
    );

    // Create long-lived scopes for lens access
    // These scopes persist for the lifetime of the fixture to allow scoped lens services
    _inventoryScope = _inventoryHost.Services.CreateScope();
    _bffScope = _bffHost.Services.CreateScope();

    Console.WriteLine("[AspireFixture] Service hosts started and ready!");

    _isInitialized = true;
  }

  /// <summary>
  /// Creates the IHost for InventoryWorker with all required services and background workers.
  /// </summary>
  [RequiresUnreferencedCode("Calls Npgsql.NpgsqlDataSourceBuilder.EnableDynamicJson(Type[], Type[])")]
  [RequiresDynamicCode("Calls Npgsql.NpgsqlDataSourceBuilder.EnableDynamicJson(Type[], Type[])")]
  private IHost _createInventoryHost(string postgresConnection, string serviceBusConnection) {
    var builder = Host.CreateApplicationBuilder();

    // Register service instance provider (uses unique instance ID to avoid partition claiming conflicts)
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp => new TestServiceInstanceProvider(_inventoryInstanceId, "InventoryWorker"));

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
    // IMPORTANT: Lenses must be Scoped (not Singleton) because they depend on ILensQuery<T> which is Scoped
    builder.Services.AddScoped<IProductLens, ProductLens>();
    builder.Services.AddScoped<IInventoryLens, InventoryLens>();

    // Register perspective runners (generated by PerspectiveRunnerRegistryGenerator)
    ECommerce.InventoryWorker.Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(builder.Services);

    // Register concrete perspective types for runner resolution
    builder.Services.AddScoped<ECommerce.InventoryWorker.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<ECommerce.InventoryWorker.Perspectives.ProductCatalogPerspective>();

    // Register Service Bus consumer subscriptions for InventoryWorker's own perspectives
    var consumerOptions = new ServiceBusConsumerOptions();
    consumerOptions.Subscriptions.Add(new TopicSubscription("products", "products-inventory-worker"));
    consumerOptions.Subscriptions.Add(new TopicSubscription("inventory", "inventory-inventory-worker"));
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

    // Register InstantCompletionStrategy for immediate perspective completion reporting (test optimization)
    builder.Services.AddSingleton<IPerspectiveCompletionStrategy, InstantCompletionStrategy>();

    // Configure PerspectiveWorker with faster polling for integration tests
    builder.Services.Configure<PerspectiveWorkerOptions>(options => {
      options.PollingIntervalMilliseconds = 100;  // Fast polling for tests
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = false;  // Disable diagnostic logging for cleaner test output
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;  // Require 2 empty polls to consider idle
    });

    // Register background workers
    builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();
    builder.Services.AddHostedService<PerspectiveWorker>();  // Processes perspective checkpoints
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
  private IHost _createBffHost(string postgresConnection, string serviceBusConnection) {
    var builder = Host.CreateApplicationBuilder();

    // Register service instance provider (uses unique instance ID to avoid partition claiming conflicts)
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp => new TestServiceInstanceProvider(_bffInstanceId, "BFF.API"));

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

    // Register InstantCompletionStrategy for immediate perspective completion reporting (test optimization)
    builder.Services.AddSingleton<IPerspectiveCompletionStrategy, InstantCompletionStrategy>();

    // Configure PerspectiveWorker with faster polling for integration tests
    builder.Services.Configure<PerspectiveWorkerOptions>(options => {
      options.PollingIntervalMilliseconds = 100;  // Fast polling for tests
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = false;  // Disable diagnostic logging for cleaner test output
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;  // Require 2 empty polls to consider idle
    });

    // Register background workers
    builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();
    builder.Services.AddHostedService<PerspectiveWorker>();  // Processes perspective checkpoints

    // Register Service Bus consumer to receive events
    var consumerOptions = new ServiceBusConsumerOptions();
    consumerOptions.Subscriptions.Add(new TopicSubscription("products", "products-bff-service"));
    consumerOptions.Subscriptions.Add(new TopicSubscription("inventory", "inventory-bff-service"));
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
  private async Task _initializeSchemaAsync(CancellationToken cancellationToken = default) {
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

    // Seed message associations for perspectives
    // These associations tell ProcessWorkBatchAsync which perspectives to invoke for which events
    using (var scope = _inventoryHost!.Services.CreateScope()) {
      var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();

      // Seed associations for InventoryWorker.ProductCatalogPerspective
      await dbContext.Database.ExecuteSqlRawAsync(@"
        INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
        VALUES
          ('ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts', 'perspective', 'ProductCatalogPerspective', 'ECommerce.InventoryWorker', NOW(), NOW()),
          ('ECommerce.Contracts.Events.ProductUpdatedEvent, ECommerce.Contracts', 'perspective', 'ProductCatalogPerspective', 'ECommerce.InventoryWorker', NOW(), NOW()),
          ('ECommerce.Contracts.Events.ProductDeletedEvent, ECommerce.Contracts', 'perspective', 'ProductCatalogPerspective', 'ECommerce.InventoryWorker', NOW(), NOW())
        ON CONFLICT (message_type, association_type, target_name, service_name) DO NOTHING
      ", cancellationToken);

      // Seed associations for InventoryWorker.InventoryLevelsPerspective
      await dbContext.Database.ExecuteSqlRawAsync(@"
        INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
        VALUES
          ('ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts', 'perspective', 'InventoryLevelsPerspective', 'ECommerce.InventoryWorker', NOW(), NOW()),
          ('ECommerce.Contracts.Events.InventoryRestockedEvent, ECommerce.Contracts', 'perspective', 'InventoryLevelsPerspective', 'ECommerce.InventoryWorker', NOW(), NOW()),
          ('ECommerce.Contracts.Events.InventoryReservedEvent, ECommerce.Contracts', 'perspective', 'InventoryLevelsPerspective', 'ECommerce.InventoryWorker', NOW(), NOW()),
          ('ECommerce.Contracts.Events.InventoryAdjustedEvent, ECommerce.Contracts', 'perspective', 'InventoryLevelsPerspective', 'ECommerce.InventoryWorker', NOW(), NOW())
        ON CONFLICT (message_type, association_type, target_name, service_name) DO NOTHING
      ", cancellationToken);

      // Seed associations for BFF.ProductCatalogPerspective
      await dbContext.Database.ExecuteSqlRawAsync(@"
        INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
        VALUES
          ('ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts', 'perspective', 'ProductCatalogPerspective', 'ECommerce.BFF.API', NOW(), NOW()),
          ('ECommerce.Contracts.Events.ProductUpdatedEvent, ECommerce.Contracts', 'perspective', 'ProductCatalogPerspective', 'ECommerce.BFF.API', NOW(), NOW()),
          ('ECommerce.Contracts.Events.ProductDeletedEvent, ECommerce.Contracts', 'perspective', 'ProductCatalogPerspective', 'ECommerce.BFF.API', NOW(), NOW())
        ON CONFLICT (message_type, association_type, target_name, service_name) DO NOTHING
      ", cancellationToken);

      // Seed associations for BFF.InventoryLevelsPerspective
      await dbContext.Database.ExecuteSqlRawAsync(@"
        INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
        VALUES
          ('ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts', 'perspective', 'InventoryLevelsPerspective', 'ECommerce.BFF.API', NOW(), NOW()),
          ('ECommerce.Contracts.Events.InventoryRestockedEvent, ECommerce.Contracts', 'perspective', 'InventoryLevelsPerspective', 'ECommerce.BFF.API', NOW(), NOW()),
          ('ECommerce.Contracts.Events.InventoryReservedEvent, ECommerce.Contracts', 'perspective', 'InventoryLevelsPerspective', 'ECommerce.BFF.API', NOW(), NOW()),
          ('ECommerce.Contracts.Events.InventoryAdjustedEvent, ECommerce.Contracts', 'perspective', 'InventoryLevelsPerspective', 'ECommerce.BFF.API', NOW(), NOW())
        ON CONFLICT (message_type, association_type, target_name, service_name) DO NOTHING
      ", cancellationToken);
    }
  }

  /// <summary>
  /// Waits for PostgreSQL to be ready by attempting to connect until successful.
  /// Aspire starts containers but doesn't guarantee they're accepting connections.
  /// </summary>
  private async Task _waitForPostgresReadyAsync(CancellationToken cancellationToken = default) {
    var maxAttempts = 30; // 30 seconds total
    for (var attempt = 1; attempt <= maxAttempts; attempt++) {
      try {
        using var dataSource = new Npgsql.NpgsqlDataSourceBuilder(_postgresConnection!).Build();
        using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        Console.WriteLine($"[AspireFixture] PostgreSQL connection successful (attempt {attempt})");
        return;
      } catch (Exception ex) when (attempt < maxAttempts) {
        // PostgreSQL not ready yet, wait and retry
        Console.WriteLine($"[AspireFixture] PostgreSQL not ready (attempt {attempt}): {ex.Message}");
        await Task.Delay(1000, cancellationToken);
      }
    }

    throw new TimeoutException($"PostgreSQL failed to accept connections after {maxAttempts} attempts");
  }

  /// <summary>
  /// Waits for the Azure Service Bus Emulator to be fully ready.
  /// The emulator takes 60-90 seconds to initialize on ARM64 (start SQL Server, create databases, provision topics).
  /// The pre-configured Config.json with $Default TrueFilter rules is mounted at container startup via Aspire.
  /// </summary>
  private async Task _waitForServiceBusEmulatorReadyAsync(CancellationToken cancellationToken = default) {
    // WORKAROUND: Azure Service Bus Emulator takes significant time to initialize:
    // 1. Start SQL Server (15s initial wait + retries)
    // 2. Create databases (SbGatewayDatabase, SbMessageContainerDatabase00001)
    // 3. Provision topics and subscriptions (using our pre-configured Config.json)
    // 4. Report "Emulator Service is Successfully Up!"
    //
    // Total time: 60-90 seconds on ARM64, potentially longer under load
    // User observed: "even with the sample app, aspire shows the services bus and all of it topics
    // and subscriptions as unhealthy for 30-60 seconds on startup"
    //
    // See: https://github.com/Azure/azure-service-bus-emulator-installer/issues/35
    // See: https://github.com/dotnet/aspire/issues/8818
    Console.WriteLine("[AspireFixture] Waiting 120 seconds for Azure Service Bus Emulator to fully initialize...");
    Console.WriteLine("[AspireFixture] (Emulator needs to: start SQL Server, create databases, provision topics/subscriptions from config file)");
    await Task.Delay(120000, cancellationToken);
    Console.WriteLine("[AspireFixture] Emulator should now be initialized with $Default TrueFilter rules from mounted config");
  }

  /// <summary>
  /// Adds $Default TrueFilter rules to all Service Bus subscriptions by modifying the emulator's config file.
  /// Uses Docker cp + C# JSON manipulation since ServiceBusAdministrationClient isn't supported by emulator.
  /// </summary>
  private async Task _addServiceBusSubscriptionRulesAsync(CancellationToken cancellationToken = default) {
    // Find the Service Bus emulator container
    // Create a shell script to execute docker commands with proper environment
    var scriptPath = Path.Combine(Path.GetTempPath(), $"docker-{Guid.NewGuid()}.sh");
    try {
      await File.WriteAllTextAsync(scriptPath, @"#!/bin/bash
export PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin
docker ps --filter ""ancestor=mcr.microsoft.com/azure-messaging/servicebus-emulator"" --format ""{{.ID}}""
", cancellationToken);

      // Execute the script through bash
      var findResult = await _runShellScriptAsync(scriptPath, cancellationToken);
      var containerId = findResult.Trim();

      if (string.IsNullOrEmpty(containerId)) {
        throw new InvalidOperationException("Could not find Service Bus emulator container");
      }

      Console.WriteLine($"[AspireFixture] Found Service Bus emulator container: {containerId}");

      // Create temp files
      var tempConfigPath = Path.GetTempFileName();
      var tempModifiedPath = Path.GetTempFileName();

      // Copy config file from container to host using shell script
      var copyFromScriptPath = Path.Combine(Path.GetTempPath(), $"docker-copy-from-{Guid.NewGuid()}.sh");
      await File.WriteAllTextAsync(copyFromScriptPath, $@"#!/bin/bash
export PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin
docker cp {containerId}:/ServiceBus_Emulator/ConfigFiles/Config.json ""{tempConfigPath}""
", cancellationToken);

      await _runShellScriptAsync(copyFromScriptPath, cancellationToken);
      File.Delete(copyFromScriptPath);
      Console.WriteLine("[AspireFixture] Copied config from container");

      // Read and modify JSON using System.Text.Json
      var configJson = await File.ReadAllTextAsync(tempConfigPath, cancellationToken);
      var config = System.Text.Json.JsonDocument.Parse(configJson);
      var configDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(configJson);

      if (configDict == null) {
        throw new InvalidOperationException("Failed to parse Service Bus config JSON");
      }

      // Navigate to UserConfig.Namespaces and add rules
      var modified = false;
      if (configDict.TryGetValue("UserConfig", out var userConfigObj) && userConfigObj is System.Text.Json.JsonElement userConfigElem) {
        var userConfigStr = userConfigElem.GetRawText();
        var userConfig = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(userConfigStr);

        if (userConfig != null && userConfig.TryGetValue("Namespaces", out var namespacesObj) && namespacesObj is System.Text.Json.JsonElement namespacesElem) {
          var namespacesList = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object>>>(namespacesElem.GetRawText());

          if (namespacesList != null) {
            foreach (var ns in namespacesList) {
              if (ns.TryGetValue("Topics", out var topicsObj) && topicsObj is System.Text.Json.JsonElement topicsElem) {
                var topicsList = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object>>>(topicsElem.GetRawText());

                if (topicsList != null) {
                  foreach (var topic in topicsList) {
                    var topicName = topic.TryGetValue("Name", out var tn) && tn is System.Text.Json.JsonElement tne ? tne.GetString() : "unknown";

                    if (topic.TryGetValue("Subscriptions", out var subsObj) && subsObj is System.Text.Json.JsonElement subsElem) {
                      var subsList = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object>>>(subsElem.GetRawText());

                      if (subsList != null) {
                        foreach (var sub in subsList) {
                          var subName = sub.TryGetValue("Name", out var sn) && sn is System.Text.Json.JsonElement sne ? sne.GetString() : "unknown";

                          // Check if Rules array exists and is empty
                          var hasRules = sub.TryGetValue("Rules", out var rulesObj) &&
                                        rulesObj is System.Text.Json.JsonElement rulesElem &&
                                        rulesElem.GetArrayLength() > 0;

                          if (!hasRules) {
                            // Add $Default TrueFilter rule
                            var defaultRule = new Dictionary<string, object> {
                              ["Name"] = "$Default",
                              ["Properties"] = new Dictionary<string, object> {
                                ["FilterType"] = 0,  // SqlFilter
                                ["SqlFilter"] = new Dictionary<string, object> {
                                  ["SqlExpression"] = "1=1"  // TrueFilter - matches all messages
                                }
                              }
                            };
                            sub["Rules"] = new List<object> { defaultRule };
                            Console.WriteLine($"[AspireFixture] Added $Default rule to {topicName}/{subName}");
                            modified = true;
                          } else {
                            Console.WriteLine($"[AspireFixture] Subscription {topicName}/{subName} already has rules, skipping");
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
          }
        }
      }

      if (modified) {
        // Serialize modified config back to JSON
        var modifiedJson = System.Text.Json.JsonSerializer.Serialize(configDict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tempModifiedPath, modifiedJson, cancellationToken);

        // Copy modified config back to container using shell script
        var copyToScriptPath = Path.Combine(Path.GetTempPath(), $"docker-copy-to-{Guid.NewGuid()}.sh");
        await File.WriteAllTextAsync(copyToScriptPath, $@"#!/bin/bash
export PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin
docker cp ""{tempModifiedPath}"" {containerId}:/ServiceBus_Emulator/ConfigFiles/Config.json
", cancellationToken);

        await _runShellScriptAsync(copyToScriptPath, cancellationToken);
        File.Delete(copyToScriptPath);
        Console.WriteLine("[AspireFixture] Copied modified config back to container");

        Console.WriteLine("[AspireFixture] Successfully added $Default TrueFilter rules to all subscriptions");
      } else {
        Console.WriteLine("[AspireFixture] All subscriptions already have rules, no modifications needed");
      }

      // Clean up temp files
      File.Delete(tempConfigPath);
      if (File.Exists(tempModifiedPath)) {
        File.Delete(tempModifiedPath);
      }

      // Verify the rules were added using shell script (non-fatal)
      try {
        var verifyScriptPath = Path.Combine(Path.GetTempPath(), $"docker-verify-{Guid.NewGuid()}.sh");
        await File.WriteAllTextAsync(verifyScriptPath, $@"#!/bin/bash
export PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin
docker exec {containerId} cat /ServiceBus_Emulator/ConfigFiles/Config.json
", cancellationToken);

        var updatedConfig = await _runShellScriptAsync(verifyScriptPath, cancellationToken);
        File.Delete(verifyScriptPath);

        if (updatedConfig.Contains("\"Name\":\"$Default\"")) {
          Console.WriteLine("[AspireFixture] Verified: $Default rules present in config");
        } else {
          Console.WriteLine("[AspireFixture] WARNING: $Default rules not found in updated config!");
        }
      } catch (Exception verifyEx) {
        Console.WriteLine($"[AspireFixture] WARNING: Could not verify rules (non-fatal): {verifyEx.Message}");
        Console.WriteLine("[AspireFixture] Rules were successfully added and copied to container, verification skipped");
      }
    } catch (Exception ex) {
      Console.WriteLine($"[AspireFixture] ERROR adding subscription rules: {ex.Message}");
      throw;
    } finally {
      // Clean up the initial find container script
      if (File.Exists(scriptPath)) {
        File.Delete(scriptPath);
      }
    }
  }

  private async Task<string> _runShellScriptAsync(string scriptPath, CancellationToken cancellationToken = default) {
    // Execute script through bash explicitly instead of directly
    var processInfo = new System.Diagnostics.ProcessStartInfo {
      FileName = "/bin/bash",
      Arguments = scriptPath,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    using var process = System.Diagnostics.Process.Start(processInfo);
    if (process == null) {
      throw new InvalidOperationException($"Failed to start process for script: {scriptPath}");
    }

    var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
    var error = await process.StandardError.ReadToEndAsync(cancellationToken);

    await process.WaitForExitAsync(cancellationToken);

    if (process.ExitCode != 0) {
      throw new InvalidOperationException($"Script failed with exit code {process.ExitCode}: {error}\nOutput: {output}");
    }

    return output;
  }

  /// <summary>
  /// Waits for all event processing to complete by querying database tables directly.
  /// Checks for any uncompleted outbox/inbox messages and perspective checkpoints.
  /// This is more reliable than using ProcessWorkBatchAsync which only shows available (not in-progress) work.
  /// </summary>
  public async Task WaitForEventProcessingAsync(int timeoutMilliseconds = 60000) {
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    const int pollIntervalMs = 100;  // Poll every 100ms

    while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds) {
      using var scope = _inventoryHost!.Services.CreateScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();

      // Query database directly for any uncompleted work using ADO.NET
      var connection = dbContext.Database.GetDbConnection();
      if (connection.State != System.Data.ConnectionState.Open) {
        await connection.OpenAsync();
      }
      await using var cmd = connection.CreateCommand();

      // Check outbox: any messages not marked as Sent (status & 2 = 0)
      cmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM wh_outbox WHERE (status & 2) = 0";
      var pendingOutbox = (int)(await cmd.ExecuteScalarAsync() ?? 0);

      // Check inbox: any messages not marked as Completed (status & 2 = 0)
      cmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM wh_inbox WHERE (status & 2) = 0";
      var pendingInbox = (int)(await cmd.ExecuteScalarAsync() ?? 0);

      // Check perspective checkpoints: any not marked as Completed (status & 2 = 0) AND not Failed (status & 4 = 0)
      cmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM wh_perspective_checkpoints WHERE (status & 2) = 0 AND (status & 4) = 0";
      var pendingPerspectives = (int)(await cmd.ExecuteScalarAsync() ?? 0);

      if (pendingOutbox == 0 && pendingInbox == 0 && pendingPerspectives == 0) {
        Console.WriteLine($"[AspireFixture] Event processing complete - no pending work (checked database after {stopwatch.ElapsedMilliseconds}ms)");
        return;
      }

      // Log what's still pending
      if (pendingOutbox > 0 || pendingInbox > 0 || pendingPerspectives > 0) {
        Console.WriteLine($"[AspireFixture] Still processing: Outbox={pendingOutbox}, Inbox={pendingInbox}, Perspectives={pendingPerspectives} (elapsed: {stopwatch.ElapsedMilliseconds}ms)");
      }

      // Wait before next poll
      await Task.Delay(pollIntervalMs);
    }

    // Timeout reached - log final state
    Console.WriteLine($"[AspireFixture] WARNING: Event processing did not complete within {timeoutMilliseconds}ms timeout");

    using var finalScope = _inventoryHost!.Services.CreateScope();
    var finalDbContext = finalScope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();

    var finalConnection = finalDbContext.Database.GetDbConnection();
    if (finalConnection.State != System.Data.ConnectionState.Open) {
      await finalConnection.OpenAsync();
    }
    await using var finalCmd = finalConnection.CreateCommand();

    finalCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM wh_outbox WHERE (status & 2) = 0";
    var finalOutbox = (int)(await finalCmd.ExecuteScalarAsync() ?? 0);

    finalCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM wh_inbox WHERE (status & 2) = 0";
    var finalInbox = (int)(await finalCmd.ExecuteScalarAsync() ?? 0);

    finalCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM wh_perspective_checkpoints WHERE (status & 2) = 0 AND (status & 4) = 0";
    var finalPerspectives = (int)(await finalCmd.ExecuteScalarAsync() ?? 0);

    Console.WriteLine($"[AspireFixture] Final state: Outbox={finalOutbox}, Inbox={finalInbox}, Perspectives={finalPerspectives}");
  }

  /// <summary>
  /// Cleans up all test data from the database (truncates all tables).
  /// Call this between test classes to ensure isolation.
  /// Gracefully handles the case where the database container has already stopped.
  /// </summary>
  public async Task CleanupDatabaseAsync(CancellationToken cancellationToken = default) {
    if (!_isInitialized) {
      return;
    }

    // Truncate all Whizbang tables in the shared database
    // Both InventoryWorker and BFF share the same database, so we only need to truncate once
    // Gracefully handle connection failures (container may have stopped after test completion)
    try {
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
    } catch (Npgsql.NpgsqlException ex) when (ex.Message.Contains("Failed to connect")) {
      // Database container has been stopped - this is expected during test teardown
      // Silently ignore connection failures since cleanup is not critical after tests complete
      Console.WriteLine("[AspireFixture] Database cleanup skipped - container already stopped");
    }
  }

  /// <summary>
  /// DIAGNOSTIC: Query event types and message associations after events are written.
  /// Helps identify naming mismatches between event_type and message_type columns.
  /// </summary>
  public async Task DumpEventTypesAndAssociationsAsync(CancellationToken cancellationToken = default) {
    using var scope = _inventoryHost!.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();

    var output = new System.Text.StringBuilder();
    output.AppendLine("[DIAGNOSTIC] ===== EVENT TYPE DIAGNOSTIC =====");
    Console.WriteLine("[DIAGNOSTIC] ===== EVENT TYPE DIAGNOSTIC =====");

    // Use ADO.NET directly to avoid EF Core scalar query issues
    var connection = dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken);
    }

    // Query actual event types in event store
    await using (var cmd = connection.CreateCommand()) {
      cmd.CommandText = "SELECT DISTINCT event_type FROM wh_event_store ORDER BY event_type LIMIT 20";
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      var count = 0;
      while (await reader.ReadAsync(cancellationToken)) {
        var eventType = reader.GetString(0);
        output.AppendLine($"[DIAGNOSTIC]   event_type: '{eventType}'");
        Console.WriteLine($"[DIAGNOSTIC]   event_type: '{eventType}'");
        count++;
      }
      output.AppendLine($"[DIAGNOSTIC] Found {count} distinct event types in wh_event_store");
      Console.WriteLine($"[DIAGNOSTIC] Found {count} distinct event types in wh_event_store");
    }

    // Query message associations
    await using (var cmd = connection.CreateCommand()) {
      cmd.CommandText = "SELECT DISTINCT message_type FROM wh_message_associations WHERE association_type = 'perspective' ORDER BY message_type LIMIT 20";
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      var count = 0;
      while (await reader.ReadAsync(cancellationToken)) {
        var msgType = reader.GetString(0);
        output.AppendLine($"[DIAGNOSTIC]   message_type: '{msgType}'");
        Console.WriteLine($"[DIAGNOSTIC]   message_type: '{msgType}'");
        count++;
      }
      output.AppendLine($"[DIAGNOSTIC] Found {count} message_type values in wh_message_associations");
      Console.WriteLine($"[DIAGNOSTIC] Found {count} message_type values in wh_message_associations");
    }

    // Query perspective checkpoints created
    await using (var cmd = connection.CreateCommand()) {
      cmd.CommandText = "SELECT COUNT(*)::int FROM wh_perspective_checkpoints";
      var checkpointCount = (int)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0);
      output.AppendLine($"[DIAGNOSTIC] Found {checkpointCount} perspective checkpoints in wh_perspective_checkpoints");
      Console.WriteLine($"[DIAGNOSTIC] Found {checkpointCount} perspective checkpoints in wh_perspective_checkpoints");
    }

    output.AppendLine("[DIAGNOSTIC] ===== END DIAGNOSTIC =====");
    Console.WriteLine("[DIAGNOSTIC] ===== END DIAGNOSTIC =====");

    // Write to file for examination
    await System.IO.File.WriteAllTextAsync("/tmp/event-type-diagnostic.log", output.ToString(), cancellationToken);
  }

  public async ValueTask DisposeAsync() {
    if (_isInitialized) {
      // Dispose scopes first (before stopping hosts)
      _inventoryScope?.Dispose();
      _bffScope?.Dispose();

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
