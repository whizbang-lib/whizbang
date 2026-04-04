using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ECommerce.BFF.API.Generated;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Generated;
using ECommerce.InventoryWorker.Generated;
using ECommerce.InventoryWorker.Lenses;
using Medo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres;
using Whizbang.Hosting.RabbitMQ;
using Whizbang.Testing.Lifecycle;
using Whizbang.Transports.RabbitMQ;

namespace ECommerce.RabbitMQ.Integration.Tests.Fixtures;

/// <summary>
/// Per-test integration fixture for RabbitMQ tests.
/// Creates test hosts (Inventory, BFF) with RabbitMQ transport and unique topology per test.
/// </summary>
public sealed class RabbitMqIntegrationFixture : IAsyncDisposable {
  private readonly string _rabbitMqConnection;
  private readonly string _inventoryPostgresConnection;
  private readonly string _bffPostgresConnection;
  private readonly Uri _managementApiUri;
  private readonly string _testId;
  private readonly HttpClient _managementClient;

  private IHost? _inventoryHost;
  private IHost? _bffHost;
  private IServiceScope? _inventoryScope;
  private IServiceScope? _bffScope;

  /// <summary>
  /// Gets the IDispatcher instance for sending commands (from InventoryWorker host).
  /// </summary>
  public IDispatcher Dispatcher => _inventoryHost?.Services.GetRequiredService<IDispatcher>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the InventoryWorker host for accessing services (used in lifecycle tests).
  /// </summary>
  public IHost InventoryHost => _inventoryHost
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the BFF host for accessing services (used in lifecycle tests).
  /// </summary>
  public IHost BffHost => _bffHost
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the IProductLens instance for querying product catalog (from InventoryWorker host).
  /// Uses a long-lived scope that is recreated when RefreshLensScopes() is called.
  /// </summary>
  public IProductLens InventoryProductLens => _inventoryScope?.ServiceProvider.GetRequiredService<IProductLens>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the IInventoryLens instance for querying inventory levels (from InventoryWorker host).
  /// Uses a long-lived scope that is recreated when RefreshLensScopes() is called.
  /// </summary>
  public IInventoryLens InventoryLens => _inventoryScope?.ServiceProvider.GetRequiredService<IInventoryLens>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the IProductCatalogLens instance for querying product catalog (from BFF host).
  /// Uses a long-lived scope that is recreated when RefreshLensScopes() is called.
  /// </summary>
  public IProductCatalogLens BffProductLens => _bffScope?.ServiceProvider.GetRequiredService<IProductCatalogLens>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the IInventoryLevelsLens instance for querying inventory levels (from BFF host).
  /// Uses a long-lived scope that is recreated when RefreshLensScopes() is called.
  /// </summary>
  public IInventoryLevelsLens BffInventoryLens => _bffScope?.ServiceProvider.GetRequiredService<IInventoryLevelsLens>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Refreshes lens scopes to ensure queries see the latest committed data.
  /// Call this after commands complete and before querying perspectives.
  /// This disposes old DbContexts and creates fresh ones with current database state.
  /// IMPORTANT: Call this immediately after WaitAsync() returns - no delays needed!
  /// PostPerspectiveInline lifecycle stage ensures data is committed before receptor fires.
  /// </summary>
  public void RefreshLensScopes() {
    // Dispose old scopes
    _inventoryScope?.Dispose();
    _bffScope?.Dispose();

    // Create fresh scopes with new DbContexts
    if (_inventoryHost != null) {
      _inventoryScope = _inventoryHost.Services.CreateScope();
    }
    if (_bffHost != null) {
      _bffScope = _bffHost.Services.CreateScope();
    }
  }

  /// <summary>
  /// Gets the Inventory PostgreSQL connection string for direct database operations.
  /// </summary>
  public string InventoryConnectionString => _inventoryPostgresConnection
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the BFF PostgreSQL connection string for direct database operations.
  /// </summary>
  public string BffConnectionString => _bffPostgresConnection
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets a logger instance for use in test scenarios.
  /// </summary>
  public ILogger<T> GetLogger<T>() {
    return _inventoryHost?.Services.GetRequiredService<ILogger<T>>()
      ?? throw new InvalidOperationException("Fixture not initialized");
  }

