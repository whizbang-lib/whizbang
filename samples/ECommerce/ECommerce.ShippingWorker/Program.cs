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
#if AZURESERVICEBUS
using Whizbang.Transports.AzureServiceBus;
#elif RABBITMQ
using Whizbang.Transports.RabbitMQ;
#endif

var builder = Host.CreateApplicationBuilder(args);

// Add service defaults (telemetry, health checks, service discovery)
builder.AddServiceDefaults();

// Get connection strings from Aspire configuration
var postgresConnection = builder.Configuration.GetConnectionString("shippingdb")
    ?? throw new InvalidOperationException("PostgreSQL connection string 'shippingdb' not found");

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

// Transport consumer
var consumerOptions = new TransportConsumerOptions();

#if AZURESERVICEBUS
consumerOptions.Destinations.Add(new TransportDestination(
  Address: "orders",
  RoutingKey: "sub-shipping-orders"  // Azure Service Bus subscription name
));

#elif RABBITMQ
consumerOptions.Destinations.Add(new TransportDestination(
  Address: "orders",                   // RabbitMQ exchange name
  RoutingKey: "shipping-worker-queue"  // RabbitMQ queue name
));

#endif

builder.Services.AddSingleton(consumerOptions);
builder.Services.AddHostedService<TransportConsumerWorker>();

var host = builder.Build();

// Initialize database schema on startup
// Creates Inbox/Outbox/EventStore tables + PostgreSQL functions
using (var scope = host.Services.CreateScope()) {
  var dbContext = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();
  var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
  await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger);
}

host.Run();
