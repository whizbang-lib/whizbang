using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Generated;
using ECommerce.Integration.Tests.Fixtures;
using ECommerce.InventoryWorker.Generated;
using ECommerce.InventoryWorker.Lenses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres;

namespace ECommerce.InMemory.Integration.Tests.Fixtures;

/// <summary>
/// In-memory integration test fixture using InProcessTransport for fast, deterministic testing.
/// Uses PostgreSQL (Testcontainers) for real database but InProcessTransport for synchronous message delivery.
/// This isolates business logic testing from Azure Service Bus infrastructure concerns.
/// </summary>
/// <remarks>
/// <para><strong>Multi-Service Event Processing:</strong> InProcessTransport delivers events to ALL services
/// subscribing to the same address, mimicking Azure Service Bus topic fanout. Each service maintains independent
/// stream ownership in its own schema (inventory.wh_active_streams, bff.wh_active_streams), allowing both services
/// to process the same streams concurrently. This matches production behavior where each service has its own
/// Azure Service Bus subscription.</para>
///
/// <para><strong>Instance ID Isolation:</strong> Each service MUST have a unique instance ID. Shared instance IDs
/// cause the work coordinator to treat multiple services as a single instance, resulting in only one service
/// claiming and processing work while the other remains idle.</para>
/// </remarks>
public sealed class InMemoryIntegrationFixture : IAsyncDisposable {
  private readonly PostgreSqlContainer _postgresContainer;
  private readonly InProcessTransport _transport;  // Shared singleton across both hosts
  private bool _isInitialized;
  private IHost? _inventoryHost;
  private IHost? _bffHost;

  /// <summary>
  /// Unique instance ID for InventoryWorker service.
  /// CRITICAL: Must be different from BFF instance ID to ensure work coordinator treats them as separate instances.
  /// </summary>
  private readonly Guid _inventoryInstanceId = Guid.CreateVersion7();

  /// <summary>
  /// Unique instance ID for BFF service.
  /// CRITICAL: Must be different from InventoryWorker instance ID to ensure work coordinator treats them as separate instances.
  /// </summary>
  private readonly Guid _bffInstanceId = Guid.CreateVersion7();
  private readonly List<IServiceScope> _lensScopes = new(); // Track scopes for lens queries to dispose them properly

  public InMemoryIntegrationFixture() {
    _postgresContainer = new PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
      .WithDatabase("whizbang_integration_test")
      .WithUsername("whizbang_user")
      .WithPassword("whizbang_pass")
      .Build();

    // Create shared InProcessTransport (both hosts will use this same instance)
    _transport = new InProcessTransport();
  }

  /// <summary>
  /// Gets the IDispatcher instance for sending commands (from InventoryWorker host).
  /// The Dispatcher creates its own scope internally when publishing events.
  /// </summary>
  public IDispatcher Dispatcher => _inventoryHost?.Services.GetRequiredService<IDispatcher>()
    ?? throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the IProductLens instance for querying product catalog (from InventoryWorker host).
  /// Creates a new scope to ensure fresh DbContext and avoid stale cached data.
  /// </summary>
  public IProductLens InventoryProductLens {
    get {
      if (_inventoryHost == null) {
        throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");
      }
      var scope = _inventoryHost.Services.CreateScope();
      _lensScopes.Add(scope);  // Track for disposal
      return scope.ServiceProvider.GetRequiredService<IProductLens>();
    }
  }

  /// <summary>
  /// Gets the IInventoryLens instance for querying inventory levels (from InventoryWorker host).
  /// Creates a new scope to ensure fresh DbContext and avoid stale cached data.
  /// </summary>
  public IInventoryLens InventoryLens {
    get {
      if (_inventoryHost == null) {
        throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");
      }
      var scope = _inventoryHost.Services.CreateScope();
      _lensScopes.Add(scope);  // Track for disposal
      return scope.ServiceProvider.GetRequiredService<IInventoryLens>();
    }
  }

  /// <summary>
  /// Gets the IProductCatalogLens instance for querying product catalog (from BFF host).
  /// Creates a new scope to ensure fresh DbContext and avoid stale cached data.
  /// </summary>
  public IProductCatalogLens BffProductLens {
    get {
      if (_bffHost == null) {
        throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");
      }
      var scope = _bffHost.Services.CreateScope();
      _lensScopes.Add(scope);  // Track for disposal
      return scope.ServiceProvider.GetRequiredService<IProductCatalogLens>();
    }
  }

