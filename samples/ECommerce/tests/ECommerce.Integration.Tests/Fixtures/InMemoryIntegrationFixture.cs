using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Generated;
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

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// In-memory integration test fixture using InProcessTransport for fast, deterministic testing.
/// Uses PostgreSQL (Testcontainers) for real database but InProcessTransport for synchronous message delivery.
/// This isolates business logic testing from Azure Service Bus infrastructure concerns.
/// </summary>
public sealed class InMemoryIntegrationFixture : IAsyncDisposable {
  private readonly PostgreSqlContainer _postgresContainer;
  private readonly InProcessTransport _transport;  // Shared singleton across both hosts
  private bool _isInitialized;
  private IHost? _inventoryHost;
  private IHost? _bffHost;
  private readonly Guid _sharedInstanceId = Guid.CreateVersion7(); // Shared across both services for partition claiming

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

    // Register service instance provider (uses shared instance ID for partition claiming compatibility)
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp => new TestServiceInstanceProvider(_sharedInstanceId, "InventoryWorker"));

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

    // NOTE: No ServiceBusConsumerWorker needed - InProcessTransport delivers messages synchronously via subscriptions

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

    // Register service instance provider (uses shared instance ID for partition claiming compatibility)
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp => new TestServiceInstanceProvider(_sharedInstanceId, "BFF.API"));

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

    // NOTE: No ServiceBusConsumerWorker needed - InProcessTransport delivers messages synchronously via subscriptions

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
  /// Seeds message associations for perspective auto-checkpoint creation.
  /// Maps event types to perspectives so process_work_batch can auto-create checkpoint rows.
  /// PHASE 3 (Option A): Manual seeding - will be replaced by generator automation in Phase 3 (Option B).
  /// </summary>
  private async Task _seedMessageAssociationsAsync(CancellationToken cancellationToken = default) {
    using var scope = _inventoryHost!.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();

    // Seed associations for InventoryWorker.ProductCatalogPerspective
    // Handles: ProductCreatedEvent, ProductUpdatedEvent, ProductDeletedEvent
    await dbContext.Database.ExecuteSqlRawAsync(@"
      INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
      VALUES
        ('ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts', 'perspective', 'ProductCatalogPerspective', 'ECommerce.InventoryWorker', NOW(), NOW()),
        ('ECommerce.Contracts.Events.ProductUpdatedEvent, ECommerce.Contracts', 'perspective', 'ProductCatalogPerspective', 'ECommerce.InventoryWorker', NOW(), NOW()),
        ('ECommerce.Contracts.Events.ProductDeletedEvent, ECommerce.Contracts', 'perspective', 'ProductCatalogPerspective', 'ECommerce.InventoryWorker', NOW(), NOW())
      ON CONFLICT (message_type, association_type, target_name, service_name) DO NOTHING
    ", cancellationToken);

    // Seed associations for InventoryWorker.InventoryLevelsPerspective
    // Handles: ProductCreatedEvent (initial), InventoryRestockedEvent, InventoryReservedEvent, InventoryAdjustedEvent
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
    // Handles: ProductCreatedEvent, ProductUpdatedEvent, ProductDeletedEvent
    await dbContext.Database.ExecuteSqlRawAsync(@"
      INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
      VALUES
        ('ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts', 'perspective', 'ProductCatalogPerspective', 'ECommerce.BFF.API', NOW(), NOW()),
        ('ECommerce.Contracts.Events.ProductUpdatedEvent, ECommerce.Contracts', 'perspective', 'ProductCatalogPerspective', 'ECommerce.BFF.API', NOW(), NOW()),
        ('ECommerce.Contracts.Events.ProductDeletedEvent, ECommerce.Contracts', 'perspective', 'ProductCatalogPerspective', 'ECommerce.BFF.API', NOW(), NOW())
      ON CONFLICT (message_type, association_type, target_name, service_name) DO NOTHING
    ", cancellationToken);

    // Seed associations for BFF.InventoryLevelsPerspective
    // Handles: ProductCreatedEvent (initial), InventoryRestockedEvent, InventoryReservedEvent, InventoryAdjustedEvent
    await dbContext.Database.ExecuteSqlRawAsync(@"
      INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
      VALUES
        ('ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts', 'perspective', 'InventoryLevelsPerspective', 'ECommerce.BFF.API', NOW(), NOW()),
        ('ECommerce.Contracts.Events.InventoryRestockedEvent, ECommerce.Contracts', 'perspective', 'InventoryLevelsPerspective', 'ECommerce.BFF.API', NOW(), NOW()),
        ('ECommerce.Contracts.Events.InventoryReservedEvent, ECommerce.Contracts', 'perspective', 'InventoryLevelsPerspective', 'ECommerce.BFF.API', NOW(), NOW()),
        ('ECommerce.Contracts.Events.InventoryAdjustedEvent, ECommerce.Contracts', 'perspective', 'InventoryLevelsPerspective', 'ECommerce.BFF.API', NOW(), NOW())
      ON CONFLICT (message_type, association_type, target_name, service_name) DO NOTHING
    ", cancellationToken);

    // DIAGNOSTIC: Query actual event types in event store to verify naming
    var eventTypes = await dbContext.Database.SqlQueryRaw<string>(@"
      SELECT DISTINCT event_type FROM wh_event_store LIMIT 10
    ").ToListAsync(cancellationToken);

    Console.WriteLine($"[InMemoryFixture] DIAGNOSTIC: Found {eventTypes.Count} distinct event types in wh_event_store:");
    foreach (var eventType in eventTypes) {
      Console.WriteLine($"[InMemoryFixture]   - '{eventType}'");
    }

    // DIAGNOSTIC: Query message associations to verify what we seeded
    var associations = await dbContext.Database.SqlQueryRaw<string>(@"
      SELECT DISTINCT message_type FROM wh_message_associations WHERE association_type = 'perspective' LIMIT 20
    ").ToListAsync(cancellationToken);

    Console.WriteLine($"[InMemoryFixture] DIAGNOSTIC: Found {associations.Count} message_type values in wh_message_associations:");
    foreach (var assoc in associations) {
      Console.WriteLine($"[InMemoryFixture]   - '{assoc}'");
    }

    Console.WriteLine("[InMemoryFixture] Message associations seeded successfully");
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
      SELECT DISTINCT event_type FROM wh_event_store ORDER BY event_type LIMIT 20
    ").ToListAsync(cancellationToken);

    var output = new System.Text.StringBuilder();
    output.AppendLine($"[DIAGNOSTIC] Found {eventTypes.Count} distinct event types in wh_event_store:");
    foreach (var eventType in eventTypes) {
      output.AppendLine($"[DIAGNOSTIC]   event_type: '{eventType}'");
      Console.WriteLine($"[DIAGNOSTIC]   event_type: '{eventType}'");
    }

    // Query message associations
    var associations = await dbContext.Database.SqlQueryRaw<string>(@"
      SELECT DISTINCT message_type FROM wh_message_associations WHERE association_type = 'perspective' ORDER BY message_type LIMIT 20
    ").ToListAsync(cancellationToken);

    output.AppendLine($"[DIAGNOSTIC] Found {associations.Count} message_type values in wh_message_associations:");
    foreach (var assoc in associations) {
      output.AppendLine($"[DIAGNOSTIC]   message_type: '{assoc}'");
      Console.WriteLine($"[DIAGNOSTIC]   message_type: '{assoc}'");
    }

    // Query perspective checkpoints created
    var checkpointCount = await dbContext.Database.SqlQueryRaw<int>(@"
      SELECT COUNT(*)::int FROM wh_perspective_checkpoints
    ").FirstOrDefaultAsync(cancellationToken);

    output.AppendLine($"[DIAGNOSTIC] Found {checkpointCount} perspective checkpoints in wh_perspective_checkpoints");
    Console.WriteLine($"[DIAGNOSTIC] Found {checkpointCount} perspective checkpoints in wh_perspective_checkpoints");

    // Write to file for examination
    await System.IO.File.WriteAllTextAsync("/tmp/event-type-diagnostic.log", output.ToString(), cancellationToken);
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

      Console.WriteLine("[InMemoryFixture] Event processing idle - all workers have no pending work (2 publishers + 2 perspective workers)");
    } catch (OperationCanceledException) {
      Console.WriteLine($"[InMemoryFixture] WARNING: Event processing did not reach idle state within {timeoutMilliseconds}ms timeout");
      Console.WriteLine($"[InMemoryFixture] InventoryWorker Publisher idle: {inventoryPublisher?.IsIdle ?? true}, PerspectiveWorker idle: {inventoryPerspectiveWorker?.IsIdle ?? true}");
      Console.WriteLine($"[InMemoryFixture] BFF Publisher idle: {bffPublisher?.IsIdle ?? true}, PerspectiveWorker idle: {bffPerspectiveWorker?.IsIdle ?? true}");
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

  /// <summary>
  /// Sets up transport subscriptions to write incoming messages to inbox tables.
  /// This replaces ServiceBusConsumerWorker for InProcessTransport scenarios.
  /// Without subscriptions, published messages are delivered to nobody and perspectives never materialize.
  /// </summary>
  private async Task _setupTransportSubscriptionsAsync(CancellationToken cancellationToken) {
    // Subscribe InventoryWorker to "products" and "inventory" topics
    await _transport.SubscribeAsync(
      async (envelope, ct) => await _handleMessageForHostAsync(_inventoryHost!, envelope, ct),
      new TransportDestination("products", "inventory-worker"),
      cancellationToken
    );

    await _transport.SubscribeAsync(
      async (envelope, ct) => await _handleMessageForHostAsync(_inventoryHost!, envelope, ct),
      new TransportDestination("inventory", "inventory-worker"),
      cancellationToken
    );

    Console.WriteLine("[InMemoryFixture] InventoryWorker subscribed to 'products' and 'inventory' topics");

    // Subscribe BFF to "products" and "inventory" topics
    await _transport.SubscribeAsync(
      async (envelope, ct) => await _handleMessageForHostAsync(_bffHost!, envelope, ct),
      new TransportDestination("products", "bff-service"),
      cancellationToken
    );

    await _transport.SubscribeAsync(
      async (envelope, ct) => await _handleMessageForHostAsync(_bffHost!, envelope, ct),
      new TransportDestination("inventory", "bff-service"),
      cancellationToken
    );

    Console.WriteLine("[InMemoryFixture] BFF subscribed to 'products' and 'inventory' topics");
  }

  /// <summary>
  /// Handles incoming messages from transport and writes them to inbox (simplified for InProcessTransport).
  /// Just writes to inbox table and lets PerspectiveWorker poll and process naturally.
  /// </summary>
  private async Task _handleMessageForHostAsync(IHost host, IMessageEnvelope envelope, CancellationToken ct) {
    try {
      // Create scope to resolve scoped services
      await using var scope = host.Services.CreateAsyncScope();
      var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
      var jsonOptions = scope.ServiceProvider.GetRequiredService<JsonSerializerOptions>();

      // 1. Serialize envelope to InboxMessage
      var newInboxMessage = _serializeToNewInboxMessage(envelope, jsonOptions);

      // 2. Queue for atomic deduplication via process_work_batch
      strategy.QueueInboxMessage(newInboxMessage);

      // 3. Flush - calls process_work_batch with atomic INSERT ... ON CONFLICT DO NOTHING
      // This writes the message to inbox table, and PerspectiveWorker will poll and process it
      await strategy.FlushAsync(WorkBatchFlags.None, ct);

      Console.WriteLine($"[InMemoryFixture] Message {envelope.MessageId} written to inbox for {host.Services.GetRequiredService<IServiceInstanceProvider>().ServiceName}");
    } catch (Exception ex) {
      Console.WriteLine($"[InMemoryFixture] Error processing message {envelope.MessageId}: {ex.Message}");
      throw;
    }
  }

  /// <summary>
  /// Serializes IMessageEnvelope to InboxMessage (same logic as ServiceBusConsumerWorker._serializeToNewInboxMessage).
  /// </summary>
  private static InboxMessage _serializeToNewInboxMessage(IMessageEnvelope envelope, JsonSerializerOptions jsonOptions) {
    var payload = envelope.Payload;
    var payloadType = payload.GetType();
    var handlerName = payloadType.Name + "Handler";
    var streamId = _extractStreamId(envelope);

    // Use short form: "TypeName, AssemblyName" (NOT AssemblyQualifiedName which includes Version/Culture/PublicKeyToken)
    // This matches the format expected by wh_message_associations and used in process_work_batch SQL JOIN
    var messageTypeName = $"{payloadType.FullName}, {payloadType.Assembly.GetName().Name}";

    var envelopeTypeName = envelope.GetType().AssemblyQualifiedName
      ?? throw new InvalidOperationException($"Envelope type {envelope.GetType().Name} must have an assembly-qualified name");

    // Serialize envelope to JSON and deserialize as MessageEnvelope<JsonElement>
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)
      ?? throw new InvalidOperationException($"Failed to deserialize envelope as MessageEnvelope<JsonElement> for message {envelope.MessageId}");

    return new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = handlerName,
      Envelope = jsonEnvelope,
      EnvelopeType = envelopeTypeName,
      StreamId = streamId,
      IsEvent = payload is IEvent,
      MessageType = messageTypeName
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
