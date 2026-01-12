using ECommerce.Contracts.Generated;
using ECommerce.Contracts.Lenses;
using ECommerce.InventoryWorker;
using ECommerce.InventoryWorker.Generated;
using ECommerce.InventoryWorker.Lenses;
using ECommerce.InventoryWorker.Services;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres;
#if AZURESERVICEBUS
using Whizbang.Transports.AzureServiceBus;
#elif RABBITMQ
using Whizbang.Transports.RabbitMQ;
#endif

var builder = Host.CreateApplicationBuilder(args);

// Add service defaults (telemetry, health checks, service discovery)
builder.AddServiceDefaults();

// Get connection strings from Aspire configuration
var postgresConnection = builder.Configuration.GetConnectionString("inventorydb")
    ?? throw new InvalidOperationException("PostgreSQL connection string 'inventorydb' not found");

#if AZURESERVICEBUS
var serviceBusConnection = builder.Configuration.GetConnectionString("servicebus")
    ?? throw new InvalidOperationException("Azure Service Bus connection string 'servicebus' not found");

// Register Azure Service Bus transport
// Note: Transport uses JsonContextRegistry internally for serialization
builder.Services.AddAzureServiceBusTransport(serviceBusConnection);
builder.Services.AddAzureServiceBusHealthChecks();

#elif RABBITMQ
var rabbitMqConnection = builder.Configuration.GetConnectionString("rabbitmq")
    ?? throw new InvalidOperationException("RabbitMQ connection string 'rabbitmq' not found");

// Register RabbitMQ transport
builder.Services.AddRabbitMQTransport(rabbitMqConnection);
builder.Services.AddRabbitMQHealthChecks();

#endif

// Add trace store for observability
builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

// Register service instance provider (MUST be before workers that depend on it)
builder.Services.AddSingleton<IServiceInstanceProvider, ServiceInstanceProvider>();

// Register OrderedStreamProcessor for message ordering in ServiceBusConsumerWorker
builder.Services.AddSingleton<OrderedStreamProcessor>();

// Create JsonSerializerOptions from global registry (MUST be registered before data source)
var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

// Register EF Core DbContext with NpgsqlDataSource (required for EnableDynamicJson)
// IMPORTANT: ConfigureJsonOptions() MUST be called BEFORE EnableDynamicJson() (Npgsql bug #5562)
// This registers JSON converters for JSONB serialization (including EnvelopeMetadata, MessageScope)
var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(postgresConnection);
dataSourceBuilder.ConfigureJsonOptions(jsonOptions);
dataSourceBuilder.EnableDynamicJson();
var dataSource = dataSourceBuilder.Build();
builder.Services.AddSingleton(dataSource);

builder.Services.AddDbContext<InventoryDbContext>(options =>
  options.UseNpgsql(dataSource));

// Register unified Whizbang API with EF Core Postgres driver
// This automatically registers ALL infrastructure:
// - IInbox, IOutbox, IEventStore (using EF Core implementations)
// - IPerspectiveStore<T> and ILensQuery<T> for all discovered perspective models
// Source generator discovers ProductDto, InventoryLevelDto from perspective implementations
_ = builder.Services
  .AddWhizbang()
  .WithEFCore<InventoryDbContext>()
  .WithDriver.Postgres;

// Register Whizbang generated services (from ECommerce.Contracts)
builder.Services.AddReceptors();
builder.Services.AddWhizbangAggregateIdExtractor();

// Register transport readiness check
#if AZURESERVICEBUS
builder.Services.AddSingleton<ITransportReadinessCheck>(sp => {
  var transport = sp.GetRequiredService<ITransport>();
  var client = sp.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>();
  var logger = sp.GetRequiredService<ILogger<Whizbang.Hosting.Azure.ServiceBus.ServiceBusReadinessCheck>>();
  return new Whizbang.Hosting.Azure.ServiceBus.ServiceBusReadinessCheck(transport, client, logger);
});