  /// <summary>
  /// Gets the IInventoryLevelsLens instance for querying inventory levels (from BFF host).
  /// Creates a new scope to ensure fresh DbContext and avoid stale cached data.
  /// </summary>
  public IInventoryLevelsLens BffInventoryLens {
    get {
      if (_bffHost == null) {
        throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync() first.");
      }
      var scope = _bffHost.Services.CreateScope();
      _lensScopes.Add(scope);  // Track for disposal
      return scope.ServiceProvider.GetRequiredService<IInventoryLevelsLens>();
    }
  }

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
  /// Initializes the test fixture: starts PostgreSQL, initializes transport, and starts service hosts.
  /// Much faster than Service Bus fixtures (~10s vs ~150s) due to synchronous in-memory transport.
  /// </summary>
  [RequiresDynamicCode("EF Core in tests may use dynamic code")]
  [RequiresUnreferencedCode("EF Core in tests may use unreferenced code")]
  public async Task InitializeAsync(CancellationToken cancellationToken = default) {
    if (_isInitialized) {
      return;
    }

    Console.WriteLine("[InMemoryFixture] Starting PostgreSQL container...");

    // Start PostgreSQL container
    await _postgresContainer.StartAsync(cancellationToken);

    Console.WriteLine("[InMemoryFixture] PostgreSQL started. Waiting for readiness...");

    // Get connection string
    var postgresConnection = _postgresContainer.GetConnectionString();

    // Wait for PostgreSQL to be ready to accept connections
    await _waitForPostgresReadyAsync(postgresConnection, cancellationToken);

    Console.WriteLine("[InMemoryFixture] PostgreSQL ready. Initializing InProcessTransport...");

    // Initialize transport (instant for in-process)
    await _transport.InitializeAsync(cancellationToken);

    Console.WriteLine("[InMemoryFixture] Transport initialized. Creating service hosts...");

    // Create service hosts (but don't start them yet)
    _inventoryHost = _createInventoryHost(postgresConnection);
    _bffHost = _createBffHost(postgresConnection);

    Console.WriteLine("[InMemoryFixture] Service hosts created. Initializing schema...");

    // Initialize PostgreSQL schema using EFCore DbContexts
    await _initializeSchemaAsync(cancellationToken);

    Console.WriteLine("[InMemoryFixture] Schema initialized. Seeding message associations...");

    // Seed message associations for perspective auto-checkpoint creation
    await _seedMessageAssociationsAsync(cancellationToken);

    Console.WriteLine("[InMemoryFixture] Message associations seeded. Starting service hosts...");

    // Start service hosts
    await Task.WhenAll(
      _inventoryHost.StartAsync(cancellationToken),
      _bffHost.StartAsync(cancellationToken)
    );

    Console.WriteLine("[InMemoryFixture] Service hosts started. Setting up transport subscriptions...");

    // CRITICAL FIX: Subscribe to transport topics to write incoming messages to inbox
    // Without this, published messages vanish and perspectives never materialize
    await _setupTransportSubscriptionsAsync(cancellationToken);

    Console.WriteLine("[InMemoryFixture] Transport subscriptions ready. Fixture initialized!");

    _isInitialized = true;
  }

  /// <summary>
  /// Creates the IHost for InventoryWorker with all required services and background workers.
  /// Uses InProcessTransport instead of AzureServiceBusTransport for in-memory message delivery.
  /// </summary>
  [RequiresUnreferencedCode("Calls Npgsql.NpgsqlDataSourceBuilder.EnableDynamicJson(Type[], Type[])")]
  [RequiresDynamicCode("Calls Npgsql.NpgsqlDataSourceBuilder.EnableDynamicJson(Type[], Type[])")]
  private IHost _createInventoryHost(string postgresConnection) {
    var builder = Host.CreateApplicationBuilder();

    // Register service instance provider with unique instance ID
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp => new TestServiceInstanceProvider(_inventoryInstanceId, "InventoryWorker"));

    // Register SHARED InProcessTransport (same instance used by both hosts)
    builder.Services.AddSingleton<ITransport>(_transport);

    // Add trace store for observability
    builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

    // Register OrderedStreamProcessor for message ordering
    builder.Services.AddSingleton<OrderedStreamProcessor>();

    // Register JsonSerializerOptions for Npgsql JSONB serialization
    var jsonOptions = ECommerce.Contracts.Generated.WhizbangJsonContext.CreateOptions();
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

    // DIAGNOSTIC: Verify IWorkCoordinatorStrategy is registered
    var strategyDescriptor = builder.Services.FirstOrDefault(sd => sd.ServiceType == typeof(IWorkCoordinatorStrategy));
    Console.WriteLine($"[InMemoryFixture] InventoryWorker IWorkCoordinatorStrategy registered: {strategyDescriptor != null} (Lifetime: {strategyDescriptor?.Lifetime})");

    // Register Whizbang generated services
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddReceptors(builder.Services);
    builder.Services.AddWhizbangAggregateIdExtractor();

    // Register lifecycle services for Perspective stage support
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddWhizbangLifecycleInvoker(builder.Services);
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddWhizbangLifecycleMessageDeserializer(builder.Services);
    builder.Services.AddSingleton<Whizbang.Core.Messaging.ILifecycleReceptorRegistry, Whizbang.Core.Messaging.DefaultLifecycleReceptorRegistry>();
    builder.Services.AddSingleton<Whizbang.Core.Messaging.IEventTypeProvider, ECommerce.Contracts.ECommerceEventTypeProvider>();

    // Register TopicRegistry to provide base topic names for events
    var topicRegistryInstance = new ECommerce.Contracts.Generated.TopicRegistry();
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRegistry>(topicRegistryInstance);

