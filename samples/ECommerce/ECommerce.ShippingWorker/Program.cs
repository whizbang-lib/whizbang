using ECommerce.Contracts.Generated;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Transports.AzureServiceBus;

var builder = Host.CreateApplicationBuilder(args);

// Add service defaults (telemetry, health checks, service discovery)
builder.AddServiceDefaults();

// Get connection strings from Aspire configuration
var postgresConnection = builder.Configuration.GetConnectionString("shippingdb")
    ?? throw new InvalidOperationException("PostgreSQL connection string 'shippingdb' not found");
var serviceBusConnection = builder.Configuration.GetConnectionString("servicebus")
    ?? throw new InvalidOperationException("Azure Service Bus connection string 'servicebus' not found");

// Register Whizbang Postgres stores (with automatic schema initialization)
builder.Services.AddWhizbangPostgres(postgresConnection, initializeSchema: true);
builder.Services.AddWhizbangPostgresHealthChecks();

// Register Azure Service Bus transport
builder.Services.AddAzureServiceBusTransport(serviceBusConnection, ECommerce.Contracts.Generated.WhizbangJsonContext.Default);
builder.Services.AddAzureServiceBusHealthChecks();

// Add trace store for observability
builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

// Register Whizbang dispatcher with source-generated receptors
builder.Services.AddReceptors();
builder.Services.AddWhizbangDispatcher();

// Register outbox publisher worker for reliable event publishing
builder.Services.AddHostedService<OutboxPublisherWorker>();

// Configure Service Bus consumer to receive events from other services
var consumerOptions = new ServiceBusConsumerOptions();
consumerOptions.Subscriptions.Add(new TopicSubscription("orders", "shipping-service"));
builder.Services.AddSingleton(consumerOptions);
builder.Services.AddHostedService<ServiceBusConsumerWorker>();

var host = builder.Build();
host.Run();
