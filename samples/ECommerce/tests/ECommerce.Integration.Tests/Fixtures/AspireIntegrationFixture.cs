using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Azure.Messaging.ServiceBus;
using ECommerce.BFF.API.Generated;
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
/// Integration test fixture that creates PostgreSQL and service hosts per-test.
/// ServiceBus emulator is pre-created and shared across tests.
/// All tests share the same topics (topic-00 and topic-01).
/// Message draining provides isolation between tests and test runs.
/// </summary>
public sealed class AspireIntegrationFixture : IAsyncDisposable {
  private readonly string _serviceBusConnection;
  private readonly string _topicA = "products";  // Topic names from real applications
  private readonly string _topicB = "inventory";
  private readonly int _batchIndex;
  private bool _isInitialized;
  private readonly Guid _testPollerInstanceId = Uuid7.NewUuid7().ToGuid();
  private readonly ServiceBusClient _sharedServiceBusClient;  // Shared client for test operations

  // Per-test resources (created during InitializeAsync)
  private DistributedApplication? _aspireApp;
  private IHost? _inventoryHost;
  private IHost? _bffHost;
  private IServiceScope? _inventoryScope;
  private IServiceScope? _bffScope;
  private string? _postgresConnection;

  /// <summary>
  /// Creates a new fixture instance that will create PostgreSQL and service hosts per-test.
  /// Uses a SHARED ServiceBusClient that all tests and hosts reuse (to stay under connection quota).
  /// </summary>
  /// <param name="serviceBusConnectionString">The ServiceBus connection string (from pre-created emulator)</param>
  /// <param name="sharedServiceBusClient">The SHARED ServiceBusClient that all hosts will reuse</param>
  /// <param name="batchIndex">The batch index for diagnostic logging (always 0)</param>
  public AspireIntegrationFixture(
    string serviceBusConnectionString,
    ServiceBusClient sharedServiceBusClient,
    int batchIndex
  ) {
    _serviceBusConnection = serviceBusConnectionString;
    _sharedServiceBusClient = sharedServiceBusClient ?? throw new ArgumentNullException(nameof(sharedServiceBusClient));
    _batchIndex = batchIndex;
    Console.WriteLine($"[AspireFixture] Using topics: {_topicA}, {_topicB}");
    Console.WriteLine("[AspireFixture] Using SHARED ServiceBusClient (reused by all hosts)");
  }

  /// <summary>
  /// Gets the IDispatcher instance for sending commands (from InventoryWorker host).
  /// The Dispatcher creates its own scope internally when publishing events.
  /// </summary>
  public IDispatcher Dispatcher => _inventoryHost?.Services.GetRequiredService<IDispatcher>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the IProductLens instance for querying product catalog (from InventoryWorker host).
  /// Resolves from a long-lived scope that persists for the lifetime of the fixture.
  /// </summary>
  public IProductLens InventoryProductLens => _inventoryScope?.ServiceProvider.GetRequiredService<IProductLens>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the IInventoryLens instance for querying inventory levels (from InventoryWorker host).
  /// Resolves from a long-lived scope that persists for the lifetime of the fixture.
  /// </summary>
  public IInventoryLens InventoryLens => _inventoryScope?.ServiceProvider.GetRequiredService<IInventoryLens>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the IProductCatalogLens instance for querying product catalog (from BFF host).
  /// Resolves from a long-lived scope that persists for the lifetime of the fixture.
  /// </summary>
  public IProductCatalogLens BffProductLens => _bffScope?.ServiceProvider.GetRequiredService<IProductCatalogLens>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the IInventoryLevelsLens instance for querying inventory levels (from BFF host).
  /// Resolves from a long-lived scope that persists for the lifetime of the fixture.
  /// </summary>
  public IInventoryLevelsLens BffInventoryLens => _bffScope?.ServiceProvider.GetRequiredService<IInventoryLevelsLens>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the PostgreSQL connection string for direct database operations.
  /// </summary>
  public string ConnectionString => _postgresConnection
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets a logger instance for use in test scenarios.
  /// </summary>
  public ILogger<T> GetLogger<T>() {
    return _inventoryHost?.Services.GetRequiredService<ILogger<T>>()
      ?? throw new InvalidOperationException("Fixture not initialized");
  }