    // Register GenericTopicRoutingStrategy for test topic routing
    // This distributes events across generic topics (topic-00, topic-01) for Azure Service Bus emulator compatibility
    var routingStrategyInstance = new GenericTopicRoutingStrategy(topicCount: 2);
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRoutingStrategy>(routingStrategyInstance);

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
      options.DebugMode = true;  // DIAGNOSTIC: Enable checkpoint tracking
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;  // Require 2 empty polls to consider idle
    });

    // Register background workers
    builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();
    builder.Services.AddHostedService<PerspectiveWorker>();  // Processes perspective checkpoints

    // NOTE: No ServiceBusConsumerWorker - fixture handles message processing directly in _handleMessageForHostAsync
    // This processes inbox work (dispatches to handlers, stores events) synchronously when messages arrive

    return builder.Build();
  }

  /// <summary>
  /// Creates the IHost for BFF with all required services and background workers.
  /// Uses InProcessTransport instead of AzureServiceBusTransport for in-memory message delivery.
  /// </summary>
  [RequiresUnreferencedCode("Calls Npgsql.NpgsqlDataSourceBuilder.EnableDynamicJson(Type[], Type[])")]
  [RequiresDynamicCode("Calls Npgsql.NpgsqlDataSourceBuilder.EnableDynamicJson(Type[], Type[])")]
  private IHost _createBffHost(string postgresConnection) {
    var builder = Host.CreateApplicationBuilder();

    // Register service instance provider with unique instance ID
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp => new TestServiceInstanceProvider(_bffInstanceId, "BFF.API"));

    var jsonOptions = ECommerce.Contracts.Generated.WhizbangJsonContext.CreateOptions();

    // Register SHARED InProcessTransport (same instance used by both hosts)
    builder.Services.AddSingleton<ITransport>(_transport);

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

    // Register TopicRegistry to provide base topic names for events
    var topicRegistryInstance = new ECommerce.Contracts.Generated.TopicRegistry();
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRegistry>(topicRegistryInstance);

    // Register GenericTopicRoutingStrategy for test topic routing
    // This distributes events across generic topics (topic-00, topic-01) for Azure Service Bus emulator compatibility
    var routingStrategyInstance = new GenericTopicRoutingStrategy(topicCount: 2);
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRoutingStrategy>(routingStrategyInstance);

    // DIAGNOSTIC: Verify IWorkCoordinatorStrategy is registered
    var strategyDescriptor = builder.Services.FirstOrDefault(sd => sd.ServiceType == typeof(IWorkCoordinatorStrategy));
    Console.WriteLine($"[InMemoryFixture] BFF IWorkCoordinatorStrategy registered: {strategyDescriptor != null} (Lifetime: {strategyDescriptor?.Lifetime})");

    // Register SignalR (required by BFF lenses)
    builder.Services.AddSignalR();

    // Register perspective invoker for scoped event processing (use BFF's generated invoker)
    ECommerce.BFF.API.Generated.DispatcherRegistrations.AddWhizbangPerspectiveInvoker(builder.Services);

    // Register perspective runners for AOT-compatible lookup (replaces reflection)
    ECommerce.BFF.API.Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(builder.Services);

    // Register lifecycle services for Perspective stage support
    ECommerce.BFF.API.Generated.DispatcherRegistrations.AddWhizbangLifecycleInvoker(builder.Services);
    ECommerce.BFF.API.Generated.DispatcherRegistrations.AddWhizbangLifecycleMessageDeserializer(builder.Services);
    builder.Services.AddSingleton<Whizbang.Core.Messaging.ILifecycleReceptorRegistry, Whizbang.Core.Messaging.DefaultLifecycleReceptorRegistry>();
    builder.Services.AddSingleton<Whizbang.Core.Messaging.IEventTypeProvider, ECommerce.Contracts.ECommerceEventTypeProvider>();

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

    // Register InstantCompletionStrategy for immediate perspective completion reporting (test optimization)
    builder.Services.AddSingleton<IPerspectiveCompletionStrategy, InstantCompletionStrategy>();

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

    // NOTE: No ServiceBusConsumerWorker - fixture handles message processing directly in _handleMessageForHostAsync
    // This processes inbox work (dispatches to handlers, stores events) synchronously when messages arrive

    return builder.Build();
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
        Console.WriteLine($"[InMemoryFixture] PostgreSQL connection successful (attempt {attempt})");
        return;
      } catch (Exception ex) when (attempt < maxAttempts) {
        Console.WriteLine($"[InMemoryFixture] PostgreSQL not ready (attempt {attempt}): {ex.Message}");
        await Task.Delay(1000, cancellationToken);
      }
    }

    throw new TimeoutException($"PostgreSQL failed to accept connections after {maxAttempts} attempts");
  }

  /// <summary>
  /// Initializes the PostgreSQL schema: Whizbang core tables + perspective tables.
  /// Both services share the same database tables (no schema isolation).
  /// </summary>
  private async Task _initializeSchemaAsync(CancellationToken cancellationToken = default) {
    // CRITICAL: Initialize InventoryWorker schema (schema="inventory")
    using (var scope = _inventoryHost!.Services.CreateScope()) {
      var inventoryDbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();
      var logger = scope.ServiceProvider.GetRequiredService<ILogger<InMemoryIntegrationFixture>>();
      Console.WriteLine("[InMemoryFixture] Initializing InventoryWorker schema...");
      await ECommerce.InventoryWorker.Generated.InventoryDbContextSchemaExtensions.EnsureWhizbangDatabaseInitializedAsync(inventoryDbContext, logger, cancellationToken);
      Console.WriteLine("[InMemoryFixture] InventoryWorker schema (inventory) initialized");
    }

    // CRITICAL: Initialize BFF schema (schema="bff")
    // Each service has its own schema with its own tables, even though they share the same database
    using (var scope = _bffHost!.Services.CreateScope()) {
      var bffDbContext = scope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();
      var logger = scope.ServiceProvider.GetRequiredService<ILogger<InMemoryIntegrationFixture>>();
      Console.WriteLine("[InMemoryFixture] Initializing BFF schema...");
      await ECommerce.BFF.API.Generated.BffDbContextSchemaExtensions.EnsureWhizbangDatabaseInitializedAsync(bffDbContext, logger, cancellationToken);
      Console.WriteLine("[InMemoryFixture] BFF schema (bff) initialized");
    }

    Console.WriteLine("[InMemoryFixture] Both schemas initialized - database tables created");
  }

  /// <summary>
  /// Registers message associations for perspective auto-checkpoint creation.
  /// Maps event types to perspectives so process_work_batch can auto-create checkpoint rows.
  /// Uses generated RegisterPerspectiveAssociationsAsync from PerspectiveDiscoveryGenerator.
  /// CRITICAL: Each service has its own schema, so we must register associations using the correct DbContext.
  /// InventoryDbContext (schema="inventory") for InventoryWorker associations.
  /// BffDbContext (schema="bff") for BFF associations.
  /// </summary>
  private async Task _seedMessageAssociationsAsync(CancellationToken cancellationToken = default) {
    // CRITICAL: Register InventoryWorker associations using generated method (schema="inventory")
    using (var scope = _inventoryHost!.Services.CreateScope()) {
      var inventoryDbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();
      var logger = scope.ServiceProvider.GetRequiredService<ILogger<InMemoryIntegrationFixture>>();

      // Use generated RegisterPerspectiveAssociationsAsync from PerspectiveDiscoveryGenerator
      await ECommerce.InventoryWorker.Generated.PerspectiveRegistrationExtensions.RegisterPerspectiveAssociationsAsync(
        inventoryDbContext,
        schema: "inventory",
        serviceName: "ECommerce.InventoryWorker",
        logger: logger,
        cancellationToken: cancellationToken
      );

      Console.WriteLine("[InMemoryFixture] InventoryWorker message associations registered (inventory schema)");
    }

    // CRITICAL: Register BFF associations using generated method (schema="bff")
    using (var scope = _bffHost!.Services.CreateScope()) {
      var bffDbContext = scope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();
      var logger = scope.ServiceProvider.GetRequiredService<ILogger<InMemoryIntegrationFixture>>();

      // Use generated RegisterPerspectiveAssociationsAsync from PerspectiveDiscoveryGenerator
      await ECommerce.BFF.API.Generated.PerspectiveRegistrationExtensions.RegisterPerspectiveAssociationsAsync(
        bffDbContext,
        schema: "bff",
        serviceName: "ECommerce.BFF.API",
        logger: logger,
        cancellationToken: cancellationToken
      );

      Console.WriteLine("[InMemoryFixture] BFF message associations registered (bff schema)");
    }

    // DIAGNOSTIC: Query message associations from InventoryWorker schema to verify seeding
    using (var scope = _inventoryHost!.Services.CreateScope()) {
      var inventoryDbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();

      var inventoryAssociations = await inventoryDbContext.Database.SqlQueryRaw<string>(@"
        SELECT DISTINCT message_type FROM inventory.wh_message_associations WHERE association_type = 'perspective' LIMIT 20
      ").ToListAsync(cancellationToken);

      Console.WriteLine($"[InMemoryFixture] DIAGNOSTIC: InventoryWorker schema has {inventoryAssociations.Count} message associations:");
      foreach (var assoc in inventoryAssociations) {
        Console.WriteLine($"[InMemoryFixture]   - '{assoc}'");
      }
    }

    // DIAGNOSTIC: Query message associations from BFF schema to verify seeding
    using (var scope = _bffHost!.Services.CreateScope()) {
      var bffDbContext = scope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();

      var bffAssociations = await bffDbContext.Database.SqlQueryRaw<string>(@"
        SELECT DISTINCT message_type FROM bff.wh_message_associations WHERE association_type = 'perspective' LIMIT 20
      ").ToListAsync(cancellationToken);

      Console.WriteLine($"[InMemoryFixture] DIAGNOSTIC: BFF schema has {bffAssociations.Count} message associations:");
      foreach (var assoc in bffAssociations) {
        Console.WriteLine($"[InMemoryFixture]   - '{assoc}'");
      }
    }

    Console.WriteLine("[InMemoryFixture] Message associations seeded successfully for both schemas");
  }

  /// <summary>
  /// DIAGNOSTIC: Query event types and message associations after events are written.
  /// Helps identify naming mismatches between event_type and message_type columns.
  /// </summary>
  public async Task DumpEventTypesAndAssociationsAsync(CancellationToken cancellationToken = default) {
    using var scope = _inventoryHost!.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();

    // Query actual event types in event store
    var eventTypes = await dbContext.Database.SqlQueryRaw<string>(@"
      SELECT DISTINCT event_type FROM inventory.wh_event_store ORDER BY event_type LIMIT 20
    ").ToListAsync(cancellationToken);

    var output = new System.Text.StringBuilder();
    output.AppendLine($"[DIAGNOSTIC] Found {eventTypes.Count} distinct event types in wh_event_store:");
    foreach (var eventType in eventTypes) {
      output.AppendLine($"[DIAGNOSTIC]   event_type: '{eventType}'");
      Console.WriteLine($"[DIAGNOSTIC]   event_type: '{eventType}'");
    }

    // Query message associations
    var associations = await dbContext.Database.SqlQueryRaw<string>(@"
      SELECT DISTINCT message_type FROM inventory.wh_message_associations WHERE association_type = 'perspective' ORDER BY message_type LIMIT 20
    ").ToListAsync(cancellationToken);

    output.AppendLine($"[DIAGNOSTIC] Found {associations.Count} message_type values in wh_message_associations:");
    foreach (var assoc in associations) {
      output.AppendLine($"[DIAGNOSTIC]   message_type: '{assoc}'");
      Console.WriteLine($"[DIAGNOSTIC]   message_type: '{assoc}'");
    }

    // Query perspective checkpoints created
    var checkpointCount = await dbContext.Database.SqlQueryRaw<int>(@"
      SELECT COUNT(*)::int FROM inventory.wh_perspective_checkpoints
    ").FirstOrDefaultAsync(cancellationToken);

    output.AppendLine($"[DIAGNOSTIC] Found {checkpointCount} perspective checkpoints in wh_perspective_checkpoints");
    Console.WriteLine($"[DIAGNOSTIC] Found {checkpointCount} perspective checkpoints in wh_perspective_checkpoints");

    // Write to file for examination
    await System.IO.File.WriteAllTextAsync("/tmp/event-type-diagnostic.log", output.ToString(), cancellationToken);
  }

  /// <summary>
  /// Dumps detailed comparison of event types vs associations to detect mismatches.
  /// </summary>
  public async Task DumpTypeNameComparisonAsync(string schemaName = "inventory", CancellationToken cancellationToken = default) {
    using var scope = _inventoryHost!.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();

    Console.WriteLine($"=== TYPE NAME COMPARISON ({schemaName}) ===");

    // Query event types from wh_event_store
    var eventTypesQuery = $"SELECT DISTINCT event_type FROM {schemaName}.wh_event_store ORDER BY event_type";
    var eventTypes = await dbContext.Database.SqlQueryRaw<string>(eventTypesQuery).ToListAsync(cancellationToken);

    // Query message types from wh_message_associations (perspectives only)
    var associationsQuery = $"SELECT DISTINCT message_type FROM {schemaName}.wh_message_associations WHERE association_type = 'perspective' ORDER BY message_type";
    var associations = await dbContext.Database.SqlQueryRaw<string>(associationsQuery).ToListAsync(cancellationToken);

    Console.WriteLine($"Event Types in wh_event_store ({eventTypes.Count}):");
    foreach (var et in eventTypes) {
      Console.WriteLine($"  - {et}");
    }

    Console.WriteLine($"Message Types in wh_message_associations ({associations.Count}):");
    foreach (var mt in associations) {
      Console.WriteLine($"  - {mt}");
    }

    // Detect mismatches
    var mismatches = associations
      .Where(a => !eventTypes.Contains(a) && eventTypes.Any(e => e.Contains(a.Replace("global::", ""))))
      .ToList();

    if (mismatches.Any()) {
      Console.WriteLine("=== DETECTED TYPE NAME MISMATCHES ===");
      foreach (var mismatch in mismatches) {
        Console.WriteLine($"Association has 'global::' prefix: {mismatch}");
      }
    }

    Console.WriteLine("=== END TYPE NAME COMPARISON ===");
  }

  /// <summary>
  /// Creates a perspective completion waiter that registers receptors BEFORE sending commands.
  /// This avoids race conditions where perspectives complete before receptors are registered.
  /// </summary>
  /// <typeparam name="TEvent">The event type to wait for</typeparam>
  /// <param name="inventoryPerspectives">Number of perspectives expected in InventoryWorker host</param>
  /// <param name="bffPerspectives">Number of perspectives expected in BFF host</param>
  /// <returns>A waiter that can be used to wait for perspective completion</returns>
  /// <remarks>
  /// Usage:
  /// <code>
  /// // ProductCreatedEvent triggers 2 perspectives in each host
  /// using var waiter = fixture.CreatePerspectiveWaiter&lt;ProductCreatedEvent&gt;(
  ///   inventoryPerspectives: 2,
  ///   bffPerspectives: 2
  /// );
  /// await fixture.Dispatcher.SendAsync(command);
  /// await waiter.WaitAsync(timeout: 15000);
  /// </code>
  /// </remarks>
  /// <docs>testing/lifecycle-synchronization</docs>
  public PerspectiveCompletionWaiter<TEvent> CreatePerspectiveWaiter<TEvent>(
    int inventoryPerspectives,
    int bffPerspectives)
    where TEvent : IEvent {

    var inventoryRegistry = _inventoryHost!.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    var bffRegistry = _bffHost!.Services.GetRequiredService<ILifecycleReceptorRegistry>();

    return new PerspectiveCompletionWaiter<TEvent>(
      inventoryRegistry,
      bffRegistry,
      inventoryPerspectives,
      bffPerspectives
    );
  }

  /// <summary>
  /// Waits for perspective processing to complete using lifecycle receptors.
  /// This eliminates race conditions by using PostPerspectiveInline lifecycle stage.
  /// </summary>
  public async Task WaitForPerspectiveCompletionAsync<TEvent>(
    int inventoryPerspectives,
    int bffPerspectives,
    int timeoutMilliseconds = 15000)
    where TEvent : IEvent {

    var totalPerspectives = inventoryPerspectives + bffPerspectives;
    Console.WriteLine($"[WaitForPerspective] Waiting for {typeof(TEvent).Name} processing (Inventory={inventoryPerspectives}, BFF={bffPerspectives}, Total={totalPerspectives}, timeout={timeoutMilliseconds}ms)");

    var inventoryCompletionSource = new TaskCompletionSource<bool>();
    var inventoryCompletedPerspectives = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

    var bffCompletionSource = new TaskCompletionSource<bool>();
    var bffCompletedPerspectives = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

    var tasksToWait = new List<Task>();
    CountingPerspectiveReceptor<TEvent>? inventoryCountingReceptor = null;
    CountingPerspectiveReceptor<TEvent>? bffCountingReceptor = null;

    // Register receptor for InventoryHost if expecting perspectives
    if (inventoryPerspectives > 0) {
      inventoryCountingReceptor = new CountingPerspectiveReceptor<TEvent>(
        inventoryCompletionSource,
        inventoryCompletedPerspectives,
        inventoryPerspectives
      );

      var inventoryRegistry = _inventoryHost!.Services.GetRequiredService<ILifecycleReceptorRegistry>();
      inventoryRegistry.Register<TEvent>(inventoryCountingReceptor, LifecycleStage.PostPerspectiveInline);
      tasksToWait.Add(inventoryCompletionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds)));
    } else {
      inventoryCompletionSource.SetResult(true);  // No perspectives expected, mark as complete
    }

    // Register receptor for BFFHost if expecting perspectives
    if (bffPerspectives > 0) {
      bffCountingReceptor = new CountingPerspectiveReceptor<TEvent>(
        bffCompletionSource,
        bffCompletedPerspectives,
        bffPerspectives
      );

      var bffRegistry = _bffHost!.Services.GetRequiredService<ILifecycleReceptorRegistry>();
      bffRegistry.Register<TEvent>(bffCountingReceptor, LifecycleStage.PostPerspectiveInline);
      tasksToWait.Add(bffCompletionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds)));
    } else {
      bffCompletionSource.SetResult(true);  // No perspectives expected, mark as complete
    }

    try {
      // Wait for all expected perspectives to complete
      if (tasksToWait.Count > 0) {
        await Task.WhenAll(tasksToWait);
      }
      Console.WriteLine($"[WaitForPerspective] All {totalPerspectives} perspectives completed:");
      if (inventoryPerspectives > 0) {
        Console.WriteLine($"  Inventory ({inventoryPerspectives}): {string.Join(", ", inventoryCompletedPerspectives)}");
      }
      if (bffPerspectives > 0) {
        Console.WriteLine($"  BFF ({bffPerspectives}): {string.Join(", ", bffCompletedPerspectives)}");
      }
    } finally {
      // Unregister receptors
      if (inventoryCountingReceptor != null) {
        var inventoryRegistry = _inventoryHost!.Services.GetRequiredService<ILifecycleReceptorRegistry>();
        inventoryRegistry.Unregister<TEvent>(inventoryCountingReceptor, LifecycleStage.PostPerspectiveInline);
      }
      if (bffCountingReceptor != null) {
        var bffRegistry = _bffHost!.Services.GetRequiredService<ILifecycleReceptorRegistry>();
        bffRegistry.Unregister<TEvent>(bffCountingReceptor, LifecycleStage.PostPerspectiveInline);
      }
    }

    Console.WriteLine($"[WaitForPerspective] {typeof(TEvent).Name} processing complete!");
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
      // CRITICAL: Truncate BOTH inventory AND bff schemas since each has independent tables
      await dbContext.Database.ExecuteSqlRawAsync(@"
        DO $$
        BEGIN
          -- Truncate core infrastructure tables (INVENTORY schema)
          TRUNCATE TABLE inventory.wh_event_store, inventory.wh_outbox, inventory.wh_inbox, inventory.wh_perspective_checkpoints, inventory.wh_perspective_events, inventory.wh_receptor_processing, inventory.wh_active_streams, inventory.wh_message_deduplication CASCADE;

          -- Truncate all perspective tables (INVENTORY schema)
          TRUNCATE TABLE inventory.wh_per_inventory_level_dto CASCADE;
          TRUNCATE TABLE inventory.wh_per_order_read_model CASCADE;
          TRUNCATE TABLE inventory.wh_per_product_dto CASCADE;

          -- Truncate core infrastructure tables (BFF schema)
          TRUNCATE TABLE bff.wh_event_store, bff.wh_outbox, bff.wh_inbox, bff.wh_perspective_checkpoints, bff.wh_perspective_events, bff.wh_receptor_processing, bff.wh_active_streams, bff.wh_message_deduplication CASCADE;

          -- Truncate all perspective tables (BFF schema)
          TRUNCATE TABLE bff.wh_per_inventory_level_dto CASCADE;
          TRUNCATE TABLE bff.wh_per_order_read_model CASCADE;
          TRUNCATE TABLE bff.wh_per_product_dto CASCADE;
        EXCEPTION
          WHEN undefined_table THEN
            -- Tables don't exist, nothing to clean up
            NULL;
        END $$;
      ", cancellationToken);
    }
  }

  /// <summary>
  /// Sets up transport subscriptions to write incoming messages to inbox tables.
  /// This replaces ServiceBusConsumerWorker for InProcessTransport scenarios.
  /// Without subscriptions, published messages are delivered to nobody and perspectives never materialize.
  /// </summary>
  /// <remarks>
  /// CRITICAL: Both hosts use GenericTopicRoutingStrategy which routes events to topic-00/topic-01.
  /// Both hosts MUST subscribe to these generic topics to receive events.
  /// Each host has its own subscription name to simulate independent Service Bus subscriptions.
  /// </remarks>
  private async Task _setupTransportSubscriptionsAsync(CancellationToken cancellationToken) {
    // Subscribe InventoryWorker to generic topics (topic-00, topic-01)
    // CRITICAL: Must match GenericTopicRoutingStrategy which routes events to these topics
    await _transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await _handleMessageForHostAsync(_inventoryHost!, envelope, envelopeType, ct),
      new TransportDestination("topic-00", "inventory-worker"),
      cancellationToken
    );

    await _transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await _handleMessageForHostAsync(_inventoryHost!, envelope, envelopeType, ct),
      new TransportDestination("topic-01", "inventory-worker"),
      cancellationToken
    );

    Console.WriteLine("[InMemoryFixture] InventoryWorker subscribed to 'topic-00' and 'topic-01' (generic topics via routing strategy)");

    // Subscribe BFF to generic topics (topic-00, topic-01) with different subscription name
    // This simulates independent Service Bus subscriptions for each service
    await _transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await _handleMessageForHostAsync(_bffHost!, envelope, envelopeType, ct),
      new TransportDestination("topic-00", "bff-service"),
      cancellationToken
    );

    await _transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await _handleMessageForHostAsync(_bffHost!, envelope, envelopeType, ct),
      new TransportDestination("topic-01", "bff-service"),
      cancellationToken
    );

    Console.WriteLine("[InMemoryFixture] BFF subscribed to 'topic-00' and 'topic-01' (generic topics via routing strategy)");
  }

  /// <summary>
  /// Handles incoming messages from transport - full processing flow.
  /// CRITICAL: Must process inbox work (dispatch to handlers, store events) - not just write to inbox!
  /// This creates events which trigger perspective checkpoints for PerspectiveWorker to process.
  /// </summary>
  private async Task _handleMessageForHostAsync(IHost host, IMessageEnvelope envelope, string? envelopeType, CancellationToken ct) {
    try {
      // Validate envelope type is present
      if (string.IsNullOrWhiteSpace(envelopeType)) {
        throw new InvalidOperationException(
          $"EnvelopeType is required from transport but was null/empty. MessageId: {envelope.MessageId}. " +
          $"This indicates a bug in the transport layer - envelope type must be preserved during transmission.");
      }

      // Create scope to resolve scoped services
      await using var scope = host.Services.CreateAsyncScope();
      var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
      var jsonOptions = scope.ServiceProvider.GetRequiredService<JsonSerializerOptions>();
      var orderedProcessor = host.Services.GetRequiredService<OrderedStreamProcessor>();

      // 1. Serialize envelope to InboxMessage
      var newInboxMessage = _serializeToNewInboxMessage(envelope, envelopeType, jsonOptions);

      // 2. Queue for atomic deduplication via process_work_batch
      strategy.QueueInboxMessage(newInboxMessage);

      // 3. Flush - calls process_work_batch with atomic INSERT ... ON CONFLICT DO NOTHING
      var workBatch = await strategy.FlushAsync(WorkBatchFlags.None, ct);

      // 4. Check if work was returned - empty means duplicate (already processed)
      var myWork = workBatch.InboxWork.Where(w => w.MessageId == envelope.MessageId.Value).ToList();

      if (myWork.Count == 0) {
        Console.WriteLine($"[InMemoryFixture] Message {envelope.MessageId} already processed (duplicate)");
        return;
      }

      Console.WriteLine($"[InMemoryFixture] Message {envelope.MessageId} written to inbox for {host.Services.GetRequiredService<IServiceInstanceProvider>().ServiceName}");

      // 5. CRITICAL: Process inbox work - dispatch to handlers (InventoryWorker) or mark events (BFF)
      // process_work_batch automatically:
      // - Stores events from inbox (Phase 4.5B) if is_event=true
      // - Creates perspective events (Phase 4.6)
      // - Creates perspective checkpoints (Phase 4.7)
      await orderedProcessor.ProcessInboxWorkAsync(
        myWork,
        processor: async (work) => {
          // Deserialize event from work item
          var @event = _deserializeEvent(work, jsonOptions);

          // Mark as EventStored - process_work_batch will store it and create perspective checkpoints
          // The is_event flag (set in _serializeToNewInboxMessage) tells process_work_batch to store it
          if (@event is IEvent) {
            return MessageProcessingStatus.EventStored;
          }

          // Non-event messages - just mark as stored
          return MessageProcessingStatus.EventStored;
        },
        completionHandler: (msgId, status) => {
          strategy.QueueInboxCompletion(msgId, status);
        },
        failureHandler: (msgId, status, error) => {
          strategy.QueueInboxFailure(msgId, status, error);
        },
        ct
      );

      // 6. Report completions/failures back to database
      await strategy.FlushAsync(WorkBatchFlags.None, ct);

      Console.WriteLine($"[InMemoryFixture] Successfully processed message {envelope.MessageId}");
    } catch (Exception ex) {
      Console.WriteLine($"[InMemoryFixture] Error processing message {envelope.MessageId}: {ex.Message}");
      throw;
    }
  }

  /// <summary>
  /// Extracts the message type name from an envelope type name.
  /// Example: "MessageEnvelope`1[[MyApp.ProductCreatedEvent, MyApp]], Whizbang.Core"
  /// Returns: "MyApp.ProductCreatedEvent, MyApp"
  /// </summary>
  private static string _extractMessageTypeFromEnvelopeType(string envelopeTypeName) {
    var startIndex = envelopeTypeName.IndexOf("[[", StringComparison.Ordinal);
    var endIndex = envelopeTypeName.IndexOf("]]", StringComparison.Ordinal);

    if (startIndex == -1 || endIndex == -1 || startIndex >= endIndex) {
      throw new InvalidOperationException(
        $"Invalid envelope type name format: '{envelopeTypeName}'. " +
        $"Expected format: 'MessageEnvelope`1[[MessageType, Assembly]], EnvelopeAssembly'");
    }

    var messageTypeName = envelopeTypeName.Substring(startIndex + 2, endIndex - startIndex - 2);

    if (string.IsNullOrWhiteSpace(messageTypeName)) {
      throw new InvalidOperationException(
        $"Failed to extract message type name from envelope type: '{envelopeTypeName}'");
    }

    return messageTypeName;
  }

  /// <summary>
  /// Serializes IMessageEnvelope to InboxMessage (same logic as ServiceBusConsumerWorker._serializeToNewInboxMessage).
  /// </summary>
  private static InboxMessage _serializeToNewInboxMessage(IMessageEnvelope envelope, string envelopeType, JsonSerializerOptions jsonOptions) {
    // Extract message type from envelope type string
    // This is CRITICAL - envelope.Payload.GetType() returns JsonElement after JSON serialization
    // We must use the envelopeType parameter which contains the original type info
    var messageTypeName = _extractMessageTypeFromEnvelopeType(envelopeType);

    // Determine if message is an event by checking the type string suffix
    // CRITICAL: Cannot use (payload is IEvent) because payload is JsonElement from InProcessTransport
    // Must check if the type name ends with "Event" (convention-based)
    var typeNameWithoutAssembly = messageTypeName.Split(',')[0].Trim();  // "ECommerce.Contracts.ProductCreatedEvent"
    var lastSegment = typeNameWithoutAssembly.Split('.').Last();  // "ProductCreatedEvent"
    var isEvent = lastSegment.EndsWith("Event", StringComparison.Ordinal);

    // Extract simple type name for handler name (last part after last '.')
    var lastDotIndex = messageTypeName.LastIndexOf('.');
    var simpleTypeName = lastDotIndex >= 0
      ? messageTypeName.Substring(lastDotIndex + 1).Split(',')[0].Trim()
      : messageTypeName.Split(',')[0].Trim();
    var handlerName = simpleTypeName + "Handler";

    var streamId = _extractStreamId(envelope);

    // Serialize envelope to JSON for storage
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);

    // Deserialize as MessageEnvelope<JsonElement>
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)
      ?? throw new InvalidOperationException($"Failed to deserialize envelope as MessageEnvelope<JsonElement> for message {envelope.MessageId}");

    return new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = handlerName,
      Envelope = jsonEnvelope,
      EnvelopeType = envelopeType,
      StreamId = streamId,
      IsEvent = isEvent,
      MessageType = messageTypeName  // ← Use extracted type, NOT payload.GetType()
    };
  }

  /// <summary>
  /// Deserializes event payload from InboxWork (same logic as ServiceBusConsumerWorker._deserializeEvent).
  /// </summary>
  private static object? _deserializeEvent(InboxWork work, JsonSerializerOptions jsonOptions) {
    try {
      var jsonElement = work.Envelope.Payload;
      var messageType = Type.GetType(work.MessageType);
      if (messageType == null) {
        return null;
      }

      return JsonSerializer.Deserialize(jsonElement, messageType, jsonOptions);
    } catch {
      return null;
    }
  }

  /// <summary>
  /// Extracts stream_id from envelope for stream-based ordering (same logic as ServiceBusConsumerWorker._extractStreamId).
  /// </summary>
  private static Guid _extractStreamId(IMessageEnvelope envelope) {
    var firstHop = envelope.Hops.FirstOrDefault();
    if (firstHop?.Metadata != null && firstHop.Metadata.TryGetValue("AggregateId", out var aggregateIdElem)) {
      if (aggregateIdElem.ValueKind == JsonValueKind.String) {
        var aggregateIdStr = aggregateIdElem.GetString();
        if (aggregateIdStr != null && Guid.TryParse(aggregateIdStr, out var parsedAggregateId)) {
          return parsedAggregateId;
        }
      }
    }

    return envelope.MessageId.Value;
  }

  public async ValueTask DisposeAsync() {
    if (_isInitialized) {
      // Dispose all lens scopes first
      foreach (var scope in _lensScopes) {
        scope.Dispose();
      }
      _lensScopes.Clear();

      // Stop hosts
      if (_inventoryHost != null) {
        await _inventoryHost.StopAsync();
        _inventoryHost.Dispose();
      }

      if (_bffHost != null) {
        await _bffHost.StopAsync();
        _bffHost.Dispose();
      }

      // Stop and dispose PostgreSQL container
      await _postgresContainer.DisposeAsync();
    }
  }
}
