using ECommerce.Contracts.Generated;
using ECommerce.InventoryWorker;
using ECommerce.InventoryWorker.Lenses;
using ECommerce.InventoryWorker.Services;
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
var postgresConnection = builder.Configuration.GetConnectionString("inventorydb")
    ?? throw new InvalidOperationException("PostgreSQL connection string 'inventorydb' not found");
var serviceBusConnection = builder.Configuration.GetConnectionString("servicebus")
    ?? throw new InvalidOperationException("Azure Service Bus connection string 'servicebus' not found");

// Register Whizbang Postgres stores (with automatic schema initialization)
var jsonOptions = ECommerce.Contracts.Generated.WhizbangJsonContext.CreateOptions();
builder.Services.AddWhizbangPostgres(postgresConnection, jsonOptions, initializeSchema: true);
builder.Services.AddWhizbangPostgresHealthChecks();

// Register Azure Service Bus transport
builder.Services.AddAzureServiceBusTransport(serviceBusConnection, ECommerce.Contracts.Generated.WhizbangJsonContext.Default);
builder.Services.AddAzureServiceBusHealthChecks();

// Add trace store for observability
builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

// Register Whizbang dispatcher with source-generated receptors
builder.Services.AddReceptors();
builder.Services.AddWhizbangAggregateIdExtractor(); // For extracting aggregate IDs from events
builder.Services.AddWhizbangPerspectiveInvoker(); // For invoking perspectives on events
builder.Services.AddWhizbangDispatcher();

// Register lenses for querying materialized views
builder.Services.AddSingleton<IProductLens, ProductLens>();
builder.Services.AddSingleton<IInventoryLens, InventoryLens>();

// Register product seeding service (runs on startup)
builder.Services.AddHostedService<ProductSeedService>();

// Register outbox publisher worker for reliable event publishing
builder.Services.AddHostedService<OutboxPublisherWorker>();

// Configure Service Bus consumer to receive events from other services
var consumerOptions = new ServiceBusConsumerOptions();
consumerOptions.Subscriptions.Add(new TopicSubscription("orders", "inventory-service"));
builder.Services.AddSingleton(consumerOptions);
builder.Services.AddHostedService<ServiceBusConsumerWorker>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