  /// <summary>
  /// Initializes the test fixture by creating PostgreSQL container and service hosts.
  /// ServiceBus emulator is already pre-created via ClassDataSource.
  /// </summary>
  [RequiresDynamicCode("EF Core in tests may use dynamic code")]
  [RequiresUnreferencedCode("EF Core in tests may use unreferenced code")]
  public async Task InitializeAsync(CancellationToken cancellationToken = default) {
    if (_isInitialized) {
      return;
    }

    Console.WriteLine($"[AspireFixture] Initializing for topics: {_topicA}, {_topicB}");

    // Create Aspire app (PostgreSQL container)
    Console.WriteLine("[AspireFixture] Creating PostgreSQL container...");
    _aspireApp = await CreateAspireAppAsync(cancellationToken);
    _postgresConnection = await GetPostgresConnectionStringAsync(_aspireApp, cancellationToken);
    Console.WriteLine("[AspireFixture] PostgreSQL ready.");

    // Drain stale messages from ServiceBus subscriptions BEFORE starting hosts
    Console.WriteLine("[AspireFixture] Draining stale messages from subscriptions...");
    await DrainSubscriptionsAsync(cancellationToken);
    Console.WriteLine("[AspireFixture] Subscriptions drained.");

    // Create service hosts (InventoryWorker + BFF)
    // IMPORTANT: Do NOT start hosts yet - schema must be initialized first!
    Console.WriteLine("[AspireFixture] Creating service hosts...");
    _inventoryHost = CreateInventoryHost(_postgresConnection, _serviceBusConnection, _topicA, _topicB);
    _bffHost = CreateBffHost(_postgresConnection, _serviceBusConnection, _topicA, _topicB);

    // Initialize Whizbang database schema (create tables, functions, etc.)
    // CRITICAL: Must run BEFORE starting hosts, otherwise workers fail trying to call process_work_batch
    Console.WriteLine("[AspireFixture] Initializing database schema...");
    using (var initScope = _inventoryHost.Services.CreateScope()) {
      var inventoryDbContext = initScope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();
      var logger = initScope.ServiceProvider.GetRequiredService<ILogger<AspireIntegrationFixture>>();
      await inventoryDbContext.EnsureWhizbangDatabaseInitializedAsync(logger, cancellationToken);
    }
    using (var initScope = _bffHost.Services.CreateScope()) {
      var bffDbContext = initScope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();
      var logger = initScope.ServiceProvider.GetRequiredService<ILogger<AspireIntegrationFixture>>();
      await bffDbContext.EnsureWhizbangDatabaseInitializedAsync(logger, cancellationToken);
    }
    Console.WriteLine("[AspireFixture] Database schema initialized.");

    // Register message associations for perspective auto-checkpoint creation
    // CRITICAL: Must run AFTER schema initialization (tables exist) and BEFORE starting hosts (workers need associations)
    Console.WriteLine("[AspireFixture] Registering message associations...");
    using (var initScope = _inventoryHost.Services.CreateScope()) {
      var inventoryDbContext = initScope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();
      var logger = initScope.ServiceProvider.GetRequiredService<ILogger<AspireIntegrationFixture>>();

      await ECommerce.InventoryWorker.Generated.PerspectiveRegistrationExtensions.RegisterPerspectiveAssociationsAsync(
        inventoryDbContext,
        schema: "inventory",
        serviceName: "ECommerce.InventoryWorker",
        logger: logger,
        cancellationToken: cancellationToken
      );

      Console.WriteLine("[AspireFixture] InventoryWorker message associations registered (inventory schema)");
    }
    using (var initScope = _bffHost.Services.CreateScope()) {
      var bffDbContext = initScope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();
      var logger = initScope.ServiceProvider.GetRequiredService<ILogger<AspireIntegrationFixture>>();

      await ECommerce.BFF.API.Generated.PerspectiveRegistrationExtensions.RegisterPerspectiveAssociationsAsync(
        bffDbContext,
        schema: "bff",
        serviceName: "ECommerce.BFF.API",
        logger: logger,
        cancellationToken: cancellationToken
      );

      Console.WriteLine("[AspireFixture] BFF message associations registered (bff schema)");
    }
    Console.WriteLine("[AspireFixture] Message associations registered.");

    // Start hosts AFTER schema is ready
    Console.WriteLine("[AspireFixture] Starting service hosts...");
    await _inventoryHost.StartAsync(cancellationToken);
    await _bffHost.StartAsync(cancellationToken);
    Console.WriteLine("[AspireFixture] Service hosts started.");

    // Create long-lived scopes for lenses
    _inventoryScope = _inventoryHost.Services.CreateScope();
    _bffScope = _bffHost.Services.CreateScope();

    Console.WriteLine("[AspireFixture] Service hosts ready.");

    // Clean up any stale data
    Console.WriteLine("[AspireFixture] Cleaning database for test isolation...");
    await CleanupDatabaseAsync(cancellationToken);
    Console.WriteLine("[AspireFixture] Database cleaned.");

    Console.WriteLine("[AspireFixture] Ready for test execution!");

    _isInitialized = true;
  }