#elif RABBITMQ
builder.Services.AddSingleton<ITransportReadinessCheck>(sp => {
  var connection = sp.GetRequiredService<RabbitMQ.Client.IConnection>();
  return new Whizbang.Hosting.RabbitMQ.RabbitMQReadinessCheck(connection);
});

#endif

// Register generated perspective runners (ProductCatalogPerspective, InventoryLevelsPerspective)
// This registers IPerspectiveRunnerRegistry + all discovered IPerspectiveRunner implementations
builder.Services.AddPerspectiveRunners();

// Register perspective instances (needed by runners)
builder.Services.AddScoped<ECommerce.InventoryWorker.Perspectives.ProductCatalogPerspective>();
builder.Services.AddScoped<ECommerce.InventoryWorker.Perspectives.InventoryLevelsPerspective>();

// Register dispatcher for sending commands
builder.Services.AddWhizbangDispatcher();

// Register lifecycle invoker for lifecycle receptor invocation
builder.Services.AddWhizbangLifecycleInvoker();
builder.Services.AddWhizbangLifecycleMessageDeserializer();

// Register lifecycle receptor registry for runtime receptor registration (used in tests)
builder.Services.AddSingleton<ILifecycleReceptorRegistry, DefaultLifecycleReceptorRegistry>();

// Register lenses (readonly repositories using EF Core ILensQuery)
builder.Services.AddScoped<IProductLens, ProductLens>();
builder.Services.AddScoped<IInventoryLens, InventoryLens>();

// Register IMessagePublishStrategy for WorkCoordinatorPublisherWorker
builder.Services.AddSingleton<IMessagePublishStrategy>(sp =>
  new TransportPublishStrategy(
    sp.GetRequiredService<ITransport>(),
    sp.GetRequiredService<ITransportReadinessCheck>()
  )
);

// Transport consumer - receives events and commands
var consumerOptions = new TransportConsumerOptions();

#if AZURESERVICEBUS
// Event subscription - receives all events published to "products" topic
consumerOptions.Destinations.Add(new TransportDestination("products", "sub-inventory-products"));
// Inbox subscription - receives point-to-point messages with destination filter
consumerOptions.Destinations.Add(new TransportDestination("inbox", "sub-inbox-inventory"));

#elif RABBITMQ
// Event subscription - RabbitMQ queue bound to products exchange
consumerOptions.Destinations.Add(new TransportDestination("products", "inventory-products-queue"));
// Inbox subscription - RabbitMQ queue for direct messages
consumerOptions.Destinations.Add(new TransportDestination("inbox", "inventory-inbox-queue"));

#endif

builder.Services.AddSingleton(consumerOptions);
builder.Services.AddHostedService<TransportConsumerWorker>();

// WorkCoordinator publisher - atomic coordination with lease-based work claiming
// Options configured via appsettings.json "WorkCoordinatorPublisher" section
// Use AddOptions().Bind() for AOT compatibility (instead of Configure<T>())
builder.Services.AddOptions<WorkCoordinatorPublisherOptions>()
  .Bind(builder.Configuration.GetSection("WorkCoordinatorPublisher"));
builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();

// Perspective worker - processes perspective checkpoints using IPerspectiveRunner instances
// Options configured via appsettings.json "PerspectiveWorker" section
builder.Services.AddOptions<PerspectiveWorkerOptions>()
  .Bind(builder.Configuration.GetSection("PerspectiveWorker"));

// Register event type provider for AOT-compatible polymorphic event deserialization
builder.Services.AddSingleton<IEventTypeProvider, ECommerce.Contracts.ECommerceEventTypeProvider>();

builder.Services.AddHostedService<PerspectiveWorker>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// Initialize database schema on startup
// Uses generated EnsureWhizbangDatabaseInitializedAsync() extension method
// Creates Inbox/Outbox/EventStore tables + PostgreSQL functions + PerspectiveRow<T> tables
using (var scope = host.Services.CreateScope()) {
  var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
  var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
  await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger);
}

host.Run();
