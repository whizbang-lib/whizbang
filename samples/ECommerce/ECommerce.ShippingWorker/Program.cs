using ECommerce.Contracts.Generated;
using ECommerce.ShippingWorker;
using ECommerce.ShippingWorker.Generated;
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
var postgresConnection = builder.Configuration.GetConnectionString("shippingdb")
    ?? throw new InvalidOperationException("PostgreSQL connection string 'shippingdb' not found");
var serviceBusConnection = builder.Configuration.GetConnectionString("servicebus")
    ?? throw new InvalidOperationException("Azure Service Bus connection string 'servicebus' not found");

// Register ECommerce message types with JsonContextRegistry
// This must be done before creating JsonSerializerOptions (line 57)
ECommerce.Contracts.ECommerceJsonContext.Register();

// Register Azure Service Bus transport
// Note: Transport uses JsonContextRegistry internally for serialization
builder.Services.AddAzureServiceBusTransport(serviceBusConnection);
builder.Services.AddAzureServiceBusHealthChecks();

// Add trace store for observability
builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

// Register EF Core DbContext for Inbox/Outbox/EventStore
builder.Services.AddDbContext<ShippingDbContext>(options =>
  options.UseNpgsql(postgresConnection));

// Register unified Whizbang API with EF Core Postgres driver
_ = builder.Services
  .AddWhizbang()
  .WithEFCore<ShippingDbContext>()
  .WithDriver.Postgres;

// Register Whizbang generated services
builder.Services.AddReceptors();
builder.Services.AddWhizbangDispatcher();
builder.Services.AddWhizbangAggregateIdExtractor();

// Register transport readiness check (ServiceBusReadinessCheck for Azure Service Bus)
builder.Services.AddSingleton<ITransportReadinessCheck>(sp => {
  var transport = sp.GetRequiredService<ITransport>();
  var client = sp.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>();
  var logger = sp.GetRequiredService<ILogger<Whizbang.Hosting.Azure.ServiceBus.ServiceBusReadinessCheck>>();
  return new Whizbang.Hosting.Azure.ServiceBus.ServiceBusReadinessCheck(transport, client, logger);
});

// Register IMessagePublishStrategy for WorkCoordinatorPublisherWorker
var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
builder.Services.AddSingleton<IMessagePublishStrategy>(sp =>
  new TransportPublishStrategy(
    sp.GetRequiredService<ITransport>(),
    sp.GetRequiredService<ITransportReadinessCheck>()
  )
);

// WorkCoordinator publisher - atomic coordination with lease-based work claiming
builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();

// Service Bus consumer
var consumerOptions = new ServiceBusConsumerOptions();
consumerOptions.Subscriptions.Add(new TopicSubscription("orders", "sub-shipping-orders"));
builder.Services.AddSingleton(consumerOptions);
builder.Services.AddHostedService<ServiceBusConsumerWorker>();

var host = builder.Build();

// Initialize database schema on startup
// Creates Inbox/Outbox/EventStore tables + PostgreSQL functions
using (var scope = host.Services.CreateScope()) {
  var dbContext = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();
  var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
  await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger);
}

host.Run();
