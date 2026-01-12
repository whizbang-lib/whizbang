using System.Net.Http.Headers;
using System.Text;
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
using Whizbang.Transports.RabbitMQ;
using Whizbang.Hosting.RabbitMQ;

namespace ECommerce.RabbitMQ.Integration.Tests.Fixtures;

/// <summary>
/// Per-class integration fixture for RabbitMQ tests.
/// Creates test hosts (Inventory, BFF) with RabbitMQ transport and provides cleanup via Management API.
/// </summary>
public sealed class RabbitMqIntegrationFixture : IAsyncDisposable {
  private readonly string _rabbitMqConnection;
  private readonly string _postgresConnection;
  private readonly Uri _managementApiUri;
  private readonly string _testClassName;
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
  /// </summary>
  public IProductLens InventoryProductLens => _inventoryScope?.ServiceProvider.GetRequiredService<IProductLens>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the IInventoryLens instance for querying inventory levels (from InventoryWorker host).
  /// </summary>
  public IInventoryLens InventoryLens => _inventoryScope?.ServiceProvider.GetRequiredService<IInventoryLens>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the IProductCatalogLens instance for querying product catalog (from BFF host).
  /// </summary>
  public IProductCatalogLens BffProductLens => _bffScope?.ServiceProvider.GetRequiredService<IProductCatalogLens>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  /// <summary>
  /// Gets the IInventoryLevelsLens instance for querying inventory levels (from BFF host).
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

  public RabbitMqIntegrationFixture(
    string rabbitMqConnection,
    string postgresConnection,
    Uri managementApiUri,
    string testClassName
  ) {
    _rabbitMqConnection = rabbitMqConnection;
    _postgresConnection = postgresConnection;
    _managementApiUri = managementApiUri;
    _testClassName = testClassName;

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
    // Create hosts
    _inventoryHost = CreateInventoryHost();
    _bffHost = CreateBffHost();

    // Initialize database schemas
    await InitializeDatabaseSchemasAsync(ct);

    // Start hosts AFTER schema is ready
    Console.WriteLine("[RabbitMqFixture] Starting service hosts...");
    await _inventoryHost.StartAsync(ct);
    await _bffHost.StartAsync(ct);
    Console.WriteLine("[RabbitMqFixture] Service hosts started.");

    // Wait for TransportConsumerWorker to fully subscribe
    // StartAsync returns before background services are fully initialized
    Console.WriteLine("[RabbitMqFixture] Waiting for consumer workers to subscribe...");
    await Task.Delay(3000, ct); // 3 seconds for subscription setup
    Console.WriteLine("[RabbitMqFixture] Consumer workers ready.");

    // Create long-lived scopes for lenses
    _inventoryScope = _inventoryHost.Services.CreateScope();
    _bffScope = _bffHost.Services.CreateScope();

    Console.WriteLine("[RabbitMqFixture] Ready for test execution!");
  }

  /// <summary>
  /// Cleans up test-specific queues and exchanges via Management API.
  /// </summary>
  public async Task CleanupTestAsync(string testName, CancellationToken ct = default) {
    string testId = new TestRabbitMqRoutingStrategy(_testClassName).GenerateTestId(testName);

    // Delete test-specific queues and exchanges via Management API
    // Note: These are placeholders - actual cleanup would need to know exact queue/exchange names
    await DeleteQueueAsync($"bff-{testId}", ct);
    await DeleteQueueAsync($"inventory-{testId}", ct);
    await DeleteExchangeAsync($"products-{testId}", ct);
  }

  private IHost CreateInventoryHost() {
    var builder = Host.CreateApplicationBuilder();

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
      new TestRabbitMqRoutingStrategy(_testClassName));

    // Register EF Core DbContext with NpgsqlDataSource (required for EnableDynamicJson)
    var inventoryDataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(_postgresConnection);
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

    // Logging
    builder.Services.AddLogging(logging => {
      logging.SetMinimumLevel(LogLevel.Information);
      logging.AddConsole();
    });