  /// <summary>
  /// Drains all messages from assigned subscriptions to ensure clean state for test.
  /// Critical for test isolation when using shared generic topics across test runs.
  /// Uses the shared ServiceBusClient to avoid creating extra connections.
  /// </summary>
  private async Task DrainSubscriptionsAsync(CancellationToken cancellationToken = default) {
    // Use shared client instead of creating a new one
    // This reduces connection count and avoids ConnectionsQuotaExceeded errors

    // Drain subscriptions for both topics (products and inventory)
    var subscriptions = new[] {
      (_topicA, "sub-inventory-products"),
      (_topicA, "sub-bff-products"),
      (_topicB, "sub-bff-inventory")
    };

    foreach (var (topic, subscription) in subscriptions) {
      var receiver = _sharedServiceBusClient.CreateReceiver(topic, subscription);
      var drained = 0;

      // Drain up to 100 messages per subscription (safety limit)
      for (var i = 0; i < 100; i++) {
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
        if (message == null) {
          break;  // No more messages
        }

        await receiver.CompleteMessageAsync(message, cancellationToken);
        drained++;
      }

      await receiver.DisposeAsync();

      if (drained > 0) {
        Console.WriteLine($"  [Drain] ✓ {topic}/{subscription}: {drained} messages");
      }
    }
  }

  /// <summary>
  /// Creates the Aspire app with PostgreSQL container.
  /// </summary>
  private static async Task<DistributedApplication> CreateAspireAppAsync(CancellationToken cancellationToken = default) {
    var appHost = await DistributedApplicationTestingBuilder
      .CreateAsync<Projects.ECommerce_Integration_Tests_AppHost>(cancellationToken: cancellationToken);

    appHost.Services.ConfigureHttpClientDefaults(http => {
      http.AddStandardResilienceHandler();
    });

    return await appHost.BuildAsync(cancellationToken);
  }

  /// <summary>
  /// Gets the PostgreSQL connection string from the Aspire app.
  /// Waits for PostgreSQL to be ready.
  /// </summary>
  private static async Task<string> GetPostgresConnectionStringAsync(
    DistributedApplication app,
    CancellationToken cancellationToken = default
  ) {
    await app.StartAsync(cancellationToken);
    var connectionString = await app.GetConnectionStringAsync("whizbang-integration-test", cancellationToken)
      ?? throw new InvalidOperationException("Failed to get PostgreSQL connection string");

    // Wait for PostgreSQL to be ready
    await WaitForPostgresAsync(connectionString, cancellationToken);

    return connectionString;
  }

  /// <summary>
  /// Waits for PostgreSQL to be ready by attempting to connect until successful.
  /// </summary>
  private static async Task WaitForPostgresAsync(string connectionString, CancellationToken cancellationToken = default) {
    var maxAttempts = 30;
    var delay = TimeSpan.FromSeconds(1);

    for (var attempt = 1; attempt <= maxAttempts; attempt++) {
      try {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        Console.WriteLine($"[AspireFixture] PostgreSQL ready after {attempt} attempt(s)");
        return;
      } catch (Npgsql.NpgsqlException) when (attempt < maxAttempts) {
        await Task.Delay(delay, cancellationToken);
      }
    }

    throw new InvalidOperationException($"PostgreSQL did not become ready after {maxAttempts} attempts");
  }

  /// <summary>
  /// Creates the InventoryWorker host with generic topic subscriptions.
  /// </summary>
  private IHost CreateInventoryHost(
    string postgresConnectionString,
    string serviceBusConnectionString,
    string topicA,
    string topicB
  ) {
    var builder = Host.CreateApplicationBuilder();

    // Register service instance provider (unique instance ID per test)
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp => new TestServiceInstanceProvider(Uuid7.NewUuid7().ToGuid(), "InventoryWorker"));

