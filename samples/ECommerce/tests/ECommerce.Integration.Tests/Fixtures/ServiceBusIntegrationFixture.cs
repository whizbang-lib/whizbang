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
    // Enable logging to capture RAISE NOTICE statements from process_work_batch Phase 4.5B
    _postgresContainer = new PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
      .WithDatabase("whizbang_integration_test")
      .WithUsername("whizbang_user")
      .WithPassword("whizbang_pass")
      .WithCommand("-c", "log_min_messages=NOTICE", "-c", "client_min_messages=NOTICE", "-c", "log_statement=all")
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
    await _drainSubscriptionsAsync(cancellationToken);
    Console.WriteLine("[ServiceBusFixture] Subscriptions drained.");

    // Create service hosts (InventoryWorker + BFF)
    // IMPORTANT: Do NOT start hosts yet - schema must be initialized first!
    Console.WriteLine("[ServiceBusFixture] Creating service hosts...");
    _inventoryHost = _createInventoryHost(_postgresConnection, _serviceBusConnection, _topicA, _topicB);
    _bffHost = _createBffHost(_postgresConnection, _serviceBusConnection, _topicA, _topicB);

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

    // Wait for ServiceBusConsumerWorker to fully subscribe to topics
    // StartAsync returns before background services are fully initialized
    // This ensures consumer workers are ready to receive messages before tests run
    Console.WriteLine("[ServiceBusFixture] Waiting for consumer workers to subscribe...");
    await Task.Delay(3000, cancellationToken); // 3 seconds for subscription setup
    Console.WriteLine("[ServiceBusFixture] Consumer workers ready.");

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
  private async Task _drainSubscriptionsAsync(CancellationToken cancellationToken = default) {
    // Use shared client instead of creating a new one
    // This reduces connection count and avoids ConnectionsQuotaExceeded errors

    // Drain subscriptions for both BFF and InventoryWorker
    var subscriptions = new[] {
      (_topicA, "sub-00-a"),  // topic-00 subscription (BFF)
      (_topicB, "sub-01-a"),  // topic-01 subscription (BFF)
      (_topicA, "sub-00-b"),  // topic-00 subscription (InventoryWorker)
      (_topicB, "sub-01-b")   // topic-01 subscription (InventoryWorker)
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
  private IHost _createInventoryHost(
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

    // Register OrderedStreamProcessor for message ordering (required by ServiceBusConsumerWorker)
    builder.Services.AddSingleton<OrderedStreamProcessor>();

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
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddWhizbangLifecycleInvoker(builder.Services);
    ECommerce.InventoryWorker.Generated.DispatcherRegistrations.AddWhizbangLifecycleMessageDeserializer(builder.Services);
    builder.Services.AddSingleton<Whizbang.Core.Messaging.ILifecycleReceptorRegistry, Whizbang.Core.Messaging.DefaultLifecycleReceptorRegistry>();
    builder.Services.AddSingleton<Whizbang.Core.Messaging.IEventTypeProvider, ECommerce.Contracts.ECommerceEventTypeProvider>();

    // Register perspective runners (generated by PerspectiveRunnerRegistryGenerator)
    ECommerce.InventoryWorker.Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(builder.Services);

    // Register concrete perspective types for runner resolution
    builder.Services.AddScoped<ECommerce.InventoryWorker.Perspectives.InventoryLevelsPerspective>();
    builder.Services.AddScoped<ECommerce.InventoryWorker.Perspectives.ProductCatalogPerspective>();

    // Register TopicRegistry to provide base topic names for events
    // Register as singleton INSTANCE instead of type registration
    var topicRegistryInstance = new ECommerce.Contracts.Generated.TopicRegistry();
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRegistry>(topicRegistryInstance);

    var testTopic = topicRegistryInstance.GetBaseTopic(typeof(ECommerce.Contracts.Events.ProductCreatedEvent));
    if (testTopic != "products") {
      throw new InvalidOperationException($"CRITICAL: TopicRegistry test failed - expected 'products', got '{testTopic}'");
    }

    // Register topic routing strategy for Azure Service Bus emulator compatibility
    // Maps all events to generic topics (topic-00, topic-01) instead of named topics (products, inventory)
    var routingStrategyInstance = new GenericTopicRoutingStrategy(topicCount: 2);
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRoutingStrategy>(routingStrategyInstance);

    var testRouted = routingStrategyInstance.ResolveTopic(typeof(ECommerce.Contracts.Events.ProductCreatedEvent), "products", null);
    if (!testRouted.StartsWith("topic-")) {
      throw new InvalidOperationException($"CRITICAL: GenericTopicRoutingStrategy test failed - expected 'topic-XX', got '{testRouted}'");
    }

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

    // Azure Service Bus consumer for InventoryWorker
    // CRITICAL: InventoryWorker MUST subscribe to receive its own published events to store them in local event store
    // Without this, events go to outbox → ServiceBus but never get stored to inventory.wh_event_store for perspectives
    var inventoryConsumerOptions = new ServiceBusConsumerOptions();
    inventoryConsumerOptions.Subscriptions.Add(new TopicSubscription(topicA, "sub-00-b"));  // topic-00 subscription
    inventoryConsumerOptions.Subscriptions.Add(new TopicSubscription(topicB, "sub-01-b"));  // topic-01 subscription
    builder.Services.AddSingleton(inventoryConsumerOptions);
    builder.Services.AddHostedService<ServiceBusConsumerWorker>(sp =>
      new ServiceBusConsumerWorker(
        sp.GetRequiredService<ITransport>(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        jsonOptions,  // Pass JSON options for event deserialization
        sp.GetRequiredService<ILogger<ServiceBusConsumerWorker>>(),
        sp.GetRequiredService<OrderedStreamProcessor>(),
        inventoryConsumerOptions,
        sp.GetService<ILifecycleInvoker>(),  // Add lifecycle invoker for Inbox stages
        sp.GetService<ILifecycleMessageDeserializer>()  // Add lifecycle deserializer
      )
    );

    // Logging
    var debugEnabled = Environment.GetEnvironmentVariable("WHIZBANG_DEBUG") == "true";
    builder.Services.AddLogging(logging => {
      logging.SetMinimumLevel(debugEnabled ? LogLevel.Debug : LogLevel.Information);
      logging.AddConsole();
    });

    return builder.Build();
  }

  /// <summary>
  /// Creates the BFF host with generic topic subscriptions.
  /// </summary>
  private IHost _createBffHost(
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

    // Register OrderedStreamProcessor for message ordering (required by ServiceBusConsumerWorker)
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

    // Register lifecycle services for Distribute stage support
    ECommerce.BFF.API.Generated.DispatcherRegistrations.AddWhizbangLifecycleInvoker(builder.Services);
    ECommerce.BFF.API.Generated.DispatcherRegistrations.AddWhizbangLifecycleMessageDeserializer(builder.Services);
    builder.Services.AddSingleton<Whizbang.Core.Messaging.ILifecycleReceptorRegistry, Whizbang.Core.Messaging.DefaultLifecycleReceptorRegistry>();
    builder.Services.AddSingleton<Whizbang.Core.Messaging.IEventTypeProvider, ECommerce.Contracts.ECommerceEventTypeProvider>();

    // Register TopicRegistry to provide base topic names for events
    var topicRegistryInstance = new ECommerce.Contracts.Generated.TopicRegistry();
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRegistry>(topicRegistryInstance);

    // Register GenericTopicRoutingStrategy for test topic routing
    // This distributes events across generic topics (topic-00, topic-01) for Azure Service Bus emulator compatibility
    var routingStrategyInstance = new GenericTopicRoutingStrategy(topicCount: 2);
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRoutingStrategy>(routingStrategyInstance);

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
        sp.GetRequiredService<ITransport>(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        jsonOptions,  // Pass JSON options for event deserialization
        sp.GetRequiredService<ILogger<ServiceBusConsumerWorker>>(),
        sp.GetRequiredService<OrderedStreamProcessor>(),
        consumerOptions,
        sp.GetService<ILifecycleInvoker>(),  // Add lifecycle invoker for Inbox stages
        sp.GetService<ILifecycleMessageDeserializer>()  // Add lifecycle deserializer
      )
    );

    // Logging
    var bffDebugEnabled = Environment.GetEnvironmentVariable("WHIZBANG_DEBUG") == "true";
    builder.Services.AddLogging(logging => {
      logging.SetMinimumLevel(bffDebugEnabled ? LogLevel.Debug : LogLevel.Information);
      logging.AddConsole();
    });

    return builder.Build();
  }

  /// <summary>
  /// Waits for perspective processing to complete for a specific event type using lifecycle receptor synchronization.
  /// This eliminates race conditions by waiting for the actual perspective processing to complete instead of polling database tables.
  /// Uses PostPerspectiveInline lifecycle stage which guarantees perspective data is persisted before returning.
  /// </summary>
  /// <typeparam name="TEvent">The event type to wait for.</typeparam>
  /// <param name="inventoryPerspectives">Number of perspectives in InventoryWorker host expected to process this event.</param>
  /// <param name="bffPerspectives">Number of perspectives in BFF host expected to process this event.</param>
  /// <param name="timeoutMilliseconds">Maximum time to wait in milliseconds (default: 15000ms).</param>
  /// <exception cref="TimeoutException">Thrown if perspective processing doesn't complete within timeout.</exception>
  /// <remarks>
  /// <para>
  /// <strong>Recommended usage:</strong> Replace polling-based synchronization with this method for deterministic synchronization.
  /// </para>
  /// <para>
  /// <strong>Example:</strong>
  /// </para>
  /// <code>
  /// // ProductCreatedEvent is handled by 2 perspectives in each host
  /// await WaitForPerspectiveCompletionAsync&lt;ProductCreatedEvent&gt;(
  ///   inventoryPerspectives: 2,
  ///   bffPerspectives: 2,
  ///   timeoutMilliseconds: 15000
  /// );
  ///
  /// // InventoryRestockedEvent is handled by both InventoryWorker and BFF perspectives
  /// await WaitForPerspectiveCompletionAsync&lt;InventoryRestockedEvent&gt;(
  ///   inventoryPerspectives: 1,
  ///   bffPerspectives: 1,
  ///   timeoutMilliseconds: 15000
  /// );
  /// </code>
  /// </remarks>
  /// <docs>testing/lifecycle-synchronization</docs>
  public async Task WaitForPerspectiveCompletionAsync<TEvent>(
    int inventoryPerspectives,
    int bffPerspectives,
    int timeoutMilliseconds = 15000)
    where TEvent : IEvent {

    var totalPerspectives = inventoryPerspectives + bffPerspectives;
    Console.WriteLine($"[WaitForPerspective] Waiting for {typeof(TEvent).Name} processing (Inventory={inventoryPerspectives}, BFF={bffPerspectives}, Total={totalPerspectives}, timeout={timeoutMilliseconds}ms)");

    var inventoryCompletionSource = new TaskCompletionSource<bool>();
    var inventoryCompletedPerspectives = new System.Collections.Concurrent.ConcurrentBag<string>();

    var bffCompletionSource = new TaskCompletionSource<bool>();
    var bffCompletedPerspectives = new System.Collections.Concurrent.ConcurrentBag<string>();

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
    var loggerFactory = _bffHost!.Services.GetRequiredService<ILoggerFactory>();
    var logger = loggerFactory.CreateLogger<PerspectiveCompletionWaiter<TEvent>>();

    // DIAGNOSTIC: Log registry instances used by test waiter
    Console.WriteLine($"[Fixture DIAGNOSTIC] Creating waiter for {typeof(TEvent).Name}: InventoryRegistry={inventoryRegistry.GetHashCode()}, BffRegistry={bffRegistry.GetHashCode()}");

    return new PerspectiveCompletionWaiter<TEvent>(
      inventoryRegistry,
      bffRegistry,
      inventoryPerspectives,
      bffPerspectives,
      logger
    );
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

    // CRITICAL: Pause BFF ServiceBusConsumerWorker BEFORE draining to prevent competing consumers
    // This ensures the processor's receivers are inactive while we drain stale messages
    Console.WriteLine("[ServiceBusFixture] Pausing BFF ServiceBusConsumerWorker before draining...");
    var bffConsumerWorker = _bffHost!.Services.GetService<Microsoft.Extensions.Hosting.IHostedService>()
      ?.GetType().Name.Contains("ServiceBusConsumerWorker") == true
      ? _bffHost.Services.GetService<Microsoft.Extensions.Hosting.IHostedService>() as Whizbang.Core.Workers.ServiceBusConsumerWorker
      : _bffHost.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
        .OfType<Whizbang.Core.Workers.ServiceBusConsumerWorker>()
        .FirstOrDefault();

    if (bffConsumerWorker != null) {
      await bffConsumerWorker.PauseAllSubscriptionsAsync();
      Console.WriteLine("[ServiceBusFixture] BFF consumer paused.");
    }

    // Drain Service Bus subscriptions to prevent cross-contamination between tests
    Console.WriteLine("[ServiceBusFixture] Draining subscriptions between tests...");
    await _drainSubscriptionsAsync(cancellationToken);
    Console.WriteLine("[ServiceBusFixture] Subscriptions drained.");

    // Resume BFF ServiceBusConsumerWorker after draining
    if (bffConsumerWorker != null) {
      await bffConsumerWorker.ResumeAllSubscriptionsAsync();
      Console.WriteLine("[ServiceBusFixture] BFF consumer resumed.");
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
  /// Waits for all pending work (outbox, inbox, perspectives) to complete in BOTH schemas before shutdown.
  /// This prevents in-flight operations from being canceled mid-transaction.
  /// </summary>
  private async Task _waitForPendingWorkAsync(int timeoutMilliseconds = 5000) {
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var attempt = 0;
    var consecutiveEmptyChecks = 0;  // Track consecutive checks with 0 pending work

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

      // CRITICAL: Require 3 consecutive checks with 0 pending to prevent race conditions
      // Without this, work might complete momentarily (pending=0), we return immediately,
      // then event cascades trigger new work (e.g., perspective → outbox → ServiceBus → BFF)
      // and disposal happens while new work is in-flight, causing "Query was cancelled" errors
      if (totalPending == 0) {
        consecutiveEmptyChecks++;
        if (consecutiveEmptyChecks >= 3) {
          Console.WriteLine($"[ServiceBusFixture] All pending work completed after {stopwatch.ElapsedMilliseconds}ms ({attempt} attempts, {consecutiveEmptyChecks} consecutive empty checks)");
          return;
        }
        // Still at 0, but need more checks to confirm system is truly idle
      } else {
        consecutiveEmptyChecks = 0;  // Reset counter when work is detected
      }

      if (attempt % 2 == 0 || consecutiveEmptyChecks > 0) {
        Console.WriteLine($"[ServiceBusFixture] Waiting for work: Inventory(O={invOutbox},I={invInbox},P={invPerspectives}), BFF(O={bffOutbox},I={bffInbox},P={bffPerspectives}) - {stopwatch.ElapsedMilliseconds}ms elapsed (empty checks: {consecutiveEmptyChecks}/3)");
      }

      await Task.Delay(200);  // Check every 200ms
    }

    Console.WriteLine($"[ServiceBusFixture] Warning: Pending work timeout after {timeoutMilliseconds}ms");
  }

  /// <summary>
  /// Performs SQL diagnostics for event and perspective checkpoint debugging.
  /// Queries inbox, event store, and perspective checkpoints for a specific event type.
  /// Controlled by WHIZBANG_DEBUG environment variable or debug parameter.
  /// </summary>
  /// <param name="eventTypeName">Simple event type name (e.g., "InventoryRestockedEvent")</param>
  /// <param name="logger">Logger for diagnostic output</param>
  /// <param name="forceDebug">Force debug output regardless of environment variable</param>
  /// <param name="cancellationToken">Cancellation token</param>
  public async Task DiagnoseEventFlowAsync(
    string eventTypeName,
    ILogger? logger = null,
    bool forceDebug = false,
    CancellationToken cancellationToken = default) {

    // Check if debug logging is enabled
    var debugEnabled = forceDebug || Environment.GetEnvironmentVariable("WHIZBANG_DEBUG") == "true";
    if (!debugEnabled || !_isInitialized) {
      return;
    }

    logger ??= GetLogger<ServiceBusIntegrationFixture>();

    var inventoryDbContext = _inventoryScope!.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();
    var bffDbContext = _bffScope!.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();

    // Get current instance IDs for comparison
    var inventoryInstanceProvider = _inventoryScope!.ServiceProvider.GetRequiredService<IServiceInstanceProvider>();
    var bffInstanceProvider = _bffScope!.ServiceProvider.GetRequiredService<IServiceInstanceProvider>();

    logger.LogInformation("[SQL Diagnostic] Inventory InstanceId: {InstanceId}, Service: {ServiceName}",
      inventoryInstanceProvider.InstanceId, inventoryInstanceProvider.ServiceName);
    logger.LogInformation("[SQL Diagnostic] BFF InstanceId: {InstanceId}, Service: {ServiceName}",
      bffInstanceProvider.InstanceId, bffInstanceProvider.ServiceName);
    logger.LogInformation("[SQL Diagnostic] Current UTC time: {Now}", DateTimeOffset.UtcNow);

    // Query 1: Check inbox for event
    var inboxQuery = @"
      SELECT
        message_id AS MessageId,
        message_type AS MessageType,
        stream_id AS StreamId,
        is_event AS IsEvent,
        status AS Status,
        received_at AS ReceivedAt
      FROM inventory.wh_inbox
      WHERE message_type LIKE '%' || {0} || '%'
      ORDER BY received_at DESC
      LIMIT 5";

    var inboxResults = await inventoryDbContext.Database
      .SqlQueryRaw<InboxDiagnosticResult>(inboxQuery, eventTypeName)
      .ToListAsync(cancellationToken);

    logger.LogDebug("[SQL Diagnostic] Inbox results for {EventType}: {Count} messages found",
      eventTypeName, inboxResults.Count);

    foreach (var row in inboxResults) {
      logger.LogDebug("  - MessageId={MessageId}, StreamId={StreamId}, IsEvent={IsEvent}, Status={Status}, ReceivedAt={ReceivedAt}",
        row.MessageId, row.StreamId, row.IsEvent, row.Status, row.ReceivedAt);
    }

    // Query 2: Check event store for event
    var eventStoreQuery = @"
      SELECT
        event_id AS EventId,
        stream_id AS StreamId,
        event_type AS EventType,
        version AS Version,
        created_at AS CreatedAt
      FROM inventory.wh_event_store
      WHERE event_type LIKE '%' || {0} || '%'
      ORDER BY created_at DESC
      LIMIT 5";

    var eventStoreResults = await inventoryDbContext.Database
      .SqlQueryRaw<EventStoreDiagnosticResult>(eventStoreQuery, eventTypeName)
      .ToListAsync(cancellationToken);

    logger.LogDebug("[SQL Diagnostic] Event store results for {EventType}: {Count} events found",
      eventTypeName, eventStoreResults.Count);

    foreach (var row in eventStoreResults) {
      logger.LogDebug("  - EventId={EventId}, StreamId={StreamId}, Version={Version}, CreatedAt={CreatedAt}",
        row.EventId, row.StreamId, row.Version, row.CreatedAt);
    }

    // Query 3: Check perspective checkpoints (should exist for streams that have events)
    if (eventStoreResults.Count > 0) {
      var streamIds = string.Join(", ", eventStoreResults.Select(e => $"'{e.StreamId}'"));
      var checkpointQuery = $@"
        SELECT
          perspective_name AS PerspectiveName,
          stream_id AS StreamId,
          last_event_id AS LastEventId,
          status AS Status
        FROM inventory.wh_perspective_checkpoints
        WHERE stream_id IN ({streamIds})
        ORDER BY perspective_name, stream_id";

      var checkpointResults = await inventoryDbContext.Database
        .SqlQueryRaw<CheckpointDiagnosticResult>(checkpointQuery)
        .ToListAsync(cancellationToken);

      logger.LogDebug("[SQL Diagnostic] Perspective checkpoints for streams with {EventType}: {Count} checkpoints found",
        eventTypeName, checkpointResults.Count);

      foreach (var row in checkpointResults) {
        logger.LogDebug("  - Perspective={PerspectiveName}, StreamId={StreamId}, LastEventId={LastEventId}, Status={Status}",
          row.PerspectiveName, row.StreamId, row.LastEventId, row.Status);
      }

      // Query 3.5: Check Inventory perspective_events
      var inventoryPerspectiveEventsQuery = $@"
        SELECT
          pe.event_work_id AS EventWorkId,
          pe.stream_id AS StreamId,
          pe.perspective_name AS PerspectiveName,
          pe.event_id AS EventId,
          pe.status AS Status,
          pe.instance_id AS InstanceId,
          pe.lease_expiry AS LeaseExpiry,
          pe.processed_at AS ProcessedAt,
          pe.attempts AS Attempts,
          pe.created_at AS CreatedAt
        FROM inventory.wh_perspective_events pe
        INNER JOIN inventory.wh_event_store es ON pe.event_id = es.event_id
        WHERE pe.stream_id IN ({streamIds})
          AND es.event_type LIKE '%' || {0} || '%'
        ORDER BY pe.created_at DESC
        LIMIT 10";

      var inventoryPerspectiveEventResults = await inventoryDbContext.Database
        .SqlQueryRaw<PerspectiveEventDiagnosticResult>(inventoryPerspectiveEventsQuery, eventTypeName)
        .ToListAsync(cancellationToken);

      logger.LogDebug("[SQL Diagnostic] Inventory perspective_events for {EventType}: {Count} events found",
        eventTypeName, inventoryPerspectiveEventResults.Count);

      foreach (var row in inventoryPerspectiveEventResults) {
        var isLeaseExpired = row.LeaseExpiry < DateTimeOffset.UtcNow;
        var isProcessed = row.ProcessedAt != null;
        var matchesInstanceId = row.InstanceId == inventoryInstanceProvider.InstanceId;

        logger.LogDebug("  - EventWorkId={EventWorkId}, Perspective={PerspectiveName}, Status={Status}",
          row.EventWorkId, row.PerspectiveName, row.Status);
        logger.LogDebug("    InstanceId={InstanceId} (matches={Matches}), LeaseExpiry={LeaseExpiry} (expired={Expired}), ProcessedAt={ProcessedAt} (processed={Processed})",
          row.InstanceId, matchesInstanceId, row.LeaseExpiry, isLeaseExpired, row.ProcessedAt, isProcessed);

        if (isProcessed) {
          logger.LogWarning("    ❌ Already processed");
        } else if (!matchesInstanceId) {
          logger.LogWarning("    ❌ InstanceId mismatch");
        } else if (isLeaseExpired) {
          logger.LogWarning("    ❌ Lease expired");
        } else {
          logger.LogInformation("    ✅ Should be claimable");
        }
      }
    }

    // Query 4: Check BFF schema as well
    var bffEventStoreQuery = @"
      SELECT
        event_id AS EventId,
        stream_id AS StreamId,
        event_type AS EventType,
        version AS Version,
        created_at AS CreatedAt
      FROM bff.wh_event_store
      WHERE event_type LIKE '%' || {0} || '%'
      ORDER BY created_at DESC
      LIMIT 5";

    var bffEventStoreResults = await bffDbContext.Database
      .SqlQueryRaw<EventStoreDiagnosticResult>(bffEventStoreQuery, eventTypeName)
      .ToListAsync(cancellationToken);

    logger.LogDebug("[SQL Diagnostic] BFF event store results for {EventType}: {Count} events found",
      eventTypeName, bffEventStoreResults.Count);

    foreach (var row in bffEventStoreResults) {
      logger.LogDebug("  - EventId={EventId}, StreamId={StreamId}, Version={Version}, CreatedAt={CreatedAt}",
        row.EventId, row.StreamId, row.Version, row.CreatedAt);
    }

    // Query 5: Check BFF perspective checkpoints
    if (bffEventStoreResults.Count > 0) {
      var bffStreamIds = string.Join(", ", bffEventStoreResults.Select(e => $"'{e.StreamId}'"));
      var bffCheckpointQuery = $@"
        SELECT
          perspective_name AS PerspectiveName,
          stream_id AS StreamId,
          last_event_id AS LastEventId,
          status AS Status
        FROM bff.wh_perspective_checkpoints
        WHERE stream_id IN ({bffStreamIds})
        ORDER BY perspective_name, stream_id";

      var bffCheckpointResults = await bffDbContext.Database
        .SqlQueryRaw<CheckpointDiagnosticResult>(bffCheckpointQuery)
        .ToListAsync(cancellationToken);

      logger.LogDebug("[SQL Diagnostic] BFF perspective checkpoints for streams with {EventType}: {Count} checkpoints found",
        eventTypeName, bffCheckpointResults.Count);

      foreach (var row in bffCheckpointResults) {
        logger.LogDebug("  - Perspective={PerspectiveName}, StreamId={StreamId}, LastEventId={LastEventId}, Status={Status}",
          row.PerspectiveName, row.StreamId, row.LastEventId, row.Status);
      }

      // Query 6: Check BFF perspective_events (THE CRITICAL QUERY!)
      // This shows us WHY PerspectiveWorker isn't claiming work
      var bffPerspectiveEventsQuery = $@"
        SELECT
          pe.event_work_id AS EventWorkId,
          pe.stream_id AS StreamId,
          pe.perspective_name AS PerspectiveName,
          pe.event_id AS EventId,
          pe.status AS Status,
          pe.instance_id AS InstanceId,
          pe.lease_expiry AS LeaseExpiry,
          pe.processed_at AS ProcessedAt,
          pe.attempts AS Attempts,
          pe.created_at AS CreatedAt
        FROM bff.wh_perspective_events pe
        INNER JOIN bff.wh_event_store es ON pe.event_id = es.event_id
        WHERE pe.stream_id IN ({bffStreamIds})
          AND es.event_type LIKE '%' || {0} || '%'
        ORDER BY pe.created_at DESC
        LIMIT 10";

      var bffPerspectiveEventResults = await bffDbContext.Database
        .SqlQueryRaw<PerspectiveEventDiagnosticResult>(bffPerspectiveEventsQuery, eventTypeName)
        .ToListAsync(cancellationToken);

      logger.LogInformation("[SQL Diagnostic] BFF perspective_events for {EventType}: {Count} events found",
        eventTypeName, bffPerspectiveEventResults.Count);

      foreach (var row in bffPerspectiveEventResults) {
        var isLeaseExpired = row.LeaseExpiry < DateTimeOffset.UtcNow;
        var isProcessed = row.ProcessedAt != null;
        var matchesInstanceId = row.InstanceId == bffInstanceProvider.InstanceId;

        logger.LogInformation("  - EventWorkId={EventWorkId}, Perspective={PerspectiveName}, Status={Status}",
          row.EventWorkId, row.PerspectiveName, row.Status);
        logger.LogInformation("    InstanceId={InstanceId} (matches={Matches}), LeaseExpiry={LeaseExpiry} (expired={Expired}), ProcessedAt={ProcessedAt} (processed={Processed})",
          row.InstanceId, matchesInstanceId, row.LeaseExpiry, isLeaseExpired, row.ProcessedAt, isProcessed);
        logger.LogInformation("    Attempts={Attempts}, CreatedAt={CreatedAt}",
          row.Attempts, row.CreatedAt);

        // Explain WHY this work isn't being claimed
        if (isProcessed) {
          logger.LogWarning("    ❌ NOT CLAIMABLE: Already processed at {ProcessedAt}", row.ProcessedAt);
        } else if (!matchesInstanceId) {
          logger.LogWarning("    ❌ NOT CLAIMABLE: InstanceId mismatch (expected {Expected}, got {Actual})",
            bffInstanceProvider.InstanceId, row.InstanceId);
        } else if (isLeaseExpired) {
          logger.LogWarning("    ❌ NOT CLAIMABLE: Lease expired (expiry={Expiry}, now={Now})",
            row.LeaseExpiry, DateTimeOffset.UtcNow);
        } else {
          logger.LogInformation("    ✅ SHOULD BE CLAIMABLE: All conditions met!");
        }
      }
    }
  }

  public async ValueTask DisposeAsync() {
    // CRITICAL: Wait for all pending work to complete before stopping hosts
    // This prevents in-flight perspective materialization from being canceled mid-transaction
    if (_isInitialized && _inventoryHost != null) {
      try {
        Console.WriteLine("[ServiceBusFixture] Waiting for pending work to complete before shutdown...");
        await _waitForPendingWorkAsync(timeoutMilliseconds: 5000);
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
    // The 3 consecutive empty checks in WaitForPendingWorkAsync ensure all database work is complete
    // before we reach this point, so no additional delay is needed
    if (_postgresContainer != null) {
      await _postgresContainer.DisposeAsync();
    }

    // DO NOT dispose shared ServiceBusClient here - it's owned by SharedFixtureSource
    // and should only be disposed once when ALL tests complete
    // Stopping the hosts above closes all ServiceBus processors/receivers properly
  }
}