    return builder.Build();
  }

  private IHost CreateBffHost() {
    var builder = Host.CreateApplicationBuilder();

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
      new TestRabbitMqRoutingStrategy(_testClassName));

    // Register EF Core DbContext with NpgsqlDataSource (required for EnableDynamicJson)
    var bffDataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(_postgresConnection);
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

    // Register TopicRegistry
    var topicRegistryInstance = new ECommerce.Contracts.Generated.TopicRegistry();
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRegistry>(topicRegistryInstance);

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
    // BFF subscribes to test-specific exchanges/queues
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination(
      Address: $"products-{_testClassName}",
      RoutingKey: $"bff-products-queue-{_testClassName}"
    ));
    consumerOptions.Destinations.Add(new TransportDestination(
      Address: $"orders-{_testClassName}",
      RoutingKey: $"bff-orders-queue-{_testClassName}"
    ));
    builder.Services.AddSingleton(consumerOptions);
    builder.Services.AddHostedService<TransportConsumerWorker>();

    // Logging
    builder.Services.AddLogging(logging => {
      logging.SetMinimumLevel(LogLevel.Information);
      logging.AddConsole();
    });

    return builder.Build();
  }

  private async Task InitializeDatabaseSchemasAsync(CancellationToken ct) {
    // Initialize Inventory database
    // CRITICAL: Must run BEFORE starting hosts, otherwise workers fail trying to call process_work_batch
    Console.WriteLine("[RabbitMqFixture] Initializing Inventory database schema...");
    if (_inventoryHost != null) {
      using var scope = _inventoryHost.Services.CreateScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();
      var logger = scope.ServiceProvider.GetRequiredService<ILogger<RabbitMqIntegrationFixture>>();
      await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger, ct);
    }

    // Initialize BFF database
    Console.WriteLine("[RabbitMqFixture] Initializing BFF database schema...");
    if (_bffHost != null) {
      using var scope = _bffHost.Services.CreateScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();
      var logger = scope.ServiceProvider.GetRequiredService<ILogger<RabbitMqIntegrationFixture>>();
      await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger, ct);
    }

    // Register message associations for perspective auto-checkpoint creation
    // CRITICAL: Must run AFTER schema initialization (tables exist) and BEFORE starting hosts (workers need associations)
    Console.WriteLine("[RabbitMqFixture] Registering message associations...");
    if (_inventoryHost != null) {
      using var scope = _inventoryHost.Services.CreateScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.InventoryWorker.InventoryDbContext>();
      var logger = scope.ServiceProvider.GetRequiredService<ILogger<RabbitMqIntegrationFixture>>();

      await ECommerce.InventoryWorker.Generated.PerspectiveRegistrationExtensions.RegisterPerspectiveAssociationsAsync(
        dbContext,
        schema: "inventory",
        serviceName: "ECommerce.InventoryWorker",
        logger: logger,
        cancellationToken: ct
      );

      Console.WriteLine("[RabbitMqFixture] InventoryWorker message associations registered (inventory schema)");
    }

    if (_bffHost != null) {
      using var scope = _bffHost.Services.CreateScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<ECommerce.BFF.API.BffDbContext>();
      var logger = scope.ServiceProvider.GetRequiredService<ILogger<RabbitMqIntegrationFixture>>();

      await ECommerce.BFF.API.Generated.PerspectiveRegistrationExtensions.RegisterPerspectiveAssociationsAsync(
        dbContext,
        schema: "bff",
        serviceName: "ECommerce.BFF.API",
        logger: logger,
        cancellationToken: ct
      );

      Console.WriteLine("[RabbitMqFixture] BFF message associations registered (bff schema)");
    }

    Console.WriteLine("[RabbitMqFixture] Database initialization complete.");
  }

  private async Task DeleteQueueAsync(string queueName, CancellationToken ct = default) {
    try {
      var response = await _managementClient.DeleteAsync($"/api/queues/%2F/{queueName}", ct);
      response.EnsureSuccessStatusCode();
    } catch {
      // Queue might not exist, ignore
    }
  }

  private async Task DeleteExchangeAsync(string exchangeName, CancellationToken ct = default) {
    try {
      var response = await _managementClient.DeleteAsync($"/api/exchanges/%2F/{exchangeName}", ct);
      response.EnsureSuccessStatusCode();
    } catch {
      // Exchange might not exist, ignore
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
    // Dispose scopes
    _inventoryScope?.Dispose();
    _bffScope?.Dispose();

    // Stop and dispose hosts (this will close RabbitMQ consumers/channels)
    if (_inventoryHost != null) {
      await _inventoryHost.StopAsync(TimeSpan.FromSeconds(10)); // Increased timeout for graceful shutdown
      _inventoryHost.Dispose();
    }

    if (_bffHost != null) {
      await _bffHost.StopAsync(TimeSpan.FromSeconds(10)); // Increased timeout for graceful shutdown
      _bffHost.Dispose();
    }

    // CRITICAL: Wait for RabbitMQ connections to fully close
    // RabbitMQ consumers dispose asynchronously, and connections need time to clean up
    Console.WriteLine("[RabbitMqFixture] Waiting for RabbitMQ connections to close...");
    await Task.Delay(2000); // 2 second delay for connection cleanup
    Console.WriteLine("[RabbitMqFixture] RabbitMQ connections closed.");

    _managementClient.Dispose();
  }
}