    // IMPORTANT: Explicitly call module initializers for test assemblies (may not run automatically)
    ECommerce.InventoryWorker.Generated.GeneratedModelRegistration.Initialize();
    ECommerce.Contracts.Generated.WhizbangIdConverterInitializer.Initialize();

    // Create JsonSerializerOptions from global registry using JsonContextRegistry.CreateCombinedOptions()
    // This combines ALL registered contexts including lens DTOs from source generator
    // DO NOT use WhizbangJsonContext.CreateOptions() - that hardcodes only 4 contexts and ignores the registry!
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    // Register JsonSerializerOptions in DI for Azure Service Bus transport
    builder.Services.AddSingleton(jsonOptions);

    // CRITICAL: Register SHARED ServiceBusClient BEFORE calling AddAzureServiceBusTransport
    // This ensures AddAzureServiceBusTransport resolves the shared client instead of creating a new one
    // Keeps us under the emulator's connection quota (~25 connections)
    builder.Services.AddSingleton(_sharedServiceBusClient);
    Console.WriteLine("[InventoryHost] Registered SHARED ServiceBusClient in DI");

    // Register Azure Service Bus transport (will resolve shared client from DI)
    builder.Services.AddAzureServiceBusTransport(serviceBusConnectionString);

    // EF Core with PostgreSQL - simple UseNpgsql (matches real InventoryWorker Program.cs)
    // Whizbang's EF Core integration handles JSON serialization for JSONB columns
    builder.Services.AddDbContext<ECommerce.InventoryWorker.InventoryDbContext>(options => {
      options.UseNpgsql(postgresConnectionString);
    });

    // Register Whizbang with EFCore infrastructure
    _ = builder.Services
      .AddWhizbang()
      .WithEFCore<ECommerce.InventoryWorker.InventoryDbContext>()
      .WithDriver.Postgres;

    // Register Whizbang generated services
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddReceptors(builder.Services);
    builder.Services.AddWhizbangAggregateIdExtractor();

    // Register perspective runners (generated by PerspectiveRunnerRegistryGenerator)
    ECommerce.InventoryWorker.Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(builder.Services);

    // Register concrete perspective types for runner resolution
    builder.Services.AddScoped<ECommerce.InventoryWorker.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<ECommerce.InventoryWorker.Perspectives.ProductCatalogPerspective>();

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

    // Azure Service Bus consumer with actual topic names matching InventoryWorker
    var consumerOptions = new ServiceBusConsumerOptions();
    consumerOptions.Subscriptions.Add(new TopicSubscription(topicA, "sub-inventory-products"));
    builder.Services.AddSingleton(consumerOptions);

    // Register IWorkChannelWriter for communication between strategy and worker
    builder.Services.AddSingleton<IWorkChannelWriter, WorkChannelWriter>();

    // Configure WorkCoordinatorPublisherWorker with faster polling for integration tests
    builder.Services.Configure<WorkCoordinatorPublisherOptions>(options => {
      options.PollingIntervalMilliseconds = 100;  // Fast polling for tests
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = true;  // DIAGNOSTIC: Enable SQL debug logging
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;  // Require 2 empty polls to consider idle
    });

    // Configure PerspectiveWorker with faster polling for integration tests
    builder.Services.Configure<PerspectiveWorkerOptions>(options => {
      options.PollingIntervalMilliseconds = 100;  // Fast polling for tests
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = true;  // DIAGNOSTIC: Enable checkpoint tracking
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;  // Require 2 empty polls to consider idle
    });

    // Register OrderedStreamProcessor for message ordering
    builder.Services.AddSingleton<OrderedStreamProcessor>();

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

    // Logging
    builder.Services.AddLogging(logging => {
      logging.SetMinimumLevel(LogLevel.Information);
      logging.AddConsole();
    });