  public RabbitMqIntegrationFixture(
    string rabbitMqConnection,
    string inventoryPostgresConnection,
    string bffPostgresConnection,
    Uri managementApiUri,
    string testId
  ) {
    _rabbitMqConnection = rabbitMqConnection;
    _inventoryPostgresConnection = inventoryPostgresConnection;
    _bffPostgresConnection = bffPostgresConnection;
    _managementApiUri = managementApiUri;
    _testId = testId;

    // Setup Management API client for cleanup
    _managementClient = new HttpClient { BaseAddress = managementApiUri };
    _managementClient.DefaultRequestHeaders.Authorization =
      new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
        Encoding.ASCII.GetBytes("guest:guest")));
  }

  /// <summary>
  /// Initializes database schemas and starts test hosts.
  /// </summary>
  public async Task InitializeAsync(CancellationToken ct = default) {
    Console.WriteLine($"[RabbitMqFixture] InitializeAsync START (testId={_testId})");

    // Create hosts
    Console.WriteLine("[RabbitMqFixture] Creating InventoryWorker host...");
    _inventoryHost = _createInventoryHost();
    Console.WriteLine("[RabbitMqFixture] InventoryWorker host created");

    Console.WriteLine("[RabbitMqFixture] Creating BFF host...");
    _bffHost = _createBffHost();
    Console.WriteLine("[RabbitMqFixture] BFF host created");

    // Initialize database schemas
    Console.WriteLine("[RabbitMqFixture] Initializing database schemas...");
    await _initializeDatabaseSchemasAsync(ct);
    Console.WriteLine("[RabbitMqFixture] Database schemas initialized");

    // Start hosts AFTER schema is ready
    Console.WriteLine("[RabbitMqFixture] Starting service hosts...");
    await _inventoryHost.StartAsync(ct);
    Console.WriteLine("[RabbitMqFixture] InventoryWorker host started");
    await _bffHost.StartAsync(ct);
    Console.WriteLine("[RabbitMqFixture] BFF host started");

    // Wait for workers to complete their first polling cycle (ready to process)
    // Uses completion signals instead of Task.Delay — deterministic and doesn't waste time
    Console.WriteLine("[RabbitMqFixture] Waiting for workers to become ready...");
    await _waitForWorkersReadyAsync(ct);
    Console.WriteLine("[RabbitMqFixture] Workers ready");

    // Create long-lived scopes for lenses
    Console.WriteLine("[RabbitMqFixture] Creating long-lived scopes for lenses...");
    _inventoryScope = _inventoryHost.Services.CreateScope();
    _bffScope = _bffHost.Services.CreateScope();
    Console.WriteLine("[RabbitMqFixture] Scopes created");

    Console.WriteLine("[RabbitMqFixture] InitializeAsync COMPLETE - Ready for test execution!");
  }

  /// <summary>
  /// Cleans up test-specific queues and exchanges via Management API.
  /// </summary>
  public async Task CleanupTestAsync(string testName, CancellationToken ct = default) {
    string testId = new TestRabbitMqRoutingStrategy(_testId).GenerateTestId(testName);

    // Delete test-specific queues and exchanges via Management API
    // Note: These are placeholders - actual cleanup would need to know exact queue/exchange names
    await _deleteQueueAsync($"bff-{testId}", ct);
    await _deleteQueueAsync($"inventory-{testId}", ct);
    await _deleteExchangeAsync($"products-{testId}", ct);
  }

  /// <summary>
  /// Cleans up all test data from the database between tests.
  /// Truncates Whizbang infrastructure tables and perspective tables.
  /// Used by shared fixture pattern to reset state between sequential tests.
  /// </summary>
  public async Task CleanupDatabaseAsync(CancellationToken cancellationToken = default) {
    // Purge RabbitMQ queues to prevent stale messages from previous tests
    await _deleteQueueAsync($"bff-products-queue-{_testId}");
    await _deleteQueueAsync($"inventory-products-queue-{_testId}");
    await _deleteQueueAsync($"bff-inventory-queue-{_testId}");

    // Delete all data from Whizbang infrastructure tables in the inventory schema.
    // Uses DELETE instead of TRUNCATE to avoid ACCESS EXCLUSIVE locks that deadlock
    // with the shared fixture's running workers (PerspectiveWorker, OutboxWorker).
    const int maxRetries = 5;
    const int retryDelayMs = 200;
    for (var attempt = 1; attempt <= maxRetries; attempt++) {
      try {
        await using var connection = new Npgsql.NpgsqlConnection(_inventoryPostgresConnection);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
          DO $$
          BEGIN
            EXECUTE (
              SELECT string_agg('DELETE FROM ' || schemaname || '.' || tablename, '; ')
              FROM pg_tables
              WHERE schemaname = 'inventory'
                AND tablename LIKE 'wh_%'
            );
          END $$;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        break;
      } catch (Npgsql.PostgresException ex) when (ex.SqlState == "40P01" && attempt < maxRetries) {
        // Deadlock — retry with backoff
        await Task.Delay(retryDelayMs * attempt, cancellationToken);
      }
    }

    Console.WriteLine("[RabbitMqFixture] Database cleaned up between tests");
  }

  private IHost _createInventoryHost() {
    var builder = Host.CreateApplicationBuilder();

    // Add connection string to configuration for generated turnkey extensions
    // The generated code derives "inventory-db" from "InventoryDbContext"
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:inventory-db"] = _inventoryPostgresConnection
    });

    // Register service instance provider (unique instance ID per test)
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp =>
      new TestServiceInstanceProvider(Uuid7.NewUuid7().ToGuid(), "InventoryWorker"));

    // IMPORTANT: Explicitly call module initializers for test assemblies (may not run automatically)
    ECommerce.InventoryWorker.Generated.GeneratedModelRegistration.Initialize();
    ECommerce.Contracts.Generated.WhizbangIdConverterInitializer.Initialize();

    // Create JsonSerializerOptions from global registry using JsonContextRegistry.CreateCombinedOptions()
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    builder.Services.AddSingleton(jsonOptions);

    // Register RabbitMQ transport
    builder.Services.AddRabbitMQTransport(_rabbitMqConnection);

    // Register routing strategy (maps to test-specific exchanges)
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRoutingStrategy>(
      new TestRabbitMqRoutingStrategy(_testId));

    // Turnkey registration (via .WithEFCore<T>().WithDriver.Postgres below) handles:
    // - NpgsqlDataSource creation with ConfigureJsonOptions + EnableDynamicJson
    // - AddDbContext<InventoryDbContext> with UseNpgsql
    // - IDbContextFactory<InventoryDbContext> singleton registration
    // Connection string is provided via config ("ConnectionStrings:inventory-db" above)

    // CRITICAL: Register IDatabaseReadinessCheck that always returns true
    // The fixture ensures the database schema is created before starting hosts,
    // and PostgresDatabaseReadinessCheck checks for tables in 'public' schema but we use named schemas.
    builder.Services.AddSingleton<IDatabaseReadinessCheck>(sp => new DefaultDatabaseReadinessCheck());

    // IMPORTANT: Explicitly call module initializers for test assemblies (may not run automatically)
    ECommerce.InventoryWorker.Generated.GeneratedModelRegistration.Initialize();
    ECommerce.Contracts.Generated.WhizbangIdConverterInitializer.Initialize();

    // CRITICAL: Clear the global Dispatcher callback before calling AddWhizbang().
    // The ECommerce.Integration.TestUtilities assembly has a module initializer that overwrites
    // ServiceRegistrationCallbacks.Dispatcher with its own callback (which registers
    // DistributeStageTestReceptor). That receptor requires TaskCompletionSource<ProductCreatedEvent>
    // in its constructor, which is not registered in DI, causing a build failure.
    // Since we explicitly call AddReceptors() and AddWhizbangDispatcher() below,
    // the auto-registration callback is not needed.
    ServiceRegistrationCallbacks.Dispatcher = null;

    // Register Whizbang with EFCore infrastructure
    _ = builder.Services
      .AddWhizbang()
      .WithEFCore<ECommerce.InventoryWorker.InventoryDbContext>()
      .WithDriver.Postgres;

    // Use Global scope for integration tests (no tenant filtering needed)
    // Without this, lens queries default to Tenant scope which requires IScopeContextAccessor.Current
    // to be set by middleware — but test scopes don't go through middleware.
    builder.Services.Configure<WhizbangCoreOptions>(o => o.DefaultQueryScope = QueryScope.Global);

    // Register Whizbang generated services
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddReceptors(builder.Services);
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddWhizbangLifecycleMessageDeserializer(builder.Services);
    builder.Services.AddSingleton<Whizbang.Core.Messaging.IEventTypeProvider, ECommerce.Contracts.ECommerceEventTypeProvider>();

    // Configure security to allow anonymous messages for testing
    // This is required because lifecycle receptors in PerspectiveWorker need security context
    // and test events don't have TenantId/UserId in their hops
    builder.Services.Replace(ServiceDescriptor.Singleton(new Whizbang.Core.Security.MessageSecurityOptions { AllowAnonymous = true }));

    // Register perspective runners
    ECommerce.InventoryWorker.Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(builder.Services);
    builder.Services.AddScoped<ECommerce.InventoryWorker.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<ECommerce.InventoryWorker.Perspectives.ProductCatalogPerspective>();

    // Register TopicRegistry to provide base topic names for events
    var topicRegistryInstance = new ECommerce.Contracts.Generated.TopicRegistry();
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRegistry>(topicRegistryInstance);

    // Register dispatcher
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddWhizbangDispatcher(builder.Services);

    // Register lenses
    builder.Services.AddScoped<IProductLens, ProductLens>();
    builder.Services.AddScoped<IInventoryLens, InventoryLens>();

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

    // Configure WorkCoordinatorPublisherWorker with faster polling for integration tests
    builder.Services.Configure<WorkCoordinatorPublisherOptions>(options => {
      options.PollingIntervalMilliseconds = 100;
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = true;
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;
    });

    // Configure PerspectiveWorker with faster polling for integration tests
    builder.Services.Configure<PerspectiveWorkerOptions>(options => {
      options.PollingIntervalMilliseconds = 100;
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = true;
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;
    });

    // Register background workers
    builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();
    builder.Services.AddHostedService<PerspectiveWorker>();

    // Register OrderedStreamProcessor for message ordering
    builder.Services.AddSingleton<OrderedStreamProcessor>();

    // RabbitMQ consumer with test-specific routing
    // Inventory subscribes to test-specific exchanges/queues
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination(
      Address: $"products-{_testId}",
      RoutingKey: $"inventory-products-queue-{_testId}",
      Metadata: new Dictionary<string, JsonElement> {
        ["SubscriberName"] = JsonDocument.Parse("\"inventory-worker\"").RootElement.Clone()
      }
    ));
    builder.Services.AddSingleton(consumerOptions);
    builder.Services.AddHostedService<TransportConsumerWorker>(sp =>
      new TransportConsumerWorker(
        sp.GetRequiredService<ITransport>(),
        consumerOptions,
        new SubscriptionResilienceOptions(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        jsonOptions,
        sp.GetRequiredService<OrderedStreamProcessor>(),
        sp.GetRequiredService<ILifecycleMessageDeserializer>(),
        sp.GetService<TransportMetrics>(),
        sp.GetRequiredService<ILogger<TransportConsumerWorker>>()
      )
    );

    // Logging
    builder.Services.AddLogging(logging => {
      logging.SetMinimumLevel(LogLevel.Information);
      logging.AddConsole();
    });

    return builder.Build();
  }

  private IHost _createBffHost() {
    var builder = Host.CreateApplicationBuilder();

    // Add connection string to configuration for generated turnkey extensions
    // The generated code derives "bff-db" from "BffDbContext"
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:bff-db"] = _bffPostgresConnection
    });

    // Register service instance provider (unique instance ID per test)
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp =>
      new TestServiceInstanceProvider(Uuid7.NewUuid7().ToGuid(), "BFF.API"));

    // IMPORTANT: Explicitly call module initializers for test assemblies (may not run automatically)
    ECommerce.BFF.API.Generated.GeneratedModelRegistration.Initialize();
    ECommerce.Contracts.Generated.WhizbangIdConverterInitializer.Initialize();

    // Create JsonSerializerOptions from global registry using JsonContextRegistry.CreateCombinedOptions()
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    builder.Services.AddSingleton(jsonOptions);

    // Register RabbitMQ transport
    builder.Services.AddRabbitMQTransport(_rabbitMqConnection);

    // Add trace store
    builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

    // Register OrderedStreamProcessor for message ordering
    builder.Services.AddSingleton<OrderedStreamProcessor>();

    // Register routing strategy (maps to test-specific exchanges)
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRoutingStrategy>(
      new TestRabbitMqRoutingStrategy(_testId));

    // Turnkey registration (via .WithEFCore<T>().WithDriver.Postgres below) handles:
    // - NpgsqlDataSource creation with ConfigureJsonOptions + EnableDynamicJson
    // - AddDbContext<BffDbContext> with UseNpgsql
    // - IDbContextFactory<BffDbContext> singleton registration
    // Connection string is provided via config ("ConnectionStrings:bff-db" above)

    // CRITICAL: Register IDatabaseReadinessCheck that always returns true
    // The fixture ensures the database schema is created before starting hosts,
    // and PostgresDatabaseReadinessCheck checks for tables in 'public' schema but we use named schemas.
    builder.Services.AddSingleton<IDatabaseReadinessCheck>(sp => new DefaultDatabaseReadinessCheck());

    // IMPORTANT: Explicitly call module initializers for test assemblies (may not run automatically)
    ECommerce.BFF.API.Generated.GeneratedModelRegistration.Initialize();
    ECommerce.Contracts.Generated.WhizbangIdConverterInitializer.Initialize();

    // CRITICAL: Clear the global Dispatcher callback before calling AddWhizbang().
    // See comment in _createInventoryHost() for full explanation.
    ServiceRegistrationCallbacks.Dispatcher = null;

    // Register Whizbang with EFCore infrastructure
    _ = builder.Services
      .AddWhizbang()
      .WithEFCore<ECommerce.BFF.API.BffDbContext>()
      .WithDriver.Postgres;

    // Use Global scope for integration tests (no tenant filtering needed)
    // Without this, lens queries default to Tenant scope which requires IScopeContextAccessor.Current
    // to be set by middleware — but test scopes don't go through middleware.
    builder.Services.Configure<WhizbangCoreOptions>(o => o.DefaultQueryScope = QueryScope.Global);

    // Register lifecycle services for Distribute stage support
    ECommerce.BFF.API.Generated.DispatcherRegistrations.AddWhizbangLifecycleMessageDeserializer(builder.Services);
    builder.Services.AddSingleton<Whizbang.Core.Messaging.IEventTypeProvider, ECommerce.Contracts.ECommerceEventTypeProvider>();

    // Configure security to allow anonymous messages for testing
    // This is required because lifecycle receptors in PerspectiveWorker need security context
    // and test events don't have TenantId/UserId in their hops
    builder.Services.Replace(ServiceDescriptor.Singleton(new Whizbang.Core.Security.MessageSecurityOptions { AllowAnonymous = true }));

    // Register TopicRegistry
    var topicRegistryInstance = new ECommerce.Contracts.Generated.TopicRegistry();
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRegistry>(topicRegistryInstance);

    // Register dispatcher
    ECommerce.BFF.API.Generated.DispatcherRegistrations.AddWhizbangDispatcher(builder.Services);

    // Register SignalR (required by BFF lenses)
    builder.Services.AddSignalR();

    // Register perspective runners
    ECommerce.BFF.API.Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(builder.Services);
    builder.Services.AddScoped<ECommerce.BFF.API.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<ECommerce.BFF.API.Perspectives.ProductCatalogPerspective>();

    // Register lenses
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

    // Configure WorkCoordinatorPublisherWorker with faster polling for integration tests
    builder.Services.Configure<WorkCoordinatorPublisherOptions>(options => {
      options.PollingIntervalMilliseconds = 100;
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = true;
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;
    });

    // Configure PerspectiveWorker with faster polling for integration tests
    builder.Services.Configure<PerspectiveWorkerOptions>(options => {
      options.PollingIntervalMilliseconds = 100;
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = true;
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;
    });

    // Register background workers
    builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();
    builder.Services.AddHostedService<PerspectiveWorker>();

    // RabbitMQ consumer with test-specific routing
    // BFF subscribes only to exchanges for events its perspectives handle:
    // - products: ProductCatalogPerspective (ProductCreated, ProductUpdated, ProductDeleted)
    // - inventory: InventoryLevelsPerspective (ProductCreated, InventoryRestocked, InventoryReserved, InventoryReleased, InventoryAdjusted)
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination(
      Address: $"products-{_testId}",
      RoutingKey: $"bff-products-queue-{_testId}",
      Metadata: new Dictionary<string, JsonElement> {
        ["SubscriberName"] = JsonDocument.Parse("\"bff-api\"").RootElement.Clone()
      }
    ));
    consumerOptions.Destinations.Add(new TransportDestination(
      Address: $"inventory-{_testId}",
      RoutingKey: $"bff-inventory-queue-{_testId}",
      Metadata: new Dictionary<string, JsonElement> {
        ["SubscriberName"] = JsonDocument.Parse("\"bff-api\"").RootElement.Clone()
      }
    ));
    builder.Services.AddSingleton(consumerOptions);
    builder.Services.AddHostedService<TransportConsumerWorker>(sp =>
      new TransportConsumerWorker(
        sp.GetRequiredService<ITransport>(),
        consumerOptions,
        new SubscriptionResilienceOptions(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        jsonOptions,
        sp.GetRequiredService<OrderedStreamProcessor>(),
        sp.GetRequiredService<ILifecycleMessageDeserializer>(),
        sp.GetService<TransportMetrics>(),
        sp.GetRequiredService<ILogger<TransportConsumerWorker>>()
      )
    );

    // Logging
    builder.Services.AddLogging(logging => {
      logging.SetMinimumLevel(LogLevel.Information);
      logging.AddConsole();
    });

    return builder.Build();
  }

  private async Task _initializeDatabaseSchemasAsync(CancellationToken ct) {
    // Create both per-test databases (each host gets its own database to eliminate lock contention)
    Console.WriteLine("[RabbitMqFixture] Creating Inventory database...");
    await _createDatabaseAsync(_inventoryPostgresConnection, ct);
    Console.WriteLine("[RabbitMqFixture] Inventory database created!");

    Console.WriteLine("[RabbitMqFixture] Creating BFF database...");
    await _createDatabaseAsync(_bffPostgresConnection, ct);
    Console.WriteLine("[RabbitMqFixture] BFF database created!");

    // Initialize Inventory database
    // CRITICAL: Must run BEFORE starting hosts, otherwise workers fail trying to call process_work_batch
    Console.WriteLine("[RabbitMqFixture] Initializing Inventory database schema...");
    if (_inventoryHost != null) {
      using var scope = _inventoryHost.Services.CreateScope();
      Console.WriteLine("[RabbitMqFixture] Created scope for Inventory");
      var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();
      Console.WriteLine("[RabbitMqFixture] Got InventoryDbContext");
      var logger = scope.ServiceProvider.GetRequiredService<ILogger<RabbitMqIntegrationFixture>>();
      Console.WriteLine("[RabbitMqFixture] Calling EnsureWhizbangDatabaseInitializedAsync for Inventory...");
      await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger, cancellationToken: ct);
      Console.WriteLine("[RabbitMqFixture] Inventory database schema initialized");
    }

    // Initialize BFF database
    Console.WriteLine("[RabbitMqFixture] Initializing BFF database schema...");
    if (_bffHost != null) {
      using var scope = _bffHost.Services.CreateScope();
      Console.WriteLine("[RabbitMqFixture] Created scope for BFF");
      var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();
      Console.WriteLine("[RabbitMqFixture] Got BffDbContext");
      var logger = scope.ServiceProvider.GetRequiredService<ILogger<RabbitMqIntegrationFixture>>();
      Console.WriteLine("[RabbitMqFixture] Calling EnsureWhizbangDatabaseInitializedAsync for BFF...");
      await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger, cancellationToken: ct);
      Console.WriteLine("[RabbitMqFixture] BFF database schema initialized");
    }

    // Register message associations for perspective auto-checkpoint creation
    // CRITICAL: Must run AFTER schema initialization (tables exist) and BEFORE starting hosts (workers need associations)
    Console.WriteLine("[RabbitMqFixture] Registering message associations...");
    if (_inventoryHost != null) {
      using var scope = _inventoryHost.Services.CreateScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();
      var logger = scope.ServiceProvider.GetRequiredService<ILogger<RabbitMqIntegrationFixture>>();

      await ECommerce.InventoryWorker.Generated.EFCorePerspectiveAssociationExtensions.RegisterPerspectiveAssociationsAsync(
        dbContext,
        "inventory",
        "ECommerce.InventoryWorker",
        logger,
        ct
      );

      Console.WriteLine("[RabbitMqFixture] InventoryWorker message associations registered (inventory schema)");
    }

    if (_bffHost != null) {
      using var scope = _bffHost.Services.CreateScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();
      var logger = scope.ServiceProvider.GetRequiredService<ILogger<RabbitMqIntegrationFixture>>();

      await ECommerce.BFF.API.Generated.EFCorePerspectiveAssociationExtensions.RegisterPerspectiveAssociationsAsync(
        dbContext,
        "bff",
        "ECommerce.BFF.API",
        logger,
        ct
      );

      Console.WriteLine("[RabbitMqFixture] BFF message associations registered (bff schema)");
    }

    Console.WriteLine("[RabbitMqFixture] Database initialization complete.");
  }

  /// <summary>
  /// Waits for all workers (outbox publisher + perspective) on both hosts to complete
  /// their first polling cycle. Uses OnWorkProcessingIdle completion signals instead of
  /// Task.Delay, making the wait deterministic and fast.
  /// </summary>
  private async Task _waitForWorkersReadyAsync(CancellationToken ct) {
    var tcsInventoryPub = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var tcsBffPub = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var tcsInventoryPersp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var tcsBffPersp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    var inventoryPub = _inventoryHost!.Services.GetServices<IHostedService>().OfType<WorkCoordinatorPublisherWorker>().FirstOrDefault();
    var bffPub = _bffHost!.Services.GetServices<IHostedService>().OfType<WorkCoordinatorPublisherWorker>().FirstOrDefault();
    var inventoryPersp = _inventoryHost.Services.GetServices<IHostedService>().OfType<PerspectiveWorker>().FirstOrDefault();
    var bffPersp = _bffHost.Services.GetServices<IHostedService>().OfType<PerspectiveWorker>().FirstOrDefault();

    // Wire one-shot idle handlers (signal on first idle = worker completed first poll cycle)
    void WireOnce(WorkCoordinatorPublisherWorker? w, TaskCompletionSource<bool> tcs) {
      if (w is null) { tcs.TrySetResult(true); return; }
      if (w.IsIdle) { tcs.TrySetResult(true); return; }
      WorkProcessingIdleHandler? h = null;
      h = () => { tcs.TrySetResult(true); w.OnWorkProcessingIdle -= h; };
      w.OnWorkProcessingIdle += h;
      if (w.IsIdle) {
        tcs.TrySetResult(true); // re-check after subscribe (race)
      }
    }

    void WirePerspOnce(PerspectiveWorker? w, TaskCompletionSource<bool> tcs) {
      if (w is null) { tcs.TrySetResult(true); return; }
      if (w.IsIdle) { tcs.TrySetResult(true); return; }
      WorkProcessingIdleHandler? h = null;
      h = () => { tcs.TrySetResult(true); w.OnWorkProcessingIdle -= h; };
      w.OnWorkProcessingIdle += h;
      if (w.IsIdle) {
        tcs.TrySetResult(true);
      }
    }

    WireOnce(inventoryPub, tcsInventoryPub);
    WireOnce(bffPub, tcsBffPub);
    WirePerspOnce(inventoryPersp, tcsInventoryPersp);
    WirePerspOnce(bffPersp, tcsBffPersp);

    // Wait for all 4 workers to signal idle (or timeout as safety net)
    await Task.WhenAll(
      tcsInventoryPub.Task,
      tcsBffPub.Task,
      tcsInventoryPersp.Task,
      tcsBffPersp.Task
    ).WaitAsync(TimeSpan.FromSeconds(30), ct);

    Console.WriteLine("[RabbitMqFixture] All workers completed first poll cycle");
  }

  private async Task _deleteQueueAsync(string queueName, CancellationToken ct = default) {
    try {
      var response = await _managementClient.DeleteAsync($"/api/queues/%2F/{queueName}", ct);
      response.EnsureSuccessStatusCode();
    } catch {
      // Queue might not exist, ignore
    }
  }

  private async Task _deleteExchangeAsync(string exchangeName, CancellationToken ct = default) {
    try {
      var response = await _managementClient.DeleteAsync($"/api/exchanges/%2F/{exchangeName}", ct);
      response.EnsureSuccessStatusCode();
    } catch {
      // Exchange might not exist, ignore
    }
  }

  /// <summary>
  /// Creates the per-test database using the template database.
  /// </summary>
  private static async Task _createDatabaseAsync(string connectionString, CancellationToken ct) {
    // Extract database name from connection string
    var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
    var dbName = builder.Database;

    // Connect to postgres database (the template) to create our test database
    builder.Database = "postgres";
    var adminConnectionString = builder.ConnectionString;

    await using var connection = new Npgsql.NpgsqlConnection(adminConnectionString);
    await connection.OpenAsync(ct);

    // Create database (IF NOT EXISTS for idempotency)
    var createDbCommand = connection.CreateCommand();
    createDbCommand.CommandText = $"CREATE DATABASE \"{dbName}\"";

    try {
      await createDbCommand.ExecuteNonQueryAsync(ct);
    } catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P04") {
      // Database already exists, ignore (42P04 = duplicate_database)
      Console.WriteLine($"[RabbitMqFixture] Database {dbName} already exists");
    }
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

    var inventoryRegistry = _inventoryHost!.Services.GetRequiredService<IReceptorRegistry>();
    var bffRegistry = _bffHost!.Services.GetRequiredService<IReceptorRegistry>();

    return new PerspectiveCompletionWaiter<TEvent>(
      inventoryRegistry,
      bffRegistry,
      inventoryPerspectives,
      bffPerspectives
    );
  }

  /// <summary>
  /// Waits for perspective processing to complete using lifecycle receptors.
  /// This is a convenience method that creates a waiter, waits, and disposes it.
  /// </summary>
  /// <typeparam name="TEvent">The event type to wait for</typeparam>
  /// <param name="inventoryPerspectives">Number of perspectives expected in InventoryWorker host</param>
  /// <param name="bffPerspectives">Number of perspectives expected in BFF host</param>
  /// <param name="timeoutMilliseconds">Maximum time to wait in milliseconds (default: 15000ms)</param>
  /// <exception cref="TimeoutException">Thrown if perspective processing doesn't complete within timeout</exception>
  /// <docs>testing/lifecycle-synchronization</docs>
  public async Task WaitForPerspectiveCompletionAsync<TEvent>(
    int inventoryPerspectives,
    int bffPerspectives,
    int timeoutMilliseconds = 15000)
    where TEvent : IEvent {

    using var waiter = CreatePerspectiveWaiter<TEvent>(inventoryPerspectives, bffPerspectives);
    await waiter.WaitAsync(timeoutMilliseconds);
  }

  public async ValueTask DisposeAsync() {
    // Dispose scopes first
    _inventoryScope?.Dispose();
    _bffScope?.Dispose();

    // Stop and dispose hosts (this will close RabbitMQ consumers/channels and DB connections)
    if (_inventoryHost != null) {
      await _inventoryHost.StopAsync(TimeSpan.FromSeconds(10)); // Increased timeout for graceful shutdown
      _inventoryHost.Dispose();
    }

    if (_bffHost != null) {
      await _bffHost.StopAsync(TimeSpan.FromSeconds(10)); // Increased timeout for graceful shutdown
      _bffHost.Dispose();
    }

    // Clean up RabbitMQ resources for this test to prevent stale messages bleeding into subsequent tests
    Console.WriteLine($"[RabbitMqFixture] Cleaning up RabbitMQ resources for testId={_testId}...");
    await _deleteQueueAsync($"bff-products-queue-{_testId}");
    await _deleteQueueAsync($"inventory-products-queue-{_testId}");
    await _deleteQueueAsync($"bff-inventory-queue-{_testId}");
    await _deleteExchangeAsync($"products-{_testId}");
    await _deleteExchangeAsync($"inventory-{_testId}");
    Console.WriteLine("[RabbitMqFixture] RabbitMQ resources cleaned up.");

    // Clear connection pools to ensure all DB connections are closed
    // CRITICAL: Must happen BEFORE dropping databases
    _clearConnectionPool(_inventoryPostgresConnection);
    _clearConnectionPool(_bffPostgresConnection);

    // Clean up per-test databases
    // CRITICAL: Must happen AFTER hosts are disposed and connection pools cleared
    await _dropDatabaseAsync(_inventoryPostgresConnection);
    await _dropDatabaseAsync(_bffPostgresConnection);

    _managementClient.Dispose();
  }

  /// <summary>
  /// Clears the Npgsql connection pool for a database connection string.
  /// This ensures all connections are closed before dropping the database.
  /// </summary>
  private static void _clearConnectionPool(string connectionString) {
    try {
      using var connection = new Npgsql.NpgsqlConnection(connectionString);
      Npgsql.NpgsqlConnection.ClearPool(connection);
      Console.WriteLine("[RabbitMqFixture] Cleared connection pool");
    } catch (Exception ex) {
      // Log but don't throw - cleanup failures shouldn't break tests
      Console.WriteLine($"[RabbitMqFixture] Warning: Failed to clear connection pool: {ex.Message}");
    }
  }

  /// <summary>
  /// Drops a per-test database after closing all active connections.
  /// This prevents database accumulation and connection pool exhaustion.
  /// </summary>
  private static async Task _dropDatabaseAsync(string connectionString) {
    try {
      // Extract database name from connection string
      var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
      var dbName = builder.Database;

      // Skip if no database specified
      if (string.IsNullOrEmpty(dbName) || dbName == "postgres") {
        return;
      }

      // Connect to postgres database (the template) to drop our test database
      builder.Database = "postgres";
      var adminConnectionString = builder.ConnectionString;

      await using var connection = new Npgsql.NpgsqlConnection(adminConnectionString);
      await connection.OpenAsync();

      // Terminate all connections to the database before dropping
      // This prevents "database is being accessed by other users" errors
      await using var terminateCommand = connection.CreateCommand();
      terminateCommand.CommandText = $@"
        SELECT pg_terminate_backend(pid)
        FROM pg_stat_activity
        WHERE datname = '{dbName}'
          AND pid <> pg_backend_pid();
      ";
      await terminateCommand.ExecuteNonQueryAsync();

      // Drop the database
      await using var dropCommand = connection.CreateCommand();
      dropCommand.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\"";
      await dropCommand.ExecuteNonQueryAsync();

      Console.WriteLine($"[RabbitMqFixture] Dropped database: {dbName}");
    } catch (Exception ex) {
      // Log but don't throw - cleanup failures shouldn't break tests
      Console.WriteLine($"[RabbitMqFixture] Warning: Failed to drop database: {ex.Message}");
    }
  }
}
