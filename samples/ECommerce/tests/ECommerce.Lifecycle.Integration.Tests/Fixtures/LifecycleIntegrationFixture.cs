using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ECommerce.Integration.Tests.Fixtures;
using ECommerce.Lifecycle.Integration.Tests.Domain;
using ECommerce.Lifecycle.Integration.Tests.Generated;
using Medo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
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

namespace ECommerce.Lifecycle.Integration.Tests.Fixtures;

/// <summary>
/// Per-test integration fixture for lifecycle batch overflow testing.
/// Creates a single host that handles commands AND runs perspectives.
/// Isolated from ECommerce.BFF.API and ECommerce.InventoryWorker module initializers.
/// </summary>
public sealed class LifecycleIntegrationFixture : IAsyncDisposable {
  private readonly string _rabbitMqConnection;
  private readonly string _postgresConnection;
  private readonly Uri _managementApiUri;
  private readonly string _testId;
  private readonly HttpClient _managementClient;

  private IHost? _host;
  private IServiceScope? _scope;

  public IDispatcher Dispatcher => _host?.Services.GetRequiredService<IDispatcher>()
    ?? throw new InvalidOperationException("Fixture not initialized");

  public IHost Host => _host
    ?? throw new InvalidOperationException("Fixture not initialized");

  public LifecycleIntegrationFixture(
    string rabbitMqConnection,
    string postgresConnection,
    Uri managementApiUri,
    string testId) {
    _rabbitMqConnection = rabbitMqConnection;
    _postgresConnection = postgresConnection;
    _managementApiUri = managementApiUri;
    _testId = testId;

    _managementClient = new HttpClient { BaseAddress = managementApiUri };
    _managementClient.DefaultRequestHeaders.Authorization =
      new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
        Encoding.ASCII.GetBytes("guest:guest")));
  }

  public async Task InitializeAsync(CancellationToken ct = default) {
    Console.WriteLine($"[LifecycleFixture] InitializeAsync START (testId={_testId})");

    _host = _createHost();

    await _initializeDatabaseSchemaAsync(ct);

    await _host.StartAsync(ct);
    Console.WriteLine("[LifecycleFixture] Host started");

    // Wait for TransportConsumerWorker to subscribe
    await Task.Delay(3000, ct);
    Console.WriteLine("[LifecycleFixture] Consumer workers ready");

    _scope = _host.Services.CreateScope();
    Console.WriteLine("[LifecycleFixture] InitializeAsync COMPLETE");
  }

  /// <summary>
  /// Creates a PerspectiveCompletionWaiter for the specified event type.
  /// Must be called BEFORE sending commands to avoid race conditions.
  /// Single-host design: same registry used for both parameters.
  /// </summary>
  public PerspectiveCompletionWaiter<TEvent> CreatePerspectiveWaiter<TEvent>(int perspectives)
    where TEvent : class, IEvent {
    var host = _host ?? throw new InvalidOperationException("Fixture not initialized");
    var registry = host.Services.GetRequiredService<IReceptorRegistry>();
    // Single host: use same registry for both slots, all perspectives on one host
    return new PerspectiveCompletionWaiter<TEvent>(
      registry, registry,
      inventoryPerspectives: 0, bffPerspectives: perspectives);
  }

  public async ValueTask DisposeAsync() {
    _scope?.Dispose();

    if (_host != null) {
      await _host.StopAsync();
      _host.Dispose();
    }

    // Cleanup RabbitMQ resources
    await _deleteQueueAsync($"lifecycle-mockbatch-queue-{_testId}");
    await _deleteQueueAsync($"lifecycle-mocknoise-queue-{_testId}");
    await _deleteExchangeAsync($"mockbatchtest-{_testId}");
    await _deleteExchangeAsync($"mockbatchnoise-{_testId}");

    // Cleanup database
    await _dropDatabaseAsync();

    _managementClient.Dispose();
    Console.WriteLine("[LifecycleFixture] Disposed");
  }

  private IHost _createHost() {
    var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

    // Connection string for EFCore
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:lifecycle-test-db"] = _postgresConnection
    });

    // Service instance provider
    builder.Services.AddSingleton<IServiceInstanceProvider>(
      new TestServiceInstanceProvider(Uuid7.NewUuid7().ToGuid(), "LifecycleTest"));

    // JSON serialization
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    builder.Services.AddSingleton(jsonOptions);

    // RabbitMQ transport
    builder.Services.AddRabbitMQTransport(_rabbitMqConnection);

    // Trace store
    builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

    // Ordered stream processor
    builder.Services.AddSingleton<OrderedStreamProcessor>();

    // Test-specific routing
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRoutingStrategy>(
      new TestRoutingStrategy(_testId));

    // Database readiness (schema created before host starts)
    builder.Services.AddSingleton<IDatabaseReadinessCheck>(new DefaultDatabaseReadinessCheck());

    // Clear static callbacks to avoid contamination from other assemblies
    ServiceRegistrationCallbacks.Dispatcher = null;
    ServiceRegistrationCallbacks.PerspectiveServices = null;

    // Explicitly call module initializer (may not run automatically in test context)
    Generated.GeneratedModelRegistration.Initialize();

    // Whizbang + EFCore
    _ = builder.Services
      .AddWhizbang()
      .WithEFCore<LifecycleTestDbContext>()
      .WithDriver.Postgres;

    // Global scope for tests (no tenant filtering)
    builder.Services.Configure<WhizbangCoreOptions>(o => o.DefaultQueryScope = QueryScope.Global);

    // Generated registrations (receptors, dispatcher, lifecycle deserializer)
    Generated.DispatcherRegistrations.AddReceptors(builder.Services);
    Generated.DispatcherRegistrations.AddWhizbangLifecycleMessageDeserializer(builder.Services);
    builder.Services.AddSingleton<IEventTypeProvider, MockEventTypeProvider>();

    // Security (allow anonymous for tests)
    builder.Services.Replace(ServiceDescriptor.Singleton(
      new Whizbang.Core.Security.MessageSecurityOptions { AllowAnonymous = true }));

    // Topic registry
    builder.Services.AddSingleton<Whizbang.Core.Routing.ITopicRegistry>(
      new Generated.TopicRegistry());

    // Dispatcher
    Generated.DispatcherRegistrations.AddWhizbangDispatcher(builder.Services);

    // Perspective stores (manual registration to avoid ModelRegistrationRegistry issues)
    _registerPerspectiveStore<MockBatchModelA>(builder.Services, "wh_per_mock_batch_model_a");
    _registerPerspectiveStore<MockBatchModelB>(builder.Services, "wh_per_mock_batch_model_b");
    _registerPerspectiveStore<MockBatchModelC>(builder.Services, "wh_per_mock_batch_model_c");
    _registerPerspectiveStore<MockBatchModelD>(builder.Services, "wh_per_mock_batch_model_d");
    _registerPerspectiveStore<MockBatchModelE>(builder.Services, "wh_per_mock_batch_model_e");

    // Perspective runners
    Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(builder.Services);
    builder.Services.AddScoped<MockBatchPerspectiveA>();
    builder.Services.AddScoped<MockBatchPerspectiveB>();
    builder.Services.AddScoped<MockBatchPerspectiveC>();
    builder.Services.AddScoped<MockBatchPerspectiveD>();
    builder.Services.AddScoped<MockBatchPerspectiveE>();

    // Publish strategy
    builder.Services.AddSingleton<IMessagePublishStrategy>(sp =>
      new TransportPublishStrategy(
        sp.GetRequiredService<ITransport>(),
        new DefaultTransportReadinessCheck()));

    // Work channel
    builder.Services.AddSingleton<IWorkChannelWriter, WorkChannelWriter>();

    // Instant completion for tests
    builder.Services.AddSingleton<IPerspectiveCompletionStrategy, InstantCompletionStrategy>();

    // Worker options (fast polling for tests)
    builder.Services.Configure<WorkCoordinatorPublisherOptions>(options => {
      options.PollingIntervalMilliseconds = 100;
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = true;
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;
    });

    builder.Services.Configure<PerspectiveWorkerOptions>(options => {
      options.PollingIntervalMilliseconds = 100;
      options.LeaseSeconds = 300;
      options.StaleThresholdSeconds = 600;
      options.DebugMode = true;
      options.PartitionCount = 10000;
      options.IdleThresholdPolls = 2;
    });

    // Background workers
    builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();
    builder.Services.AddHostedService<PerspectiveWorker>();

    // RabbitMQ consumer with test-specific routing
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination(
      Address: $"mockbatchtest-{_testId}",
      RoutingKey: $"lifecycle-mockbatch-queue-{_testId}",
      Metadata: new Dictionary<string, JsonElement> {
        ["SubscriberName"] = JsonDocument.Parse("\"lifecycle-test\"").RootElement.Clone()
      }));
    consumerOptions.Destinations.Add(new TransportDestination(
      Address: $"mockbatchnoise-{_testId}",
      RoutingKey: $"lifecycle-mocknoise-queue-{_testId}",
      Metadata: new Dictionary<string, JsonElement> {
        ["SubscriberName"] = JsonDocument.Parse("\"lifecycle-test\"").RootElement.Clone()
      }));
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
        sp.GetRequiredService<ILogger<TransportConsumerWorker>>()));

    // Logging
    builder.Services.AddLogging(logging => {
      logging.SetMinimumLevel(LogLevel.Information);
      logging.AddConsole();
    });

    return builder.Build();
  }

  private async Task _initializeDatabaseSchemaAsync(CancellationToken ct) {
    Console.WriteLine("[LifecycleFixture] Creating database...");
    await _createDatabaseAsync(ct);

    // Initialize Whizbang infrastructure (schema, tables, functions, perspective associations)
    using var scope = _host!.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<LifecycleTestDbContext>();
    var logger = scope.ServiceProvider.GetService<ILogger<LifecycleTestDbContext>>();
    await context.EnsureWhizbangDatabaseInitializedAsync(logger, cancellationToken: ct);
    Console.WriteLine("[LifecycleFixture] Database schema created");
  }

  private async Task _createDatabaseAsync(CancellationToken ct) {
    var connectionBuilder = new NpgsqlConnectionStringBuilder(_postgresConnection);
    var dbName = connectionBuilder.Database;

    // Connect to postgres db to create the test database
    connectionBuilder.Database = "postgres";
    await using var connection = new NpgsqlConnection(connectionBuilder.ConnectionString);
    await connection.OpenAsync(ct);

    // Create database if not exists
    await using var checkCommand = connection.CreateCommand();
    checkCommand.CommandText = $"SELECT 1 FROM pg_database WHERE datname = '{dbName}'";
    var exists = await checkCommand.ExecuteScalarAsync(ct);
    if (exists == null) {
      await using var createCommand = connection.CreateCommand();
      createCommand.CommandText = $"CREATE DATABASE \"{dbName}\"";
      await createCommand.ExecuteNonQueryAsync(ct);
    }
  }

  private async Task _dropDatabaseAsync() {
    try {
      var connectionBuilder = new NpgsqlConnectionStringBuilder(_postgresConnection);
      var dbName = connectionBuilder.Database;
      connectionBuilder.Database = "postgres";
      await using var connection = new NpgsqlConnection(connectionBuilder.ConnectionString);
      await connection.OpenAsync();

      await using var terminateCommand = connection.CreateCommand();
      terminateCommand.CommandText = $@"
        SELECT pg_terminate_backend(pid) FROM pg_stat_activity
        WHERE datname = '{dbName}' AND pid <> pg_backend_pid();
      ";
      await terminateCommand.ExecuteNonQueryAsync();

      await using var dropCommand = connection.CreateCommand();
      dropCommand.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\"";
      await dropCommand.ExecuteNonQueryAsync();
    } catch (Exception ex) {
      Console.WriteLine($"[LifecycleFixture] Warning: Failed to drop database: {ex.Message}");
    }
  }

  private async Task _deleteQueueAsync(string queueName) {
    try {
      await _managementClient.DeleteAsync($"/api/queues/%2F/{queueName}");
    } catch { /* cleanup best effort */ }
  }

  private async Task _deleteExchangeAsync(string exchangeName) {
    try {
      await _managementClient.DeleteAsync($"/api/exchanges/%2F/{exchangeName}");
    } catch { /* cleanup best effort */ }
  }

  private static void _registerPerspectiveStore<TModel>(IServiceCollection services, string tableName)
    where TModel : class {
    services.AddScoped<IPerspectiveStore<TModel>>(sp => {
      var context = sp.GetRequiredService<LifecycleTestDbContext>();
      return new EFCorePostgresPerspectiveStore<TModel>(context, tableName);
    });
  }
}

/// <summary>
/// Test-specific routing strategy that appends test ID to topic names.
/// </summary>
internal sealed class TestRoutingStrategy(string testId) : Whizbang.Core.Routing.ITopicRoutingStrategy {
  public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
    return $"{baseTopic}-{testId}";
  }
}