    return builder.Build();
  }

  /// <summary>
  /// Creates the BFF host with generic topic subscriptions.
  /// </summary>
  private IHost CreateBffHost(
    string postgresConnectionString,
    string serviceBusConnectionString,
    string topicA,
    string topicB
  ) {
    var builder = Host.CreateApplicationBuilder();

    // Register service instance provider (unique instance ID per test)
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp => new TestServiceInstanceProvider(Uuid7.NewUuid7().ToGuid(), "BFF.API"));

    // IMPORTANT: Explicitly call module initializers for test assemblies (may not run automatically)
    ECommerce.BFF.API.Generated.GeneratedModelRegistration.Initialize();
    ECommerce.Contracts.Generated.WhizbangIdConverterInitializer.Initialize();

    // Create JsonSerializerOptions from global registry using JsonContextRegistry.CreateCombinedOptions()
    // This combines ALL registered contexts including lens DTOs from source generator
    // DO NOT use WhizbangJsonContext.CreateOptions() - that hardcodes only 4 contexts and ignores the registry!
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    // Register JsonSerializerOptions in DI for Azure Service Bus transport
    builder.Services.AddSingleton(jsonOptions);

    // CRITICAL: Register SHARED ServiceBusClient BEFORE calling AddAzureServiceBusTransport
    // This ensures AddAzureServiceBusTransport resolves the shared client instead of creating a new one
    // Keeps us under the emulator's connection quota (~25 connections)
    builder.Services.AddSingleton(_sharedServiceBusClient);
    Console.WriteLine("[BFFHost] Registered SHARED ServiceBusClient in DI");

    // Register Azure Service Bus transport (will resolve shared client from DI)
    builder.Services.AddAzureServiceBusTransport(serviceBusConnectionString);

    // Add trace store for observability
    builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

    // Register OrderedStreamProcessor for message ordering
    builder.Services.AddSingleton<OrderedStreamProcessor>();

    // EF Core with PostgreSQL - simple UseNpgsql (matches real applications)
    // Whizbang's EF Core integration handles JSON serialization for JSONB columns
    builder.Services.AddDbContext<ECommerce.BFF.API.BffDbContext>(options =>
      options.UseNpgsql(postgresConnectionString));

    // Register Whizbang with EFCore infrastructure

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

    // Azure Service Bus consumer with actual topic names matching BFF.API
    var consumerOptions = new ServiceBusConsumerOptions();
    consumerOptions.Subscriptions.Add(new TopicSubscription(topicA, "sub-bff-products"));
    consumerOptions.Subscriptions.Add(new TopicSubscription(topicB, "sub-bff-inventory"));
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

    // Logging
    builder.Services.AddLogging(logging => {
      logging.SetMinimumLevel(LogLevel.Information);
      logging.AddConsole();
    });

    return builder.Build();
  }

  /// <summary>
  /// Waits for all event processing to complete by querying database tables directly.
  /// Checks for any uncompleted outbox/inbox messages and perspective checkpoints.
  /// This is more reliable than using ProcessWorkBatchAsync which only shows available (not in-progress) work.
  /// Default timeout reduced to 15s thanks to warmup eliminating cold starts.
  /// </summary>
  public async Task WaitForEventProcessingAsync(int timeoutMilliseconds = 15000) {
    Console.WriteLine($"[WaitForEvents] Starting event processing wait (Batch {_batchIndex}, Topics {_topicA}/{_topicB}, timeout={timeoutMilliseconds}ms)");
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var attempt = 0;

    while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds) {
      attempt++;
      using var scope = _inventoryHost!.Services.CreateScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();

      // Query database directly for any uncompleted work using ADO.NET
      var connection = dbContext.Database.GetDbConnection();
      if (connection.State != System.Data.ConnectionState.Open) {
        await connection.OpenAsync();
      }

      await using var cmd = connection.CreateCommand();

      // Check outbox: any messages not marked as Published (status & 4 = 0)
      // Outbox uses MessageProcessingStatus.Published (bit 2, value 4) to indicate completion
      cmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_outbox WHERE (status & 4) = 0";
      var pendingOutbox = (int)(await cmd.ExecuteScalarAsync() ?? 0);

      // Check inbox: any messages not marked as Completed (status & 2 = 0)
      cmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_inbox WHERE (status & 2) = 0";
      var pendingInbox = (int)(await cmd.ExecuteScalarAsync() ?? 0);

      // Check perspective checkpoints: any not marked as Completed (status & 2 = 0) AND not Failed (status & 4 = 0)
      cmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_perspective_checkpoints WHERE (status & 2) = 0 AND (status & 4) = 0";
      var pendingPerspectives = (int)(await cmd.ExecuteScalarAsync() ?? 0);

      // DIAGNOSTIC: Log initial state and checkpoint details
      if (attempt == 1) {
        Console.WriteLine($"[WaitForEvents] Initial state: Outbox={pendingOutbox}, Inbox={pendingInbox}, Perspectives={pendingPerspectives}");

        cmd.CommandText = @"
          SELECT
            perspective_name,
            stream_id::text,
            status,
            COALESCE(last_event_id::text, 'NULL') as last_event_id,
            COALESCE(error, 'NULL') as error
          FROM inventory.wh_perspective_checkpoints
          LIMIT 10";
        await using var reader = await cmd.ExecuteReaderAsync();
        Console.WriteLine("[DIAGNOSTIC] Perspective checkpoints in database:");
        while (await reader.ReadAsync()) {
          Console.WriteLine($"  - {reader.GetString(0)}, stream={reader.GetString(1)}, status={reader.GetInt32(2)}, last_event={reader.GetString(3)}, error={reader.GetString(4)}");
        }
      }

      if (pendingOutbox == 0 && pendingInbox == 0 && pendingPerspectives == 0) {
        Console.WriteLine($"[AspireFixture] Event processing complete - no pending work (checked database after {stopwatch.ElapsedMilliseconds}ms, {attempt} attempts)");
        return;
      }

      // Log progress every 3 attempts (~1-2 seconds) for faster feedback
      if (attempt % 3 == 0) {
        Console.WriteLine($"[WaitForEvents] Still waiting: Outbox={pendingOutbox}, Inbox={pendingInbox}, Perspectives={pendingPerspectives} (attempt {attempt}, elapsed: {stopwatch.ElapsedMilliseconds}ms)");
      }

      // Progressive backoff: start at 100ms, increase to 2000ms
      var delay = Math.Min(100 + (attempt * 100), 2000);
      await Task.Delay(delay);
    }

    // Timeout reached - log final state with batch info
    Console.WriteLine($"[AspireFixture] WARNING: Event processing did not complete within {timeoutMilliseconds}ms timeout (Batch {_batchIndex}, Topics {_topicA}/{_topicB})");

    using var finalScope = _inventoryHost!.Services.CreateScope();
    var finalDbContext = finalScope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();

    var finalConnection = finalDbContext.Database.GetDbConnection();
    if (finalConnection.State != System.Data.ConnectionState.Open) {
      await finalConnection.OpenAsync();
    }

    await using var finalCmd = finalConnection.CreateCommand();

    finalCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_outbox WHERE (status & 4) = 0";
    var finalOutbox = (int)(await finalCmd.ExecuteScalarAsync() ?? 0);

    finalCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_inbox WHERE (status & 2) = 0";
    var finalInbox = (int)(await finalCmd.ExecuteScalarAsync() ?? 0);

    finalCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_perspective_checkpoints WHERE (status & 2) = 0 AND (status & 4) = 0";
    var finalPerspectives = (int)(await finalCmd.ExecuteScalarAsync() ?? 0);

    Console.WriteLine($"[AspireFixture] Final state - Batch {_batchIndex}, Topics {_topicA}/{_topicB}: Outbox={finalOutbox}, Inbox={finalInbox}, Perspectives={finalPerspectives}");
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
      cmd.CommandText = "SELECT DISTINCT event_type FROM inventory.wh_event_store ORDER BY event_type LIMIT 20";
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
      cmd.CommandText = "SELECT DISTINCT message_type FROM inventory.wh_message_associations WHERE association_type = 'perspective' ORDER BY message_type LIMIT 20";
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
      cmd.CommandText = "SELECT COUNT(*)::int FROM inventory.wh_perspective_checkpoints";
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
    // Dispose scopes
    _inventoryScope?.Dispose();
    _bffScope?.Dispose();

    // Stop and dispose hosts (this will close ServiceBus processors/receivers)
    if (_inventoryHost != null) {
      await _inventoryHost.StopAsync();
      _inventoryHost.Dispose();
    }

    if (_bffHost != null) {
      await _bffHost.StopAsync();
      _bffHost.Dispose();
    }

    // CRITICAL: Wait for AMQP connections to fully close
    // ServiceBus processors dispose asynchronously, and connections need time to clean up
    // Without this delay, connections accumulate and exceed emulator quota (~25)
    Console.WriteLine("[AspireFixture] Waiting for ServiceBus connections to close...");
    await Task.Delay(2000);  // 2 second delay for connection cleanup
    Console.WriteLine("[AspireFixture] ServiceBus connections closed.");

    // Dispose Aspire app (stops PostgreSQL container)
    if (_aspireApp != null) {
      await _aspireApp.DisposeAsync();
    }

    // DO NOT dispose shared ServiceBusClient here - it's owned by SharedFixtureSource
    // and should only be disposed once when ALL tests complete
    // Stopping the hosts above closes all ServiceBus processors/receivers properly
  }
}
