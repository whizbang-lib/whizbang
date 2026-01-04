using System.Diagnostics.CodeAnalysis;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Generated;
using ECommerce.Contracts.Lenses;
using ECommerce.InventoryWorker.Generated;
using ECommerce.InventoryWorker.Lenses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Testcontainers.ServiceBus;
using Whizbang.Core;
using Whizbang.Core.Lenses;
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
  private readonly INetwork _network;  // Network for Service Bus + SQL Server
  private readonly MsSqlContainer _mssqlContainer;  // SQL Server for Service Bus emulator
  private readonly ServiceBusContainer _serviceBusContainer;
  private bool _isInitialized;
  private IHost? _inventoryHost;
  private IHost? _bffHost;
  private readonly Guid _sharedInstanceId = Guid.CreateVersion7(); // Shared across both services for partition claiming
  private Azure.Messaging.ServiceBus.ServiceBusClient? _sharedServiceBusClient; // CRITICAL: Single shared client across both hosts

  public SharedIntegrationFixture() {
    _postgresContainer = new PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
      .WithDatabase("whizbang_integration_test")
      .WithUsername("whizbang_user")
      .WithPassword("whizbang_pass")
      .Build();

    // Create network for Service Bus emulator and SQL Server
    _network = new NetworkBuilder().Build();

    // Create SQL Server container with increased memory for Service Bus emulator
    // The Service Bus emulator requires SQL Server and can run out of memory on ARM64
    // 6GB is required for SQL Server 2022 on ARM64 to handle the Service Bus emulator workload
    _mssqlContainer = new MsSqlBuilder()
      .WithImage("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
      .WithNetwork(_network)
      .WithNetworkAliases("database-container")  // Network alias for Service Bus emulator to find SQL Server
      .WithCreateParameterModifier(x => x.HostConfig.Memory = 6L * 1024 * 1024 * 1024)  // 6GB memory limit for ARM64
      .Build();

    // Configure topics and subscriptions using ServiceBusBuilder's WithConfig API
    var configPath = Path.Combine(AppContext.BaseDirectory, "servicebus-config.json");
    _serviceBusContainer = new ServiceBusBuilder()
      .WithImage("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
      .WithAcceptLicenseAgreement(true)
      .WithConfig(configPath)  // Use Testcontainers API instead of generic WithBindMount
      .WithMsSqlContainer(_network, _mssqlContainer, "database-container")  // Use our SQL Server container with increased memory
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

    // Start network first
    await _network.CreateAsync(cancellationToken);

    // Start containers in parallel
    // SQL Server must start before Service Bus emulator (dependency)
    await Task.WhenAll(
      _postgresContainer.StartAsync(cancellationToken),
      _mssqlContainer.StartAsync(cancellationToken)
    );

    await _serviceBusContainer.StartAsync(cancellationToken);

    Console.WriteLine("[SharedFixture] Containers started. Waiting for PostgreSQL to be ready...");

    // Get connection strings
    var postgresConnection = _postgresContainer.GetConnectionString();
    var serviceBusConnectionRaw = _serviceBusContainer.GetConnectionString();

    // CRITICAL: Convert Testcontainers AMQP connection string to Service Bus format
    // Testcontainers generates: Endpoint=amqp://127.0.0.1:PORT/;SharedAccessKeyName=...
    // Azure Service Bus client expects: Endpoint=sb://localhost:PORT;SharedAccessKeyName=...;UseDevelopmentEmulator=true
    // See: https://github.com/Azure/azure-service-bus-emulator-installer/issues/51
    var serviceBusConnection = _convertToServiceBusConnectionString(serviceBusConnectionRaw);

    Console.WriteLine($"[SharedFixture] PostgreSQL Connection: {postgresConnection}");
    Console.WriteLine($"[SharedFixture] Service Bus Connection (raw): {serviceBusConnectionRaw}");
    Console.WriteLine($"[SharedFixture] Service Bus Connection (converted): {serviceBusConnection}");

    // Wait for PostgreSQL to be ready to accept connections
    await _waitForPostgresReadyAsync(postgresConnection, cancellationToken);

    Console.WriteLine("[SharedFixture] PostgreSQL ready. Creating SHARED ServiceBusClient...");

    // CRITICAL: Create single shared ServiceBusClient BEFORE creating hosts
    // This client will be registered in both hosts' DI containers to avoid connection quota issues
    _sharedServiceBusClient = new Azure.Messaging.ServiceBus.ServiceBusClient(serviceBusConnection);
    Console.WriteLine("[SharedFixture] Shared ServiceBusClient created. Creating service hosts...");

    // Create service hosts (but don't start them yet)
    // Both hosts will use the shared ServiceBusClient instance
    _inventoryHost = _createInventoryHost(postgresConnection, serviceBusConnection);
    _bffHost = _createBffHost(postgresConnection, serviceBusConnection);

    Console.WriteLine("[SharedFixture] Service hosts created. Initializing schema...");

    // Initialize PostgreSQL schema using EFCore DbContexts
    await _initializeSchemaAsync(cancellationToken);

    Console.WriteLine("[SharedFixture] Schema initialized. Starting service hosts...");

    // Start service hosts
    await Task.WhenAll(
      _inventoryHost.StartAsync(cancellationToken),
      _bffHost.StartAsync(cancellationToken)
    );

    // WORKAROUND: Azure Service Bus Emulator reports ready before it can actually process messages
    // Wait for the emulator to be truly ready by checking health endpoint and waiting 30s
    // See: https://github.com/Azure/azure-service-bus-emulator-installer/issues/35
    Console.WriteLine("[SharedFixture] Waiting for Azure Service Bus Emulator to be fully ready...");
    await _waitForServiceBusEmulatorReadyAsync(cancellationToken);

    Console.WriteLine("[SharedFixture] Service hosts started and ready!");

    _isInitialized = true;
  }

  /// <summary>
  /// Creates the IHost for InventoryWorker with all required services and background workers.
  /// </summary>
  [RequiresUnreferencedCode("Calls Npgsql.NpgsqlDataSourceBuilder.EnableDynamicJson(Type[], Type[])")]
  [RequiresDynamicCode("Calls Npgsql.NpgsqlDataSourceBuilder.EnableDynamicJson(Type[], Type[])")]
  private IHost _createInventoryHost(string postgresConnection, string serviceBusConnection) {
    var builder = Host.CreateApplicationBuilder();

    // Register service instance provider (uses shared instance ID for partition claiming compatibility)
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp => new TestServiceInstanceProvider(_sharedInstanceId, "InventoryWorker"));

    // CRITICAL: Register SHARED ServiceBusClient BEFORE calling AddAzureServiceBusTransport
    // This prevents creating multiple clients and hitting connection quota
    builder.Services.AddSingleton(_sharedServiceBusClient ?? throw new InvalidOperationException("Shared ServiceBusClient not initialized"));

    // Register Azure Service Bus transport (will reuse the shared client registered above)
    var jsonOptions = ECommerce.Contracts.Generated.WhizbangJsonContext.CreateOptions();
    builder.Services.AddAzureServiceBusTransport(serviceBusConnection);

    // Add trace store for observability
    builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

    // Register OrderedStreamProcessor for message ordering
    builder.Services.AddSingleton<OrderedStreamProcessor>();

    // Register JsonSerializerOptions for Npgsql JSONB serialization
    builder.Services.AddSingleton(jsonOptions);

    // Register EF Core DbContext with NpgsqlDataSource (required for EnableDynamicJson)
    // IMPORTANT: ConfigureJsonOptions() MUST be called BEFORE EnableDynamicJson() (Npgsql bug #5562)
    // This registers WhizbangId JSON converters for JSONB serialization
    var inventoryDataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(postgresConnection);
    inventoryDataSourceBuilder.ConfigureJsonOptions(jsonOptions);
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
      options.DebugMode = true;  // DIAGNOSTIC: Enable SQL debug logging
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;  // Require 2 empty polls to consider idle
    });

    // Register perspective invoker for scoped event processing (use InventoryWorker's generated invoker)
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddWhizbangPerspectiveInvoker(builder.Services);

    // Register perspective runners for AOT-compatible lookup (replaces reflection)
    ECommerce.InventoryWorker.Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(builder.Services);

    // Register Whizbang dispatcher with outbox and transport support
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddWhizbangDispatcher(builder.Services);

    // NOTE: ILensQuery<T> registrations are now auto-generated by EFCoreServiceRegistrationGenerator
    // in GeneratedModelRegistration.Initialize() for all discovered perspectives

    // Register lenses for querying materialized views
    // IMPORTANT: Lenses must be Scoped (not Singleton) because they depend on ILensQuery<T> which is Scoped
    builder.Services.AddScoped<IProductLens, ProductLens>();
    builder.Services.AddScoped<IInventoryLens, InventoryLens>();

    // Register InventoryWorker perspectives manually (avoid ambiguity with BFF perspectives)
    // NEW: Converted perspectives - registered by AddPerspectiveRunners, just need scoped instances for runner resolution
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

    // Configure PerspectiveWorker with faster polling for integration tests
    builder.Services.Configure<PerspectiveWorkerOptions>(options => {
      options.PollingIntervalMilliseconds = 100;  // Fast polling for tests
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = true;  // DIAGNOSTIC: Enable checkpoint tracking
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

    // Register service instance provider (uses shared instance ID for partition claiming compatibility)
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp => new TestServiceInstanceProvider(_sharedInstanceId, "BFF.API"));

    var jsonOptions = ECommerce.Contracts.Generated.WhizbangJsonContext.CreateOptions();

    // CRITICAL: Register SHARED ServiceBusClient BEFORE calling AddAzureServiceBusTransport
    // This prevents creating multiple clients and hitting connection quota
    builder.Services.AddSingleton(_sharedServiceBusClient ?? throw new InvalidOperationException("Shared ServiceBusClient not initialized"));

    // Register Azure Service Bus transport (will reuse the shared client registered above)
    builder.Services.AddAzureServiceBusTransport(serviceBusConnection);

    // Add trace store for observability
    builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

    // Register OrderedStreamProcessor for message ordering
    builder.Services.AddSingleton<OrderedStreamProcessor>();

    // Register JsonSerializerOptions for Npgsql JSONB serialization
    builder.Services.AddSingleton(jsonOptions);

    // Register EF Core DbContext with NpgsqlDataSource (required for EnableDynamicJson)
    // IMPORTANT: ConfigureJsonOptions() MUST be called BEFORE EnableDynamicJson() (Npgsql bug #5562)
    // This registers WhizbangId JSON converters for JSONB serialization
    var bffDataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(postgresConnection);
    bffDataSourceBuilder.ConfigureJsonOptions(jsonOptions);
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

    // Register perspective runners for AOT-compatible lookup (replaces reflection)
    ECommerce.BFF.API.Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(builder.Services);

    // Configure WorkCoordinatorPublisherWorker with faster polling for integration tests
    builder.Services.Configure<WorkCoordinatorPublisherOptions>(options => {
      options.PollingIntervalMilliseconds = 100;  // Fast polling for tests
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = true;  // DIAGNOSTIC: Enable SQL debug logging
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;  // Require 2 empty polls to consider idle
    });

    // NOTE: BFF.API doesn't have receptors, so no DispatcherRegistrations is generated
    // BFF only materializes perspectives - it doesn't send commands

    // Register BFF perspectives manually (avoid ambiguity with InventoryWorker perspectives)
    // NEW: Converted perspectives - registered by AddPerspectiveRunners, just need scoped instances for runner resolution
    builder.Services.AddScoped<ECommerce.BFF.API.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<ECommerce.BFF.API.Perspectives.ProductCatalogPerspective>();

    // NOTE: ILensQuery<T> registrations are now auto-generated by EFCoreServiceRegistrationGenerator
    // in GeneratedModelRegistration.Initialize() for all discovered perspectives

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

    // Configure PerspectiveWorker with faster polling for integration tests
    builder.Services.Configure<PerspectiveWorkerOptions>(options => {
      options.PollingIntervalMilliseconds = 100;  // Fast polling for tests
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = true;  // DIAGNOSTIC: Enable checkpoint tracking
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;  // Require 2 empty polls to consider idle
    });

    // Register background workers
    builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();
    builder.Services.AddHostedService<PerspectiveWorker>();  // Processes perspective checkpoints

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
  /// Converts Testcontainers AMQP connection string to Azure Service Bus format.
  /// Testcontainers generates: Endpoint=amqp://127.0.0.1:PORT/;SharedAccessKeyName=...;SharedAccessKey=...;UseDevelopmentEmulator=true
  /// Azure Service Bus client expects: Endpoint=sb://localhost:PORT;SharedAccessKeyName=...;SharedAccessKey=...;UseDevelopmentEmulator=true
  /// CRITICAL: The trailing slash after PORT must be removed, or the client fails silently.
  /// </summary>
  private static string _convertToServiceBusConnectionString(string amqpConnectionString) {
    // Replace amqp:// with sb://
    var result = amqpConnectionString.Replace("amqp://", "sb://");

    // Replace 127.0.0.1 with localhost (Azure Service Bus client prefers localhost)
    result = result.Replace("127.0.0.1", "localhost");

    // CRITICAL: Remove trailing slash from endpoint (sb://localhost:PORT/; → sb://localhost:PORT;)
    // The Azure Service Bus client silently fails to receive messages if the trailing slash is present
    result = System.Text.RegularExpressions.Regex.Replace(result, @":\d+/;", m => m.Value.Replace("/;", ";"));

    return result;
  }

  /// <summary>
  /// Waits for PostgreSQL to be ready to accept connections by attempting to open a connection.
  /// Polls up to 30 times (30 seconds total) with 1 second delay between attempts.
  /// </summary>
  private async Task _waitForPostgresReadyAsync(string connectionString, CancellationToken cancellationToken = default) {
    var maxAttempts = 30; // 30 seconds total
    for (var attempt = 1; attempt <= maxAttempts; attempt++) {
      try {
        using var dataSource = new Npgsql.NpgsqlDataSourceBuilder(connectionString).Build();
        using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        Console.WriteLine($"[SharedFixture] PostgreSQL connection successful (attempt {attempt})");
        return;
      } catch (Exception ex) when (attempt < maxAttempts) {
        Console.WriteLine($"[SharedFixture] PostgreSQL not ready (attempt {attempt}): {ex.Message}");
        await Task.Delay(1000, cancellationToken);
      }
    }

    throw new TimeoutException($"PostgreSQL failed to accept connections after {maxAttempts} attempts");
  }

  /// <summary>
  /// Waits for the Azure Service Bus Emulator to be fully ready by polling the health endpoint.
  /// The emulator reports "ready" before it can actually process messages, so we need to verify readiness.
  /// </summary>
  private async Task _waitForServiceBusEmulatorReadyAsync(CancellationToken cancellationToken = default) {
    // Get the health endpoint URL with the correct dynamically mapped port
    // The Service Bus container exposes port 5300 (health) which Testcontainers maps to a random host port
    var healthPort = _serviceBusContainer.GetMappedPublicPort(5300);
    var healthEndpoint = $"http://localhost:{healthPort}/health";
    Console.WriteLine($"[SharedFixture] Checking emulator health at {healthEndpoint}");

    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

    var maxAttempts = 30; // 30 seconds total
    for (var attempt = 1; attempt <= maxAttempts; attempt++) {
      try {
        var response = await httpClient.GetAsync(healthEndpoint, cancellationToken);
        if (response.IsSuccessStatusCode) {
          var content = await response.Content.ReadAsStringAsync(cancellationToken);
          if (content.Contains("healthy", StringComparison.OrdinalIgnoreCase)) {
            Console.WriteLine($"[SharedFixture] Azure Service Bus Emulator health check passed (attempt {attempt})");
            // WORKAROUND: The emulator's internal SQL Server needs time to start up, and the emulator
            // has built-in retry logic (15s wait + 2-3 retries × 15s) before creating databases.
            // Then it needs to create databases, topics, and subscriptions. Total time is ~150-180s.
            // Even after health check passes, we must wait for:
            // 1. SQL Server connection retries (30-45s)
            // 2. Database creation (SbGatewayDatabase, SbMessageContainerDatabase00001)
            // 3. Topic and subscription creation
            // 4. "Emulator Service is Successfully Up!" message
            // See: https://github.com/Azure/azure-service-bus-emulator-installer/issues/35
            Console.WriteLine("[SharedFixture] Waiting 180 seconds for emulator to fully initialize (SQL Server + databases + topics/subscriptions)...");
            await Task.Delay(180000, cancellationToken);
            Console.WriteLine("[SharedFixture] Emulator should now be ready for message sending");
            return;
          }
        }
      } catch {
        // Health endpoint not ready yet, continue waiting
      }

      if (attempt < maxAttempts) {
        await Task.Delay(1000, cancellationToken);
      }
    }

    throw new TimeoutException($"Azure Service Bus Emulator health check failed after {maxAttempts} attempts");
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
  }

  /// <summary>
  /// Waits for both InventoryWorker and BFF work processing to become idle.
  /// Tracks 4 workers: WorkCoordinatorPublisherWorker (outbox/inbox) + PerspectiveWorker (perspective materialization) for both services.
  /// Uses event callbacks to efficiently detect when all event processing is complete.
  /// Falls back to timeout if idle state is not reached within the specified time.
  /// </summary>
  public async Task WaitForEventProcessingAsync(int timeoutMilliseconds = 30000) {
    // Get WorkCoordinatorPublisherWorker instances (outbox/inbox processing)
    var inventoryPublisher = _inventoryHost!.Services.GetServices<IHostedService>()
      .OfType<WorkCoordinatorPublisherWorker>()
      .FirstOrDefault();

    var bffPublisher = _bffHost!.Services.GetServices<IHostedService>()
      .OfType<WorkCoordinatorPublisherWorker>()
      .FirstOrDefault();

    // Get PerspectiveWorker instances (perspective materialization)
    var inventoryPerspectiveWorker = _inventoryHost!.Services.GetServices<IHostedService>()
      .OfType<PerspectiveWorker>()
      .FirstOrDefault();

    var bffPerspectiveWorker = _bffHost!.Services.GetServices<IHostedService>()
      .OfType<PerspectiveWorker>()
      .FirstOrDefault();

    // Create TaskCompletionSources for all 4 workers
    var inventoryPublisherTcs = new TaskCompletionSource<bool>();
    var bffPublisherTcs = new TaskCompletionSource<bool>();
    var inventoryPerspectiveTcs = new TaskCompletionSource<bool>();
    var bffPerspectiveTcs = new TaskCompletionSource<bool>();

    // Wire up one-time idle callbacks
    WorkProcessingIdleHandler? inventoryPublisherHandler = null;
    WorkProcessingIdleHandler? bffPublisherHandler = null;
    WorkProcessingIdleHandler? inventoryPerspectiveHandler = null;
    WorkProcessingIdleHandler? bffPerspectiveHandler = null;

    inventoryPublisherHandler = () => {
      inventoryPublisherTcs.TrySetResult(true);
      if (inventoryPublisher != null && inventoryPublisherHandler != null) {
        inventoryPublisher.OnWorkProcessingIdle -= inventoryPublisherHandler;
      }
    };

    bffPublisherHandler = () => {
      bffPublisherTcs.TrySetResult(true);
      if (bffPublisher != null && bffPublisherHandler != null) {
        bffPublisher.OnWorkProcessingIdle -= bffPublisherHandler;
      }
    };

    inventoryPerspectiveHandler = () => {
      inventoryPerspectiveTcs.TrySetResult(true);
      if (inventoryPerspectiveWorker != null && inventoryPerspectiveHandler != null) {
        inventoryPerspectiveWorker.OnWorkProcessingIdle -= inventoryPerspectiveHandler;
      }
    };

    bffPerspectiveHandler = () => {
      bffPerspectiveTcs.TrySetResult(true);
      if (bffPerspectiveWorker != null && bffPerspectiveHandler != null) {
        bffPerspectiveWorker.OnWorkProcessingIdle -= bffPerspectiveHandler;
      }
    };

    // Register WorkCoordinatorPublisherWorker callbacks
    if (inventoryPublisher != null) {
      inventoryPublisher.OnWorkProcessingIdle += inventoryPublisherHandler;
      if (inventoryPublisher.IsIdle) {
        inventoryPublisherTcs.TrySetResult(true);
      }
    } else {
      inventoryPublisherTcs.TrySetResult(true);
    }

    if (bffPublisher != null) {
      bffPublisher.OnWorkProcessingIdle += bffPublisherHandler;
      if (bffPublisher.IsIdle) {
        bffPublisherTcs.TrySetResult(true);
      }
    } else {
      bffPublisherTcs.TrySetResult(true);
    }

    // Register PerspectiveWorker callbacks
    if (inventoryPerspectiveWorker != null) {
      inventoryPerspectiveWorker.OnWorkProcessingIdle += inventoryPerspectiveHandler;
      if (inventoryPerspectiveWorker.IsIdle) {
        inventoryPerspectiveTcs.TrySetResult(true);
      }
    } else {
      inventoryPerspectiveTcs.TrySetResult(true);
    }

    if (bffPerspectiveWorker != null) {
      bffPerspectiveWorker.OnWorkProcessingIdle += bffPerspectiveHandler;
      if (bffPerspectiveWorker.IsIdle) {
        bffPerspectiveTcs.TrySetResult(true);
      }
    } else {
      bffPerspectiveTcs.TrySetResult(true);
    }

    // Wait for all 4 workers to become idle (or timeout)
    using var cts = new CancellationTokenSource(timeoutMilliseconds);

    try {
      await Task.WhenAll(
        inventoryPublisherTcs.Task,
        bffPublisherTcs.Task,
        inventoryPerspectiveTcs.Task,
        bffPerspectiveTcs.Task
      ).WaitAsync(cts.Token);

      Console.WriteLine("[SharedFixture] Event processing idle - all workers have no pending work (2 publishers + 2 perspective workers)");
    } catch (OperationCanceledException) {
      Console.WriteLine($"[SharedFixture] WARNING: Event processing did not reach idle state within {timeoutMilliseconds}ms timeout");
      Console.WriteLine($"[SharedFixture] InventoryWorker Publisher idle: {inventoryPublisher?.IsIdle ?? true}, PerspectiveWorker idle: {inventoryPerspectiveWorker?.IsIdle ?? true}");
      Console.WriteLine($"[SharedFixture] BFF Publisher idle: {bffPublisher?.IsIdle ?? true}, PerspectiveWorker idle: {bffPerspectiveWorker?.IsIdle ?? true}");
    } finally {
      // Clean up handlers
      if (inventoryPublisher != null && inventoryPublisherHandler != null) {
        inventoryPublisher.OnWorkProcessingIdle -= inventoryPublisherHandler;
      }
      if (bffPublisher != null && bffPublisherHandler != null) {
        bffPublisher.OnWorkProcessingIdle -= bffPublisherHandler;
      }
      if (inventoryPerspectiveWorker != null && inventoryPerspectiveHandler != null) {
        inventoryPerspectiveWorker.OnWorkProcessingIdle -= inventoryPerspectiveHandler;
      }
      if (bffPerspectiveWorker != null && bffPerspectiveHandler != null) {
        bffPerspectiveWorker.OnWorkProcessingIdle -= bffPerspectiveHandler;
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
          TRUNCATE TABLE inventory.wh_event_store, inventory.wh_outbox, inventory.wh_inbox, inventory.wh_perspective_checkpoints, inventory.wh_receptor_processing CASCADE;

          -- Truncate all perspective tables (pattern: wh_per_*)
          -- This clears materialized views from both InventoryWorker and BFF
          TRUNCATE TABLE inventory.wh_per_inventory_level_dto CASCADE;
          TRUNCATE TABLE inventory.wh_per_order_read_model CASCADE;
          TRUNCATE TABLE inventory.wh_per_product_dto CASCADE;
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

      // Dispose shared ServiceBusClient AFTER hosts are stopped
      // The transport in each host is configured to NOT dispose the client (line 590 of AzureServiceBusTransport.cs)
      // so we're responsible for disposing it here
      if (_sharedServiceBusClient != null) {
        await _sharedServiceBusClient.DisposeAsync();
        Console.WriteLine("[SharedFixture] Shared ServiceBusClient disposed");
      }

      // Stop and dispose containers
      await Task.WhenAll(
        _postgresContainer.DisposeAsync().AsTask(),
        _mssqlContainer.DisposeAsync().AsTask(),
        _serviceBusContainer.DisposeAsync().AsTask(),
        _network.DisposeAsync().AsTask()
      );
    }
  }
}
