using System.Diagnostics.CodeAnalysis;
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
using Testcontainers.PostgreSql;
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
/// Integration test fixture that creates PostgreSQL (TestContainers) and service hosts per-test.
/// ServiceBus emulator is pre-created and shared across tests via SharedFixtureSource.
/// All tests share the same topics (topic-00 and topic-01).
/// Message draining provides isolation between tests and test runs.
/// </summary>
public sealed class ServiceBusIntegrationFixture : IAsyncDisposable {
  private readonly string _serviceBusConnection;
  private readonly string _topicA = "topic-00";  // Generic topics (Azure Service Bus emulator compatibility)
  private readonly string _topicB = "topic-01";
  private readonly int _batchIndex;
  private bool _isInitialized;
  private readonly Guid _testPollerInstanceId = Uuid7.NewUuid7().ToGuid();
  private readonly ServiceBusClient _sharedServiceBusClient;  // Shared client for test operations
  private readonly PostgreSqlContainer _postgresContainer;  // TestContainers PostgreSQL

  // Per-test resources (created during InitializeAsync)
  private IHost? _inventoryHost;
  private IHost? _bffHost;
  private IServiceScope? _inventoryScope;
  private IServiceScope? _bffScope;
  private string? _postgresConnection;

  /// <summary>
  /// Creates a new fixture instance that will create PostgreSQL (TestContainers) and service hosts per-test.
  /// Uses a SHARED ServiceBusClient that all tests and hosts reuse (to stay under connection quota).
  /// </summary>
  /// <param name="serviceBusConnectionString">The ServiceBus connection string (from pre-created emulator)</param>
  /// <param name="sharedServiceBusClient">The SHARED ServiceBusClient that all hosts will reuse</param>
  /// <param name="batchIndex">The batch index for diagnostic logging (always 0)</param>
  public ServiceBusIntegrationFixture(
    string serviceBusConnectionString,
    ServiceBusClient sharedServiceBusClient,
    int batchIndex
  ) {
    _serviceBusConnection = serviceBusConnectionString;
    _sharedServiceBusClient = sharedServiceBusClient ?? throw new ArgumentNullException(nameof(sharedServiceBusClient));
    _batchIndex = batchIndex;

    // Create TestContainers PostgreSQL (proven reliable)
    _postgresContainer = new PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
      .WithDatabase("whizbang_integration_test")
      .WithUsername("whizbang_user")
      .WithPassword("whizbang_pass")
      .Build();

    Console.WriteLine($"[ServiceBusFixture] Using topics: {_topicA}, {_topicB}");
    Console.WriteLine("[ServiceBusFixture] Using SHARED ServiceBusClient (reused by all hosts)");
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
  /// Initializes the test fixture by creating PostgreSQL container (TestContainers) and service hosts.
  /// ServiceBus emulator is already pre-created via SharedFixtureSource.
  /// </summary>
  [RequiresDynamicCode("EF Core in tests may use dynamic code")]
  [RequiresUnreferencedCode("EF Core in tests may use unreferenced code")]
  public async Task InitializeAsync(CancellationToken cancellationToken = default) {
    if (_isInitialized) {
      return;
    }

    Console.WriteLine($"[ServiceBusFixture] Initializing for topics: {_topicA}, {_topicB}");

    // Start TestContainers PostgreSQL (reliable, fast)
    Console.WriteLine("[ServiceBusFixture] Starting PostgreSQL container...");
    await _postgresContainer.StartAsync(cancellationToken);
    _postgresConnection = _postgresContainer.GetConnectionString();
    await _waitForPostgresReadyAsync(_postgresConnection, cancellationToken);
    Console.WriteLine("[ServiceBusFixture] PostgreSQL ready.");

    // NOTE: Skip warmup - SharedFixtureSource already warmed up the emulator
    // No need for 2-second delay since emulator is already initialized

    // Drain stale messages from ServiceBus subscriptions
    Console.WriteLine("[ServiceBusFixture] Draining stale messages from subscriptions...");
    await DrainSubscriptionsAsync(cancellationToken);
    Console.WriteLine("[ServiceBusFixture] Subscriptions drained.");

    // Create service hosts (InventoryWorker + BFF)
    // IMPORTANT: Do NOT start hosts yet - schema must be initialized first!
    Console.WriteLine("[ServiceBusFixture] Creating service hosts...");
    _inventoryHost = CreateInventoryHost(_postgresConnection, _serviceBusConnection, _topicA, _topicB);
    _bffHost = CreateBffHost(_postgresConnection, _serviceBusConnection, _topicA, _topicB);

    // Initialize Whizbang database schema (create tables, functions, etc.)
    // CRITICAL: Must run BEFORE starting hosts, otherwise workers fail trying to call process_work_batch
    Console.WriteLine("[ServiceBusFixture] Initializing database schema...");
    using (var initScope = _inventoryHost.Services.CreateScope()) {
      var inventoryDbContext = initScope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();
      var logger = initScope.ServiceProvider.GetRequiredService<ILogger<ServiceBusIntegrationFixture>>();
      await inventoryDbContext.EnsureWhizbangDatabaseInitializedAsync(logger, cancellationToken);
    }
    using (var initScope = _bffHost.Services.CreateScope()) {
      var bffDbContext = initScope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();
      var logger = initScope.ServiceProvider.GetRequiredService<ILogger<ServiceBusIntegrationFixture>>();
      await bffDbContext.EnsureWhizbangDatabaseInitializedAsync(logger, cancellationToken);
    }
    Console.WriteLine("[ServiceBusFixture] Database schema initialized.");

    // Register message associations for perspective auto-checkpoint creation
    // CRITICAL: Must run AFTER schema initialization (tables exist) and BEFORE starting hosts (workers need associations)
    Console.WriteLine("[ServiceBusFixture] Registering message associations...");
    using (var initScope = _inventoryHost.Services.CreateScope()) {
      var inventoryDbContext = initScope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();
      var logger = initScope.ServiceProvider.GetRequiredService<ILogger<ServiceBusIntegrationFixture>>();

      await ECommerce.InventoryWorker.Generated.PerspectiveRegistrationExtensions.RegisterPerspectiveAssociationsAsync(
        inventoryDbContext,
        schema: "inventory",
        serviceName: "ECommerce.InventoryWorker",
        logger: logger,
        cancellationToken: cancellationToken
      );

      Console.WriteLine("[ServiceBusFixture] InventoryWorker message associations registered (inventory schema)");
    }
    using (var initScope = _bffHost.Services.CreateScope()) {
      var bffDbContext = initScope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();
      var logger = initScope.ServiceProvider.GetRequiredService<ILogger<ServiceBusIntegrationFixture>>();

      await ECommerce.BFF.API.Generated.PerspectiveRegistrationExtensions.RegisterPerspectiveAssociationsAsync(
        bffDbContext,
        schema: "bff",
        serviceName: "ECommerce.BFF.API",
        logger: logger,
        cancellationToken: cancellationToken
      );

      Console.WriteLine("[ServiceBusFixture] BFF message associations registered (bff schema)");
    }
    Console.WriteLine("[ServiceBusFixture] Message associations registered.");

    // Start hosts AFTER schema is ready
    Console.WriteLine("[ServiceBusFixture] Starting service hosts...");
    await _inventoryHost.StartAsync(cancellationToken);
    await _bffHost.StartAsync(cancellationToken);
    Console.WriteLine("[ServiceBusFixture] Service hosts started.");

    // Create long-lived scopes for lenses
    _inventoryScope = _inventoryHost.Services.CreateScope();
    _bffScope = _bffHost.Services.CreateScope();

    Console.WriteLine("[ServiceBusFixture] Service hosts ready.");

    // Clean up any stale data
    Console.WriteLine("[ServiceBusFixture] Cleaning database for test isolation...");
    await CleanupDatabaseAsync(cancellationToken);
    Console.WriteLine("[ServiceBusFixture] Database cleaned.");

    Console.WriteLine("[ServiceBusFixture] Ready for test execution!");

    _isInitialized = true;
  }

  /// <summary>
  /// Waits for PostgreSQL to be ready by attempting to connect until successful.
  /// </summary>
  private async Task _waitForPostgresReadyAsync(string connectionString, CancellationToken cancellationToken = default) {
    var maxAttempts = 30;
    var delay = TimeSpan.FromSeconds(1);

    for (var attempt = 1; attempt <= maxAttempts; attempt++) {
      try {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        Console.WriteLine($"[ServiceBusFixture] PostgreSQL ready after {attempt} attempt(s)");
        return;
      } catch (Npgsql.NpgsqlException) when (attempt < maxAttempts) {
        await Task.Delay(delay, cancellationToken);
      }
    }

    throw new InvalidOperationException($"PostgreSQL did not become ready after {maxAttempts} attempts");
  }

  /// <summary>
  /// Drains all messages from assigned subscriptions to ensure clean state for test.
  /// Critical for test isolation when using shared generic topics across test runs.
  /// Uses the shared ServiceBusClient to avoid creating extra connections.
  /// </summary>
  private async Task DrainSubscriptionsAsync(CancellationToken cancellationToken = default) {
    // Use shared client instead of creating a new one
    // This reduces connection count and avoids ConnectionsQuotaExceeded errors

    // Drain subscriptions for BFF only (InventoryWorker builds perspectives internally)
    var subscriptions = new[] {
      (_topicA, "sub-00-a"),  // topic-00 subscription
      (_topicB, "sub-01-a")   // topic-01 subscription
    };

    foreach (var (topic, subscription) in subscriptions) {
      var receiver = _sharedServiceBusClient.CreateReceiver(topic, subscription);
      var drained = 0;
      var consecutiveEmptyPolls = 0;

      // Drain until we see 3 consecutive empty polls (ensures messages settle)
      while (consecutiveEmptyPolls < 3) {
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
        if (message == null) {
          consecutiveEmptyPolls++;
          await Task.Delay(50, cancellationToken);  // Brief pause between polls
          continue;
        }

        consecutiveEmptyPolls = 0;  // Reset counter when message found
        await receiver.CompleteMessageAsync(message, cancellationToken);
        drained++;

        // Safety limit - don't drain forever
        if (drained >= 200) {
          Console.WriteLine($"  [Drain] ⚠️  {topic}/{subscription}: Hit safety limit (200 messages)");
          break;
        }
      }

      await receiver.DisposeAsync();

      Console.WriteLine($"  [Drain] {topic}/{subscription}: {drained} messages drained");
    }
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

    // Register EF Core DbContext with NpgsqlDataSource (required for EnableDynamicJson)
    // IMPORTANT: ConfigureJsonOptions() MUST be called BEFORE EnableDynamicJson() (Npgsql bug #5562)
    // This registers JSON converters for JSONB serialization (including EnvelopeMetadata, MessageScope)
    var inventoryDataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(postgresConnectionString);
    inventoryDataSourceBuilder.ConfigureJsonOptions(jsonOptions);
    inventoryDataSourceBuilder.EnableDynamicJson();
    var inventoryDataSource = inventoryDataSourceBuilder.Build();
    builder.Services.AddSingleton(inventoryDataSource);

    builder.Services.AddDbContext<ECommerce.InventoryWorker.InventoryDbContext>(options => {
      options.UseNpgsql(inventoryDataSource);
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

    // Register topic routing strategy for Azure Service Bus emulator compatibility
    // Maps all events to generic topics (topic-00, topic-01) instead of named topics (products, inventory)
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRoutingStrategy>(
      new GenericTopicRoutingStrategy(topicCount: 2)
    );

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

    // InventoryWorker builds perspectives from local event store - no event topic subscriptions needed
    // Inbox subscription would be configured here if testing command routing, but
    // integration tests use direct receptor invocation instead

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

    // Register background workers
    builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();
    builder.Services.AddHostedService<PerspectiveWorker>();  // Processes perspective checkpoints

    // NOTE: InventoryWorker does NOT use ServiceBusConsumerWorker
    // InventoryWorker perspectives read directly from inventory.wh_event_store (event sourcing pattern)
    // Only BFF uses ServiceBusConsumerWorker to receive events via Service Bus topics

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

    // Register EF Core DbContext with NpgsqlDataSource (required for EnableDynamicJson)
    // IMPORTANT: ConfigureJsonOptions() MUST be called BEFORE EnableDynamicJson() (Npgsql bug #5562)
    // This registers JSON converters for JSONB serialization (including EnvelopeMetadata, MessageScope)
    var bffDataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(postgresConnectionString);
    bffDataSourceBuilder.ConfigureJsonOptions(jsonOptions);
    bffDataSourceBuilder.EnableDynamicJson();
    var bffDataSource = bffDataSourceBuilder.Build();
    builder.Services.AddSingleton(bffDataSource);

    builder.Services.AddDbContext<ECommerce.BFF.API.BffDbContext>(options =>
      options.UseNpgsql(bffDataSource));

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

    // Azure Service Bus consumer with generic topic subscriptions (emulator compatibility)
    // BFF subscribes to generic topics with generic subscriptions (sub-00-a, sub-01-a)
    var consumerOptions = new ServiceBusConsumerOptions();
    consumerOptions.Subscriptions.Add(new TopicSubscription(topicA, "sub-00-a"));  // topic-00 subscription
    consumerOptions.Subscriptions.Add(new TopicSubscription(topicB, "sub-01-a"));  // topic-01 subscription
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
      using var inventoryScope = _inventoryHost!.Services.CreateScope();
      var inventoryDbContext = inventoryScope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();

      // Query database directly for any uncompleted work using ADO.NET
      var inventoryConnection = inventoryDbContext.Database.GetDbConnection();
      if (inventoryConnection.State != System.Data.ConnectionState.Open) {
        await inventoryConnection.OpenAsync();
      }

      await using var invCmd = inventoryConnection.CreateCommand();

      // INVENTORY SCHEMA: Check outbox, inbox, perspectives
      invCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_outbox WHERE (status & 4) = 0";
      var invPendingOutbox = (int)(await invCmd.ExecuteScalarAsync() ?? 0);

      invCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_inbox WHERE (status & 2) = 0";
      var invPendingInbox = (int)(await invCmd.ExecuteScalarAsync() ?? 0);

      invCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_perspective_checkpoints WHERE (status & 2) = 0 AND (status & 4) = 0";
      var invPendingPerspectives = (int)(await invCmd.ExecuteScalarAsync() ?? 0);

      invCmd.CommandText = @"
        SELECT CAST(COUNT(*) AS INTEGER) FROM (
          SELECT id FROM inventory.wh_per_product_dto
          UNION ALL
          SELECT id FROM inventory.wh_per_inventory_level_dto
        ) AS all_perspective_rows";
      var invPerspectiveRowCount = (int)(await invCmd.ExecuteScalarAsync() ?? 0);

      // BFF SCHEMA: Check outbox, inbox, perspectives
      using var bffScope = _bffHost!.Services.CreateScope();
      var bffDbContext = bffScope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();

      var bffConnection = bffDbContext.Database.GetDbConnection();
      if (bffConnection.State != System.Data.ConnectionState.Open) {
        await bffConnection.OpenAsync();
      }

      await using var bffCmd = bffConnection.CreateCommand();

      bffCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM bff.wh_outbox WHERE (status & 4) = 0";
      var bffPendingOutbox = (int)(await bffCmd.ExecuteScalarAsync() ?? 0);

      bffCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM bff.wh_inbox WHERE (status & 2) = 0";
      var bffPendingInbox = (int)(await bffCmd.ExecuteScalarAsync() ?? 0);

      bffCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM bff.wh_perspective_checkpoints WHERE (status & 2) = 0 AND (status & 4) = 0";
      var bffPendingPerspectives = (int)(await bffCmd.ExecuteScalarAsync() ?? 0);

      bffCmd.CommandText = @"
        SELECT CAST(COUNT(*) AS INTEGER) FROM (
          SELECT id FROM bff.wh_per_product_dto
          UNION ALL
          SELECT id FROM bff.wh_per_inventory_level_dto
        ) AS all_perspective_rows";
      var bffPerspectiveRowCount = (int)(await bffCmd.ExecuteScalarAsync() ?? 0);

      // Aggregate totals
      var pendingOutbox = invPendingOutbox + bffPendingOutbox;
      var pendingInbox = invPendingInbox + bffPendingInbox;
      var pendingPerspectives = invPendingPerspectives + bffPendingPerspectives;
      var perspectiveRowCount = invPerspectiveRowCount + bffPerspectiveRowCount;

      // DIAGNOSTIC: Log initial state and checkpoint details for BOTH schemas
      if (attempt == 1) {
        Console.WriteLine($"[WaitForEvents] Initial state: Inventory(O={invPendingOutbox},I={invPendingInbox},P={invPendingPerspectives},Rows={invPerspectiveRowCount}), BFF(O={bffPendingOutbox},I={bffPendingInbox},P={bffPendingPerspectives},Rows={bffPerspectiveRowCount})");

        invCmd.CommandText = @"
          SELECT
            perspective_name,
            stream_id::text,
            status,
            COALESCE(last_event_id::text, 'NULL') as last_event_id,
            COALESCE(error, 'NULL') as error
          FROM inventory.wh_perspective_checkpoints
          LIMIT 10";
        await using var invReader = await invCmd.ExecuteReaderAsync();
        Console.WriteLine("[DIAGNOSTIC] Inventory perspective checkpoints:");
        while (await invReader.ReadAsync()) {
          Console.WriteLine($"  - {invReader.GetString(0)}, stream={invReader.GetString(1)}, status={invReader.GetInt32(2)}, last_event={invReader.GetString(3)}, error={invReader.GetString(4)}");
        }

        bffCmd.CommandText = @"
          SELECT
            perspective_name,
            stream_id::text,
            status,
            COALESCE(last_event_id::text, 'NULL') as last_event_id,
            COALESCE(error, 'NULL') as error
          FROM bff.wh_perspective_checkpoints
          LIMIT 10";
        await using var bffReader = await bffCmd.ExecuteReaderAsync();
        Console.WriteLine("[DIAGNOSTIC] BFF perspective checkpoints:");
        while (await bffReader.ReadAsync()) {
          Console.WriteLine($"  - {bffReader.GetString(0)}, stream={bffReader.GetString(1)}, status={bffReader.GetInt32(2)}, last_event={bffReader.GetString(3)}, error={bffReader.GetString(4)}");
        }
        await bffReader.CloseAsync();

        // DIAGNOSTIC: Check BFF inbox messages (after they should have been processed)
        // Wait a bit for messages to arrive
        await Task.Delay(500);

        bffCmd.CommandText = @"
          SELECT
            message_id::text,
            handler_name,
            event_type,
            is_event,
            COALESCE(stream_id::text, 'NULL') as stream_id,
            status,
            processed_at IS NOT NULL as is_processed
          FROM bff.wh_inbox
          ORDER BY received_at DESC
          LIMIT 5";
        await using var bffInboxReader = await bffCmd.ExecuteReaderAsync();
        Console.WriteLine("[DIAGNOSTIC] BFF inbox messages:");
        var inboxCount = 0;
        while (await bffInboxReader.ReadAsync()) {
          inboxCount++;
          var msgId = bffInboxReader.GetString(0);
          var eventType = bffInboxReader.GetString(2);
          var isEvent = bffInboxReader.GetBoolean(3);
          var streamId = bffInboxReader.GetString(4);
          var status = bffInboxReader.GetInt32(5);
          Console.WriteLine($"  - msg={msgId[..8]}..., event_type={eventType}, is_event={isEvent}, stream_id={streamId[..8]}..., status={status}");
        }
        if (inboxCount == 0) {
          Console.WriteLine("  (no messages found)");
        }
        await bffInboxReader.CloseAsync();

        // DIAGNOSTIC: Check BFF event store
        bffCmd.CommandText = @"
          SELECT CAST(COUNT(*) AS INTEGER) FROM bff.wh_event_store";
        var bffEventCount = (int)(await bffCmd.ExecuteScalarAsync() ?? 0);
        Console.WriteLine($"[DIAGNOSTIC] BFF event store: {bffEventCount} events");

        // DIAGNOSTIC: Check what event types are in the BFF event store
        if (bffEventCount > 0) {
          bffCmd.CommandText = @"
            SELECT
              event_id::text,
              event_type,
              aggregate_type
            FROM bff.wh_event_store
            ORDER BY sequence_number
            LIMIT 5";
          await using var bffEventReader = await bffCmd.ExecuteReaderAsync();
          Console.WriteLine("[DIAGNOSTIC] BFF event store event types:");
          while (await bffEventReader.ReadAsync()) {
            var eventId = bffEventReader.GetString(0);
            var eventType = bffEventReader.GetString(1);
            var aggregateType = bffEventReader.GetString(2);
            Console.WriteLine($"  - event_id={eventId[..8]}..., event_type={eventType}, aggregate_type={aggregateType}");
          }
          await bffEventReader.CloseAsync();
        }

        // DIAGNOSTIC: Check BFF message associations using C# API (NEW!)
        var bffAssociations = ECommerce.BFF.API.Generated.PerspectiveRegistrationExtensions
          .GetMessageAssociations("ECommerce.BFF.API");
        Console.WriteLine("[DIAGNOSTIC] BFF message associations (from C# API):");
        foreach (var assoc in bffAssociations.OrderBy(a => a.TargetName).ThenBy(a => a.MessageType)) {
          Console.WriteLine($"  - {assoc.MessageType} → {assoc.TargetName}");
        }
        Console.WriteLine($"[DIAGNOSTIC] Total BFF associations: {bffAssociations.Count}");

        // DIAGNOSTIC: Show fuzzy matching with new API features
        Console.WriteLine();
        Console.WriteLine("[DIAGNOSTIC] === NEW FUZZY MATCHING API FEATURES ===");

        // Example 1: Simple name matching (ignore namespace/assembly)
        var perspectivesSimpleName = ECommerce.BFF.API.Generated.PerspectiveRegistrationExtensions
          .GetPerspectivesForEvent("ProductCreatedEvent", "ECommerce.BFF.API", Whizbang.Core.MatchStrictness.SimpleName);
        Console.WriteLine($"[DIAGNOSTIC] Simple name 'ProductCreatedEvent' → {string.Join(", ", perspectivesSimpleName)}");

        // Example 2: Case-insensitive simple name matching
        var perspectivesCaseInsensitive = ECommerce.BFF.API.Generated.PerspectiveRegistrationExtensions
          .GetPerspectivesForEvent("productcreatedevent", "ECommerce.BFF.API", Whizbang.Core.MatchStrictness.SimpleNameCaseInsensitive);
        Console.WriteLine($"[DIAGNOSTIC] Case-insensitive 'productcreatedevent' → {string.Join(", ", perspectivesCaseInsensitive)}");

        // Example 3: Regex pattern matching (any event with "Product" in the name)
        var perspectivesPattern = ECommerce.BFF.API.Generated.PerspectiveRegistrationExtensions
          .GetPerspectivesForEvent(new System.Text.RegularExpressions.Regex(".*Product.*"), "ECommerce.BFF.API");
        Console.WriteLine($"[DIAGNOSTIC] Regex pattern '.*Product.*' → {string.Join(", ", perspectivesPattern)}");

        // Example 4: Get events handled by a perspective (fuzzy)
        var eventsForInventory = ECommerce.BFF.API.Generated.PerspectiveRegistrationExtensions
          .GetEventsForPerspective("InventoryPerspective", "ECommerce.BFF.API", Whizbang.Core.MatchStrictness.SimpleName);
        Console.WriteLine($"[DIAGNOSTIC] Events handled by InventoryPerspective: {string.Join(", ", eventsForInventory)}");

        Console.WriteLine("[DIAGNOSTIC] === END FUZZY MATCHING DEMO ===");
        Console.WriteLine();

        // DIAGNOSTIC: Show which perspectives handle ProductCreatedEvent (exact match - original)
        var perspectivesForProduct = ECommerce.BFF.API.Generated.PerspectiveRegistrationExtensions
          .GetPerspectivesForEvent("ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts", "ECommerce.BFF.API");
        Console.WriteLine($"[DIAGNOSTIC] Perspectives handling ProductCreatedEvent (exact): {string.Join(", ", perspectivesForProduct)}");

        // DIAGNOSTIC: Check which perspective checkpoints exist in BFF
        bffCmd.CommandText = @"
          SELECT perspective_name, status, last_event_id::text
          FROM bff.wh_perspective_checkpoints
          ORDER BY perspective_name";
        await using var bffCheckpointReader = await bffCmd.ExecuteReaderAsync();
        Console.WriteLine("[DIAGNOSTIC] BFF perspective checkpoints:");
        while (await bffCheckpointReader.ReadAsync()) {
          var perspName = bffCheckpointReader.GetString(0);
          var status = bffCheckpointReader.GetInt32(1);
          var lastEventId = bffCheckpointReader.IsDBNull(2) ? "null" : bffCheckpointReader.GetString(2);
          Console.WriteLine($"  - {perspName}: status={status}, last_event_id={lastEventId[..8]}...");
        }
        await bffCheckpointReader.CloseAsync();

        // DIAGNOSTIC: Check row counts for each BFF perspective table
        bffCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM bff.wh_per_product_dto";
        var bffProductRows = (int)(await bffCmd.ExecuteScalarAsync() ?? 0);
        bffCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM bff.wh_per_inventory_level_dto";
        var bffInventoryRows = (int)(await bffCmd.ExecuteScalarAsync() ?? 0);
        Console.WriteLine($"[DIAGNOSTIC] BFF perspective rows: ProductDto={bffProductRows}, InventoryLevelDto={bffInventoryRows}");
      }

      // CRITICAL: Wait for all work to complete AND for perspective rows to exist
      // This prevents returning before perspective transactions are visible to lens queries
      if (pendingOutbox == 0 && pendingInbox == 0 && pendingPerspectives == 0 && perspectiveRowCount > 0) {
        Console.WriteLine($"[ServiceBusFixture] Event processing complete - no pending work, {perspectiveRowCount} perspective rows exist (checked database after {stopwatch.ElapsedMilliseconds}ms, {attempt} attempts)");
        Console.WriteLine($"[DIAGNOSTIC-FINAL] BFF: Rows={bffPerspectiveRowCount}, Inventory: Rows={invPerspectiveRowCount}");
        return;
      }

      // Log progress every 3 attempts (~1-2 seconds) for faster feedback
      if (attempt % 3 == 0) {
        Console.WriteLine($"[WaitForEvents] Still waiting: Outbox={pendingOutbox}, Inbox={pendingInbox}, Perspectives={pendingPerspectives}, PerspectiveRows={perspectiveRowCount} (attempt {attempt}, elapsed: {stopwatch.ElapsedMilliseconds}ms)");
      }

      // Progressive backoff: start at 100ms, increase to 2000ms
      var delay = Math.Min(100 + (attempt * 100), 2000);
      await Task.Delay(delay);
    }

    // Timeout reached - log final state with batch info for BOTH schemas
    Console.WriteLine($"[ServiceBusFixture] WARNING: Event processing did not complete within {timeoutMilliseconds}ms timeout (Batch {_batchIndex}, Topics {_topicA}/{_topicB})");

    using var finalInvScope = _inventoryHost!.Services.CreateScope();
    var finalInvDbContext = finalInvScope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();

    var finalInvConnection = finalInvDbContext.Database.GetDbConnection();
    if (finalInvConnection.State != System.Data.ConnectionState.Open) {
      await finalInvConnection.OpenAsync();
    }

    await using var finalInvCmd = finalInvConnection.CreateCommand();

    finalInvCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_outbox WHERE (status & 4) = 0";
    var finalInvOutbox = (int)(await finalInvCmd.ExecuteScalarAsync() ?? 0);

    finalInvCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_inbox WHERE (status & 2) = 0";
    var finalInvInbox = (int)(await finalInvCmd.ExecuteScalarAsync() ?? 0);

    finalInvCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_perspective_checkpoints WHERE (status & 2) = 0 AND (status & 4) = 0";
    var finalInvPerspectives = (int)(await finalInvCmd.ExecuteScalarAsync() ?? 0);

    using var finalBffScope = _bffHost!.Services.CreateScope();
    var finalBffDbContext = finalBffScope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();

    var finalBffConnection = finalBffDbContext.Database.GetDbConnection();
    if (finalBffConnection.State != System.Data.ConnectionState.Open) {
      await finalBffConnection.OpenAsync();
    }

    await using var finalBffCmd = finalBffConnection.CreateCommand();

    finalBffCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM bff.wh_outbox WHERE (status & 4) = 0";
    var finalBffOutbox = (int)(await finalBffCmd.ExecuteScalarAsync() ?? 0);

    finalBffCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM bff.wh_inbox WHERE (status & 2) = 0";
    var finalBffInbox = (int)(await finalBffCmd.ExecuteScalarAsync() ?? 0);

    finalBffCmd.CommandText = "SELECT CAST(COUNT(*) AS INTEGER) FROM bff.wh_perspective_checkpoints WHERE (status & 2) = 0 AND (status & 4) = 0";
    var finalBffPerspectives = (int)(await finalBffCmd.ExecuteScalarAsync() ?? 0);

    Console.WriteLine($"[ServiceBusFixture] Final state - Batch {_batchIndex}, Topics {_topicA}/{_topicB}: Inventory(O={finalInvOutbox},I={finalInvInbox},P={finalInvPerspectives}), BFF(O={finalBffOutbox},I={finalBffInbox},P={finalBffPerspectives})");
  }

  /// <summary>
  /// Cleans up all test data from the database (truncates all tables).
  /// Also drains Service Bus subscriptions to prevent cross-contamination.
  /// Call this between test classes to ensure isolation.
  /// Gracefully handles the case where the database container has already stopped.
  /// </summary>
  public async Task CleanupDatabaseAsync(CancellationToken cancellationToken = default) {
    if (!_isInitialized) {
      return;
    }

    // Drain Service Bus subscriptions FIRST to prevent cross-contamination
    Console.WriteLine("[ServiceBusFixture] Draining subscriptions between tests...");
    await DrainSubscriptionsAsync(cancellationToken);
    Console.WriteLine("[ServiceBusFixture] Subscriptions drained.");

    // Truncate all Whizbang tables in the shared database
    // Both InventoryWorker and BFF share the same database, so we only need to truncate once
    // Gracefully handle connection failures (container may have stopped after test completion)
    try {
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
            TRUNCATE TABLE inventory.wh_event_store, inventory.wh_outbox, inventory.wh_inbox, inventory.wh_perspective_checkpoints, inventory.wh_receptor_processing, inventory.wh_active_streams CASCADE;

            -- Truncate all perspective tables (INVENTORY schema)
            TRUNCATE TABLE inventory.wh_per_inventory_level_dto CASCADE;
            TRUNCATE TABLE inventory.wh_per_order_read_model CASCADE;
            TRUNCATE TABLE inventory.wh_per_product_dto CASCADE;

            -- Truncate core infrastructure tables (BFF schema)
            TRUNCATE TABLE bff.wh_event_store, bff.wh_outbox, bff.wh_inbox, bff.wh_perspective_checkpoints, bff.wh_receptor_processing, bff.wh_active_streams CASCADE;

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
    } catch (Npgsql.NpgsqlException ex) when (ex.Message.Contains("Failed to connect")) {
      // Database container has been stopped - this is expected during test teardown
      // Silently ignore connection failures since cleanup is not critical after tests complete
      Console.WriteLine("[ServiceBusFixture] Database cleanup skipped - container already stopped");
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

    // Query registered message associations
    await using (var cmd = connection.CreateCommand()) {
      cmd.CommandText = "SELECT DISTINCT message_type, target_name FROM inventory.wh_message_associations ORDER BY message_type, target_name LIMIT 50";
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      var count = 0;
      output.AppendLine("[DIAGNOSTIC] Registered message associations:");
      Console.WriteLine("[DIAGNOSTIC] Registered message associations:");
      while (await reader.ReadAsync(cancellationToken)) {
        var messageType = reader.GetString(0);
        var targetName = reader.GetString(1);
        output.AppendLine($"[DIAGNOSTIC]   message_type: '{messageType}' -> target: '{targetName}'");
        Console.WriteLine($"[DIAGNOSTIC]   message_type: '{messageType}' -> target: '{targetName}'");
        count++;
      }
      output.AppendLine($"[DIAGNOSTIC] Found {count} message associations");
      Console.WriteLine($"[DIAGNOSTIC] Found {count} message associations");
    }

    output.AppendLine("[DIAGNOSTIC] ================================");
    Console.WriteLine("[DIAGNOSTIC] ================================");
  }

  /// <summary>
  /// Waits for all pending work (outbox, inbox, perspectives) to complete in BOTH schemas before shutdown.
  /// This prevents in-flight operations from being canceled mid-transaction.
  /// </summary>
  private async Task WaitForPendingWorkAsync(int timeoutMilliseconds = 5000) {
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var attempt = 0;

    while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds) {
      attempt++;

      // Check inventory schema
      using var inventoryScope = _inventoryHost!.Services.CreateScope();
      var inventoryDbContext = inventoryScope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();
      var inventoryConn = inventoryDbContext.Database.GetDbConnection();
      if (inventoryConn.State != System.Data.ConnectionState.Open) {
        await inventoryConn.OpenAsync();
      }

      await using var invCmd = inventoryConn.CreateCommand();
      invCmd.CommandText = @"
        SELECT
          (SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_outbox WHERE (status & 4) = 0) as outbox,
          (SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_inbox WHERE (status & 2) = 0) as inbox,
          (SELECT CAST(COUNT(*) AS INTEGER) FROM inventory.wh_perspective_checkpoints WHERE (status & 2) = 0 AND (status & 4) = 0) as perspectives";
      await using var invReader = await invCmd.ExecuteReaderAsync();
      await invReader.ReadAsync();
      var invOutbox = invReader.GetInt32(0);
      var invInbox = invReader.GetInt32(1);
      var invPerspectives = invReader.GetInt32(2);

      // Check BFF schema
      using var bffScope = _bffHost!.Services.CreateScope();
      var bffDbContext = bffScope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();
      var bffConn = bffDbContext.Database.GetDbConnection();
      if (bffConn.State != System.Data.ConnectionState.Open) {
        await bffConn.OpenAsync();
      }

      await using var bffCmd = bffConn.CreateCommand();
      bffCmd.CommandText = @"
        SELECT
          (SELECT CAST(COUNT(*) AS INTEGER) FROM bff.wh_outbox WHERE (status & 4) = 0) as outbox,
          (SELECT CAST(COUNT(*) AS INTEGER) FROM bff.wh_inbox WHERE (status & 2) = 0) as inbox,
          (SELECT CAST(COUNT(*) AS INTEGER) FROM bff.wh_perspective_checkpoints WHERE (status & 2) = 0 AND (status & 4) = 0) as perspectives";
      await using var bffReader = await bffCmd.ExecuteReaderAsync();
      await bffReader.ReadAsync();
      var bffOutbox = bffReader.GetInt32(0);
      var bffInbox = bffReader.GetInt32(1);
      var bffPerspectives = bffReader.GetInt32(2);

      var totalPending = invOutbox + invInbox + invPerspectives + bffOutbox + bffInbox + bffPerspectives;

      if (totalPending == 0) {
        Console.WriteLine($"[ServiceBusFixture] All pending work completed after {stopwatch.ElapsedMilliseconds}ms ({attempt} attempts)");
        return;
      }

      if (attempt % 2 == 0) {
        Console.WriteLine($"[ServiceBusFixture] Waiting for work: Inventory(O={invOutbox},I={invInbox},P={invPerspectives}), BFF(O={bffOutbox},I={bffInbox},P={bffPerspectives}) - {stopwatch.ElapsedMilliseconds}ms elapsed");
      }

      await Task.Delay(200);  // Check every 200ms
    }

    Console.WriteLine($"[ServiceBusFixture] Warning: Pending work timeout after {timeoutMilliseconds}ms");
  }

  public async ValueTask DisposeAsync() {
    // CRITICAL: Wait for all pending work to complete before stopping hosts
    // This prevents in-flight perspective materialization from being canceled mid-transaction
    if (_isInitialized && _inventoryHost != null) {
      try {
        Console.WriteLine("[ServiceBusFixture] Waiting for pending work to complete before shutdown...");
        await WaitForPendingWorkAsync(timeoutMilliseconds: 5000);
        Console.WriteLine("[ServiceBusFixture] All pending work completed.");
      } catch (Exception ex) {
        Console.WriteLine($"[ServiceBusFixture] Warning: Pre-shutdown wait encountered error (non-critical): {ex.Message}");
      }
    }

    // Dispose scopes
    _inventoryScope?.Dispose();
    _bffScope?.Dispose();

    // Stop and dispose hosts (this will close ServiceBus processors/receivers)
    if (_inventoryHost != null) {
      await _inventoryHost.StopAsync(TimeSpan.FromSeconds(10));  // Increased timeout for graceful shutdown
      _inventoryHost.Dispose();
    }

    if (_bffHost != null) {
      await _bffHost.StopAsync(TimeSpan.FromSeconds(10));  // Increased timeout for graceful shutdown
      _bffHost.Dispose();
    }

    // CRITICAL: Wait for AMQP connections to fully close
    // ServiceBus processors dispose asynchronously, and connections need time to clean up
    // Without this delay, connections accumulate and exceed emulator quota (~25)
    Console.WriteLine("[ServiceBusFixture] Waiting for ServiceBus connections to close...");
    await Task.Delay(2000);  // 2 second delay for connection cleanup
    Console.WriteLine("[ServiceBusFixture] ServiceBus connections closed.");

    // Dispose TestContainers PostgreSQL (instead of Aspire app)
    if (_postgresContainer != null) {
      await _postgresContainer.DisposeAsync();
    }

    // DO NOT dispose shared ServiceBusClient here - it's owned by SharedFixtureSource
    // and should only be disposed once when ALL tests complete
    // Stopping the hosts above closes all ServiceBus processors/receivers properly
  }
}
