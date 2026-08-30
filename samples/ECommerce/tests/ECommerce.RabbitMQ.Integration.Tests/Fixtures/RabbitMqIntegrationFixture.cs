using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ECommerce.BFF.API.Generated;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Generated;
using ECommerce.Integration.TestUtilities.Fixtures;
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

    // Deterministic readiness gate — BackgroundService.StartAsync returns
    // before ExecuteAsync has actually issued any LISTEN/SubscribeBatchAsync
    // calls. Tests that dispatch immediately after the host starts can land
    // their first messages before the BFF RabbitMQ consumer is bound, so
    // the message is delivered to an unbound exchange and lost — the
    // receptor never sees it and the lifecycle TCS never signals.
    //
    // TransportConsumerWorker.SubscriptionsReady is a public API that
    // completes after every destination's SubscribeBatchAsync has returned.
    // Wait on both hosts here so every test body starts with the certainty
    // that the consumer is actually receiving.
    Console.WriteLine("[RabbitMqFixture] Awaiting transport consumer readiness...");
    await _waitForTransportConsumersReadyAsync(_inventoryHost, ct);
    await _waitForTransportConsumersReadyAsync(_bffHost, ct);
    Console.WriteLine("[RabbitMqFixture] Transport consumers ready");

    // Wait for workers to complete startup (initial checkpoints, rewind scan, registry reconciliation).
    // The BFF PerspectiveWorker may not reach idle during startup due to background work
    // (e.g. perspective registry reconciliation, table statistics collection).
    // Tests use CleanupDatabaseAsync which also waits before each test.
    Console.WriteLine("[RabbitMqFixture] Waiting for workers to become ready...");
    try {
      await _waitForWorkersReadyAsync(ct);
    } catch (TimeoutException) {
      Console.WriteLine("[RabbitMqFixture] Some workers still active during init — continuing");
    }
    Console.WriteLine("[RabbitMqFixture] Workers ready");

    // Create long-lived scopes for lenses
    Console.WriteLine("[RabbitMqFixture] Creating long-lived scopes for lenses...");
    _inventoryScope = _inventoryHost.Services.CreateScope();
    _bffScope = _bffHost.Services.CreateScope();
    Console.WriteLine("[RabbitMqFixture] Scopes created");

    // Warm-up dispatch — drive the full pipeline (Inventory dispatch → ASB publish →
    // BFF consume → BFF perspective → PostPerspectiveDetached lifecycle) once before
    // any test runs. This forces lazy initialization (RabbitMQ channels, ASB sender
    // setup, perspective JIT, serialization codegen, etc.) to complete here, where
    // the wait is bounded by a deliberately generous 60 s budget. Without this, the
    // first test that exercises the pipeline pays the warm-up tax inside its own
    // assertion deadline and frequently times out — exactly the
    // PostPerspectiveDetached_* / PrePerspectiveInline_* flakes seen on the v0.647
    // → v0.654 PR train, where retries always passed because the second run was
    // warm.
    Console.WriteLine("[RabbitMqFixture] Warming up dispatch pipeline...");
    await _warmUpDispatchPipelineAsync(ct);
    Console.WriteLine("[RabbitMqFixture] Warm-up complete");

    Console.WriteLine("[RabbitMqFixture] InitializeAsync COMPLETE - Ready for test execution!");
  }

  /// <summary>
  /// One-time dispatch through the full Inventory → ASB → BFF → perspective →
  /// lifecycle path, used by <see cref="InitializeAsync"/> to absorb cold-start latency
  /// before any test runs. The warm-up command is a real
  /// <c>CreateProductCommand</c>; the row it leaves behind is wiped by the per-test
  /// <see cref="CleanupDatabaseAsync"/> hook so test bodies start against a clean
  /// database.
  /// </summary>
  private async Task _warmUpDispatchPipelineAsync(CancellationToken ct) {
    var warmupProductId = ECommerce.Contracts.Commands.ProductId.New();
    var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receptor = new ECommerce.Integration.Tests.Fixtures.GenericLifecycleCompletionReceptor<ECommerce.Contracts.Events.ProductCreatedEvent>(
      completionSource,
      expectedStage: LifecycleStage.PostPerspectiveDetached,
      perspectiveName: null,
      messageFilter: e => e.ProductId == warmupProductId.Value);

    var registry = _bffHost!.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ECommerce.Contracts.Events.ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveDetached);
    try {
      var dispatcher = _inventoryHost!.Services.GetRequiredService<IDispatcher>();
      await dispatcher.SendAsync(new ECommerce.Contracts.Commands.CreateProductCommand {
        ProductId = warmupProductId,
        Name = "Warm-up product",
        Description = "Discarded by per-test cleanup; exists only to absorb cold-start latency.",
        Price = 0.01m,
        InitialStock = 0,
      });

      // 60 s budget — comfortably larger than observed cold-start latency
      // (typically 5-20 s in CI) but small enough that a genuine pipeline-stuck
      // failure surfaces here with a clear "warm-up timed out" diagnostic
      // instead of cascading into every per-test assertion.
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
    } finally {
      registry.Unregister<ECommerce.Contracts.Events.ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveDetached);
    }
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
  /// Cleans up all test data between tests when using the shared fixture pattern.
  /// Purges RabbitMQ queues and deletes all Whizbang infrastructure table data.
  /// </summary>
  public async Task CleanupDatabaseAsync(CancellationToken cancellationToken = default) {
    // 1. Wait for workers to drain any in-flight messages from the previous test FIRST.
    // This prevents truncating data that workers are still processing.
    // v0.654 late-suite-flake investigation: previously this swallowed
    // TimeoutException and proceeded to truncate the DB while workers might still
    // be mid-flight. That silently corrupted state and surfaced as random tests
    // ~95 deep into the suite timing out at their own assertion deadline — different
    // test each run, always exactly when load reached the point where the
    // PerspectiveWorker / drain workers couldn't catch up between tests. Now we let
    // the exception propagate: _waitForWorkersReadyAsync logs WHICH workers failed
    // to idle, the test fails fast with that diagnostic, and the next failing CI
    // run tells us exactly where to look instead of asking us to play detective on
    // a different cascading symptom each time.
    await _waitForWorkersReadyAsync(cancellationToken);

    // 2. Purge RabbitMQ queues to prevent stale messages from previous tests
    await _purgeQueueAsync($"bff-products-queue-{_testId}");
    await _purgeQueueAsync($"inventory-products-queue-{_testId}");
    await _purgeQueueAsync($"bff-inventory-queue-{_testId}");

    // 3. Delete all Whizbang infrastructure table data from BOTH databases (explicit ordering for FK safety)
    const int maxRetries = 5;
    const int retryDelayMs = 300;

    // Clean inventory database
    for (var attempt = 1; attempt <= maxRetries; attempt++) {
      try {
        await using var connection = new Npgsql.NpgsqlConnection(_inventoryPostgresConnection);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
          DELETE FROM inventory.wh_perspective_events;
          DELETE FROM inventory.wh_perspective_cursors;
          DELETE FROM inventory.wh_lifecycle_completions;
          DELETE FROM inventory.wh_outbox;
          DELETE FROM inventory.wh_inbox;
          DELETE FROM inventory.wh_receptor_processing;
          DELETE FROM inventory.wh_message_deduplication;
          DELETE FROM inventory.wh_event_store;
          DELETE FROM inventory.wh_active_streams;
          DELETE FROM inventory.wh_per_inventory_level;
          DELETE FROM inventory.wh_per_product;
          DELETE FROM inventory.wh_perspective_snapshots;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        break;
      } catch (Npgsql.PostgresException ex) when (ex.SqlState == "40P01" && attempt < maxRetries) {
        Console.WriteLine($"[RabbitMqFixture] Deadlock during inventory cleanup (attempt {attempt}/{maxRetries}), retrying...");
        await Task.Delay(retryDelayMs * attempt, cancellationToken);
      }
    }

    // Clean BFF database
    for (var attempt = 1; attempt <= maxRetries; attempt++) {
      try {
        await using var connection = new Npgsql.NpgsqlConnection(_bffPostgresConnection);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
          DELETE FROM bff.wh_perspective_events;
          DELETE FROM bff.wh_perspective_cursors;
          DELETE FROM bff.wh_lifecycle_completions;
          DELETE FROM bff.wh_outbox;
          DELETE FROM bff.wh_inbox;
          DELETE FROM bff.wh_receptor_processing;
          DELETE FROM bff.wh_message_deduplication;
          DELETE FROM bff.wh_event_store;
          DELETE FROM bff.wh_active_streams;
          DELETE FROM bff.wh_per_product;
          DELETE FROM bff.wh_per_inventory_level;
          DELETE FROM bff.wh_perspective_snapshots;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        break;
      } catch (Npgsql.PostgresException ex) when (ex.SqlState == "40P01" && attempt < maxRetries) {
        Console.WriteLine($"[RabbitMqFixture] Deadlock during BFF cleanup (attempt {attempt}/{maxRetries}), retrying...");
        await Task.Delay(retryDelayMs * attempt, cancellationToken);
      }
    }

    // 4. Clear publisher in-flight state AFTER workers are idle.
    // The new path tracks in-flight on IWorkChannelWriter (singleton across the publish pipeline);
    // calling ClearInFlight directly avoids the per-worker wrapper the legacy publisher exposed.
    _inventoryHost!.Services.GetService<Whizbang.Core.Messaging.IWorkChannelWriter>()?.ClearInFlight();
    _bffHost!.Services.GetService<Whizbang.Core.Messaging.IWorkChannelWriter>()?.ClearInFlight();

    Console.WriteLine("[RabbitMqFixture] Database cleaned up between tests");
  }

  private async Task _purgeQueueAsync(string queueName, CancellationToken ct = default) {
    try {
      var response = await _managementClient.DeleteAsync($"/api/queues/%2F/{queueName}/contents", ct);
      // 204 = purged, 404 = queue doesn't exist — both are fine
    } catch {
      // Queue might not exist, ignore
    }
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

    // Lifecycle stage firing observer — deterministic test-side signal that fires
    // from ReceptorInvoker.OnReceptorFiredAsync after every receptor invocation completes.
    // Replaces the prior receptor-TCS pattern (fragile under shared-fixture state +
    // parallel CI scheduling). See LifecycleStageFiringObserver for rationale.
    builder.Services.AddSingleton<ECommerce.Integration.TestUtilities.Fixtures.LifecycleStageFiringObserver>();
    builder.Services.AddSingleton<Whizbang.Core.Messaging.IReceptorFiringObserver>(sp =>
      sp.GetRequiredService<ECommerce.Integration.TestUtilities.Fixtures.LifecycleStageFiringObserver>());

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
    // Configure PerspectiveWorker with faster polling for integration tests
    builder.Services.Configure<PerspectiveWorkerOptions>(options => {
      options.PollingIntervalMilliseconds = 100;
      options.LeaseSeconds = 300;
      options.AbandonStaleInstanceThresholdSeconds = 600;
      options.DebugMode = true;
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;
    });

    // Centralized test-side timing overrides — ClaimWorker poll cadence,
    // BackupTickCoordinator wake cadence, and SlidingWindow* MaxWait. See
    // TestWorkerTimingOverrides XML doc for the per-knob rationale.
    builder.Services.ApplyTestTimings();

    // Register background workers
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
      ,
        serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider())
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

    // Lifecycle stage firing observer — see InventoryHost rationale.
    builder.Services.AddSingleton<ECommerce.Integration.TestUtilities.Fixtures.LifecycleStageFiringObserver>();
    builder.Services.AddSingleton<Whizbang.Core.Messaging.IReceptorFiringObserver>(sp =>
      sp.GetRequiredService<ECommerce.Integration.TestUtilities.Fixtures.LifecycleStageFiringObserver>());

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
    // Configure PerspectiveWorker with faster polling for integration tests
    builder.Services.Configure<PerspectiveWorkerOptions>(options => {
      options.PollingIntervalMilliseconds = 100;
      options.LeaseSeconds = 300;
      options.AbandonStaleInstanceThresholdSeconds = 600;
      options.DebugMode = true;
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;
    });

    // Centralized test-side timing overrides — ClaimWorker poll cadence,
    // BackupTickCoordinator wake cadence, and SlidingWindow* MaxWait. See
    // TestWorkerTimingOverrides XML doc for the per-knob rationale.
    builder.Services.ApplyTestTimings();

    // Register background workers
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
      ,
        serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider())
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
  /// <summary>
  /// Awaits <see cref="Whizbang.Core.Workers.TransportConsumerWorker.SubscriptionsReady"/>
  /// on every transport consumer hosted by <paramref name="host"/>. Once
  /// this returns, the host is actually receiving on its destinations —
  /// dispatches issued after this point are guaranteed to be observable by
  /// the lifecycle receptors instead of being dropped on the floor by an
  /// unbound exchange.
  /// </summary>
  private static async Task _waitForTransportConsumersReadyAsync(Microsoft.Extensions.Hosting.IHost host, CancellationToken ct) {
    var consumers = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
      .OfType<Whizbang.Core.Workers.TransportConsumerWorker>()
      .ToList();
    if (consumers.Count == 0) {
      return;
    }
    await Task.WhenAll(consumers.Select(c => c.WaitForSubscriptionsReadyAsync(ct))).ConfigureAwait(false);
  }

  // Diagnostic record so the per-worker idle-status snapshot is structured and easy to
  // log — used by _waitForWorkersReadyAsync to surface WHICH workers are still active
  // when the safety-net timeout fires.
  private readonly record struct _WorkerIdleSnapshot(string Host, string Worker, Task Wait, Func<bool> IsIdleNow);

  private async Task _waitForWorkersReadyAsync(CancellationToken ct) {
    // Phase H step 4b made the drain workers the active path; the legacy publisher
    // defaults to disabled and reports IsIdle=true instantly. Polling only it (and
    // PerspectiveWorker) made the fixture truncate tables while the real drain
    // workers were still mid-flight. Wait on the actual active workers per host:
    //   - OutboxDrainWorker  → outbox publish path
    //   - InboxDrainWorker   → inbox payload-fetch + dispatch handoff
    //   - PerspectiveWorker  → perspective projection
    // Legacy OutboxPublishWorker is still polled for backwards-compat with hosts
    // that have the rollback flag enabled; it instantly succeeds when disabled.
    var snapshots = new List<_WorkerIdleSnapshot>();

    void WireOnce<TWorker>(string host, string workerName, TWorker? w, Func<TWorker, bool> idleGetter, Action<TWorker, WorkProcessingIdleHandler> subscribe, Action<TWorker, WorkProcessingIdleHandler> unsubscribe)
      where TWorker : class {
      if (w is null) { return; }
      var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      snapshots.Add(new _WorkerIdleSnapshot(host, workerName, tcs.Task, () => idleGetter(w)));
      if (idleGetter(w)) { tcs.TrySetResult(true); return; }
      void Handler() { tcs.TrySetResult(true); unsubscribe(w, Handler); }
      subscribe(w, Handler);
      // Re-check after subscribe to close the race window.
      if (idleGetter(w)) { tcs.TrySetResult(true); }
    }

    void WireOutboxPublish(string host, OutboxPublishWorker? w) => WireOnce(host, "OutboxPublishWorker", w, x => x.IsIdle, (x, h) => x.OnWorkProcessingIdle += h, (x, h) => x.OnWorkProcessingIdle -= h);
    void WireOutboxDrain(string host, OutboxDrainWorker? w) => WireOnce(host, "OutboxDrainWorker", w, x => x.IsIdle, (x, h) => x.OnWorkProcessingIdle += h, (x, h) => x.OnWorkProcessingIdle -= h);
    void WireInboxDrain(string host, InboxDrainWorker? w) => WireOnce(host, "InboxDrainWorker", w, x => x.IsIdle, (x, h) => x.OnWorkProcessingIdle += h, (x, h) => x.OnWorkProcessingIdle -= h);
    void WirePerspective(string host, PerspectiveWorker? w) => WireOnce(host, "PerspectiveWorker", w, x => x.IsIdle, (x, h) => x.OnWorkProcessingIdle += h, (x, h) => x.OnWorkProcessingIdle -= h);

    var inventoryHostedServices = _inventoryHost!.Services.GetServices<IHostedService>().ToList();
    var bffHostedServices = _bffHost!.Services.GetServices<IHostedService>().ToList();

    WireOutboxPublish("Inventory", inventoryHostedServices.OfType<OutboxPublishWorker>().FirstOrDefault());
    WireOutboxPublish("BFF", bffHostedServices.OfType<OutboxPublishWorker>().FirstOrDefault());
    WireOutboxDrain("Inventory", inventoryHostedServices.OfType<OutboxDrainWorker>().FirstOrDefault());
    WireOutboxDrain("BFF", bffHostedServices.OfType<OutboxDrainWorker>().FirstOrDefault());
    WireInboxDrain("Inventory", inventoryHostedServices.OfType<InboxDrainWorker>().FirstOrDefault());
    WireInboxDrain("BFF", bffHostedServices.OfType<InboxDrainWorker>().FirstOrDefault());
    WirePerspective("Inventory", inventoryHostedServices.OfType<PerspectiveWorker>().FirstOrDefault());
    WirePerspective("BFF", bffHostedServices.OfType<PerspectiveWorker>().FirstOrDefault());

    if (snapshots.Count == 0) {
      Console.WriteLine("[RabbitMqFixture] No idle-capable workers found — proceeding");
      return;
    }

    // Safety-net timeout. Bumped 30s → 90s after the late-suite flake investigation
    // (Jun 2026): under heavy CI load the perspective worker occasionally takes
    // longer than 30s to fully drain. 90s gives genuinely-busy workers room to
    // finish without masking a true stuck-worker bug.
    var effectiveTimeout = Whizbang.Testing.TestTimeouts.Scale(90000);
    try {
      await Task.WhenAll(snapshots.Select(s => s.Wait)).WaitAsync(TimeSpan.FromMilliseconds(effectiveTimeout), ct);
    } catch (TimeoutException) {
      // Diagnostic surfacing: identify exactly WHICH workers failed to idle so the
      // caller's "swallow and proceed" path can produce a structured signal instead
      // of a generic "Workers not idle before cleanup" message. Re-throw so the
      // caller's TimeoutException catch handles the swallow-vs-rethrow policy.
      var stillActive = snapshots.Where(s => !s.IsIdleNow()).ToList();
      var summary = stillActive.Count == 0
        ? "all workers reported idle after timeout (likely race vs. event subscription)"
        : string.Join(", ", stillActive.Select(s => $"{s.Host}/{s.Worker}"));
      Console.WriteLine($"[RabbitMqFixture] _waitForWorkersReadyAsync TIMEOUT after {effectiveTimeout}ms — still active: {summary}");
      throw;
    }

    Console.WriteLine($"[RabbitMqFixture] All workers idle ({snapshots.Count} signals)");
  }

  /// <summary>
  /// Waits for all workers on both hosts to become idle.
  /// Useful after perspective processing to ensure DB commits are flushed.
  /// </summary>
  public async Task WaitForWorkersIdleAsync(int timeoutMilliseconds = 15000) {
    await _waitForWorkersReadyAsync(default);
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

  /// <summary>
  /// Waits for a message to be published via the outbox worker using the
  /// <see cref="WorkCoordinatorPublisherWorker.OnOutboxMessagePublished"/> hook.
  /// Deterministic — fires directly from the worker's processing loop.
  /// </summary>
  public async Task<Guid> WaitForOutboxPublishAsync(int timeoutMilliseconds = 30000) {
    var tcs = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
    // Post-Phase-H: OutboxDrainWorker is the active publish path. Subscribe to both if
    // registered so the fixture works regardless of which worker is active.
    var hostedServices = InventoryHost.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList();
    var drainWorker = hostedServices.OfType<OutboxDrainWorker>().FirstOrDefault();
    var publishWorker = hostedServices.OfType<OutboxPublishWorker>().FirstOrDefault();
    if (drainWorker is null && publishWorker is null) {
      throw new InvalidOperationException("Neither OutboxDrainWorker nor OutboxPublishWorker registered on InventoryHost");
    }

    void handler(OutboxMessagePublishedEvent e) {
      tcs.TrySetResult(e.MessageId);
    }

    if (drainWorker is not null) {
      drainWorker.OnOutboxMessagePublished += handler;
    }
    if (publishWorker is not null) {
      publishWorker.OnOutboxMessagePublished += handler;
    }

    try {
      var effectiveTimeout = Whizbang.Testing.TestTimeouts.Scale(timeoutMilliseconds);
      return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(effectiveTimeout));
    } finally {
      if (drainWorker is not null) {
        drainWorker.OnOutboxMessagePublished -= handler;
      }
      if (publishWorker is not null) {
        publishWorker.OnOutboxMessagePublished -= handler;
      }
    }
  }

  /// <summary>
  /// Waits for a specific number of perspective EVENTS (not fires) to be processed using
  /// the <see cref="PerspectiveWorker.OnPerspectiveEventProcessed"/> hook.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The handler fires ONCE per (perspective, stream) batch — but each fire carries an
  /// <c>EventCount</c> that reflects how many events were processed in that batch. This
  /// implementation SUMS EventCount so callers can specify a precise event total instead
  /// of guessing how the worker batched them.
  /// </para>
  /// <para>
  /// Example: CreateProductCommand with InitialStock &gt; 0 produces 3 events on inventory
  /// (ProductCreated x2 + InventoryRestocked x1). Whether the worker dispatches them in 2
  /// or 3 batches (depending on drain timing) doesn't matter — <c>expectedCompletions: 3</c>
  /// always waits until all 3 events have actually been applied.
  /// </para>
  /// </remarks>
  public async Task WaitForPerspectiveProcessingAsync(
      int expectedCompletions,
      int timeoutMilliseconds = 30000,
      string? hostFilter = null,
      Guid? streamId = null) {

    var eventCount = 0;
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    void WireWorker(PerspectiveWorker? worker) {
      if (worker is null) {
        return;
      }

      void handler(PerspectiveEventProcessedEvent e) {
        // Stream-id filter eliminates cross-test contamination: prior test's in-flight
        // events keep firing on the worker after cleanup; without this filter their
        // EventCount satisfied the wait before THIS test's command had committed.
        // Mirror of the ASB fixture fix.
        if (streamId.HasValue && e.StreamId != streamId.Value) {
          return;
        }
        var current = Interlocked.Add(ref eventCount, e.EventCount);
        if (current >= expectedCompletions) {
          tcs.TrySetResult(true);
        }
      }

      worker.OnPerspectiveEventProcessed += handler;
    }

    if (hostFilter is null or "inventory") {
      var inventoryWorker = InventoryHost.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
        .OfType<PerspectiveWorker>().FirstOrDefault();
      WireWorker(inventoryWorker);
    }

    if (hostFilter is null or "bff") {
      var bffWorker = BffHost.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
        .OfType<PerspectiveWorker>().FirstOrDefault();
      WireWorker(bffWorker);
    }

    var effectiveTimeout = Whizbang.Testing.TestTimeouts.Scale(timeoutMilliseconds);
    await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(effectiveTimeout));
  }

  /// <summary>
  /// Deterministic-progress variant of <see cref="WaitForPerspectiveProcessingAsync"/>.
  /// Fails fast with a structured diagnostic when no perspective events have arrived for
  /// <paramref name="noProgressIdleMs"/> (default 10 s). When events are flowing the helper
  /// stays patient up to <paramref name="absoluteTimeoutMs"/>; when the pipeline stalls the
  /// test errors with the actual counts + idle time so flakes are immediately distinguishable
  /// from genuine pipeline breakage.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This addresses the timeout-only flake mode where transport / consumer / worker get
  /// stuck and the test waits the full configured timeout (a minute or two) before failing
  /// with no signal as to which stage stalled. The progress gate caps idle waits at a few
  /// seconds and reports the exact event count + stream id observed so the CI log surfaces
  /// the failure stage immediately. The absolute ceiling still applies — if events are
  /// arriving slowly the test waits, but never longer than the ceiling.
  /// </para>
  /// </remarks>
  public async Task WaitForPerspectiveProcessingDeterministicAsync(
      int expectedCompletions,
      Guid streamId,
      int noProgressIdleMs = 10000,
      int absoluteTimeoutMs = 60000,
      string? hostFilter = null) {
    var eventCount = 0;
    var observedStreamCount = 0;
    var lastProgressTicks = Environment.TickCount64;
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var handlers = new List<(PerspectiveWorker worker, PerspectiveEventProcessedHandler handler)>();

    void WireWorker(PerspectiveWorker? worker) {
      if (worker is null) {
        return;
      }

      void handler(PerspectiveEventProcessedEvent e) {
        Interlocked.Increment(ref observedStreamCount);
        if (e.StreamId != streamId) {
          return;
        }
        Interlocked.Exchange(ref lastProgressTicks, Environment.TickCount64);
        var current = Interlocked.Add(ref eventCount, e.EventCount);
        if (current >= expectedCompletions) {
          tcs.TrySetResult(true);
        }
      }

      worker.OnPerspectiveEventProcessed += handler;
      handlers.Add((worker, handler));
    }

    if (hostFilter is null or "inventory") {
      WireWorker(InventoryHost.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
        .OfType<PerspectiveWorker>().FirstOrDefault());
    }
    if (hostFilter is null or "bff") {
      WireWorker(BffHost.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
        .OfType<PerspectiveWorker>().FirstOrDefault());
    }

    using var watchdogCts = new CancellationTokenSource();
    var watchdog = Task.Run(async () => {
      while (!tcs.Task.IsCompleted && !watchdogCts.IsCancellationRequested) {
        try { await Task.Delay(1000, watchdogCts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        var idleMs = Environment.TickCount64 - Interlocked.Read(ref lastProgressTicks);
        if (idleMs > noProgressIdleMs) {
          tcs.TrySetException(new TimeoutException(
            $"Perspective processing stalled — got {Volatile.Read(ref eventCount)}/{expectedCompletions} matching events for stream {streamId:N} after observing {Volatile.Read(ref observedStreamCount)} cross-stream fires, idle for {idleMs}ms (no-progress threshold {noProgressIdleMs}ms, ceiling {absoluteTimeoutMs}ms)."));
          return;
        }
      }
    }, watchdogCts.Token);

    try {
      await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(Whizbang.Testing.TestTimeouts.Scale(absoluteTimeoutMs))).ConfigureAwait(false);
    } finally {
      watchdogCts.Cancel();
      foreach (var (worker, handler) in handlers) {
        worker.OnPerspectiveEventProcessed -= handler;
      }
    }
  }

  /// <summary>
  /// Truly signal-based completion wait: uses
  /// <see cref="Whizbang.Core.Perspectives.Sync.IPerspectiveSyncAwaiter.WaitForStreamAsync"/>
  /// to wait until ALL pending perspective_events for the given (perspective, stream) have
  /// been processed. No event counting, no progress watchdog — the worker's cursor catching
  /// up IS the completion signal. The TUnit test [Timeout(N)] attribute is the only upper
  /// bound; if the cursor never catches up the test fails at that ceiling with the worker's
  /// own structured diagnostic.
  /// </summary>
  /// <remarks>
  /// Caller passes the perspective types they expect to apply events for the stream. The
  /// helper resolves <see cref="Whizbang.Core.Perspectives.Sync.IPerspectiveSyncAwaiter"/>
  /// from the inventory host (or BFF) and runs all per-perspective waits in parallel. Each
  /// per-perspective wait returns when that perspective's cursor for the stream has caught
  /// up to all currently-pending events — a real worker signal, not a polling heuristic.
  /// </remarks>
  public async Task WaitForStreamCaughtUpAsync(
      Guid streamId,
      IReadOnlyList<Type> perspectiveTypes,
      TimeSpan timeout,
      string? hostFilter = null) {
    ArgumentNullException.ThrowIfNull(perspectiveTypes);
    if (perspectiveTypes.Count == 0) {
      return;
    }

    var hosts = new List<Microsoft.Extensions.Hosting.IHost>();
    if (hostFilter is null or "inventory") {
      hosts.Add(InventoryHost);
    }
    if (hostFilter is null or "bff") {
      hosts.Add(BffHost);
    }

    // IPerspectiveSyncAwaiter is registered as Scoped — resolving it from the
    // root provider trips ValidateScopes when DI is built with strict mode
    // (which is the default in production hosts and was apparently enabled in
    // this fixture's hosts after recent refactors). Open a scope per host so
    // the resolution is valid. The awaiter's pending-events tracker is a
    // singleton internally, so per-scope resolution doesn't lose state across
    // these calls.
    var scopes = new List<IServiceScope>();
    var waits = new List<Task>();
    try {
      foreach (var host in hosts) {
        var scope = host.Services.CreateScope();
        scopes.Add(scope);
        var awaiter = scope.ServiceProvider.GetService<Whizbang.Core.Perspectives.Sync.IPerspectiveSyncAwaiter>();
        if (awaiter is null) {
          continue;
        }
        foreach (var perspectiveType in perspectiveTypes) {
          waits.Add(awaiter.WaitForStreamAsync(
            perspectiveType, streamId, eventTypes: null, timeout: timeout));
        }
      }
      await Task.WhenAll(waits).ConfigureAwait(false);
    } finally {
      foreach (var scope in scopes) {
        scope.Dispose();
      }
    }
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
