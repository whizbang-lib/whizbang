using ECommerce.Contracts.Generated;
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
using Whizbang.Transports.AzureServiceBus;

var builder = Host.CreateApplicationBuilder(args);

// Add service defaults (telemetry, health checks, service discovery)
builder.AddServiceDefaults();

// Get connection strings from Aspire configuration
var postgresConnection = builder.Configuration.GetConnectionString("inventorydb")
    ?? throw new InvalidOperationException("PostgreSQL connection string 'inventorydb' not found");
var serviceBusConnection = builder.Configuration.GetConnectionString("servicebus")
    ?? throw new InvalidOperationException("Azure Service Bus connection string 'servicebus' not found");

// IMPORTANT: Force Contracts assembly to load BEFORE creating JsonSerializerOptions
// This ensures ECommerce.Contracts.ECommerceJsonContext ModuleInitializer runs
// and registers all ECommerce message types with JsonContextRegistry
_ = typeof(ECommerce.Contracts.Commands.CreateProductCommand).Assembly;

// Register Azure Service Bus transport
// Note: Transport uses JsonContextRegistry internally for serialization
builder.Services.AddAzureServiceBusTransport(serviceBusConnection);
builder.Services.AddAzureServiceBusHealthChecks();

// Add trace store for observability
builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

// Register service instance provider (MUST be before workers that depend on it)
builder.Services.AddSingleton<IServiceInstanceProvider, ServiceInstanceProvider>();

// Register OrderedStreamProcessor for message ordering in ServiceBusConsumerWorker
builder.Services.AddSingleton<OrderedStreamProcessor>();

// Register EF Core DbContext for Inbox/Outbox/EventStore
builder.Services.AddDbContext<InventoryDbContext>(options =>
  options.UseNpgsql(postgresConnection));

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

// Register transport readiness check (ServiceBusReadinessCheck for Azure Service Bus)
builder.Services.AddSingleton<ITransportReadinessCheck>(sp => {
  var transport = sp.GetRequiredService<ITransport>();
  var client = sp.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>();
  var logger = sp.GetRequiredService<ILogger<Whizbang.Hosting.Azure.ServiceBus.ServiceBusReadinessCheck>>();
  return new Whizbang.Hosting.Azure.ServiceBus.ServiceBusReadinessCheck(transport, client, logger);
});

// NOTE: No perspectives in this service (OLD OrderInventoryPerspective was removed)
// If you add perspectives in the future, call builder.Services.AddPerspectiveRunners()

// Register dispatcher for sending commands
builder.Services.AddWhizbangDispatcher();

// Register lenses (readonly repositories using EF Core ILensQuery)
builder.Services.AddScoped<IProductLens, ProductLens>();
builder.Services.AddScoped<IInventoryLens, InventoryLens>();

// Create JsonSerializerOptions from global registry (required by workers)
var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

// Register IMessagePublishStrategy for WorkCoordinatorPublisherWorker
builder.Services.AddSingleton<IMessagePublishStrategy>(sp =>
  new TransportPublishStrategy(
    sp.GetRequiredService<ITransport>(),
    sp.GetRequiredService<ITransportReadinessCheck>()
  )
);

// Service Bus consumer - receives events and commands
var consumerOptions = new ServiceBusConsumerOptions();
// Event subscription - receives all events published to "products" topic
consumerOptions.Subscriptions.Add(new TopicSubscription("products", "sub-inventory-products"));
// Inbox subscription - receives point-to-point messages with CorrelationFilter
// Note: Subscription name and destination filter must match those registered in AppHost
consumerOptions.Subscriptions.Add(new TopicSubscription("inbox", "sub-inbox-inventory", "inventory-service"));
builder.Services.AddSingleton(consumerOptions);
builder.Services.AddHostedService<ServiceBusConsumerWorker>(sp =>
  new ServiceBusConsumerWorker(
    sp.GetRequiredService<IServiceInstanceProvider>(),
    sp.GetRequiredService<ITransport>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    jsonOptions,
    sp.GetRequiredService<ILogger<ServiceBusConsumerWorker>>(),
    sp.GetRequiredService<OrderedStreamProcessor>(),
    consumerOptions
  )
);

// WorkCoordinator publisher - atomic coordination with lease-based work claiming
// Options configured via appsettings.json "WorkCoordinatorPublisher" section
// Use AddOptions().Bind() for AOT compatibility (instead of Configure<T>())
builder.Services.AddOptions<WorkCoordinatorPublisherOptions>()
  .Bind(builder.Configuration.GetSection("WorkCoordinatorPublisher"));
builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();

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
