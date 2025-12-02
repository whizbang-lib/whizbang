using ECommerce.Contracts.Generated;
using ECommerce.InventoryWorker;
using ECommerce.InventoryWorker.Lenses;
using ECommerce.InventoryWorker.Services;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using Microsoft.EntityFrameworkCore;
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

// Register Azure Service Bus transport
builder.Services.AddAzureServiceBusTransport(serviceBusConnection, ECommerce.Contracts.Generated.WhizbangJsonContext.Default);
builder.Services.AddAzureServiceBusHealthChecks();

// Add trace store for observability
builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

// Register EF Core DbContext for Inbox/Outbox/EventStore
builder.Services.AddDbContext<InventoryDbContext>(options =>
  options.UseNpgsql(postgresConnection));

// Register unified Whizbang API with EF Core Postgres driver
// This automatically registers ALL infrastructure:
// - IInbox, IOutbox, IEventStore (using EF Core implementations)
// Source generator discovers models from InventoryDbContext (none in this service)
_ = builder.Services
  .AddWhizbang()
  .WithEFCore<InventoryDbContext>()
  .WithDriver.Postgres;

// Register Whizbang generated services (from ECommerce.Contracts)
builder.Services.AddReceptors();
builder.Services.AddWhizbangAggregateIdExtractor();

// Register lenses (readonly repositories)
builder.Services.AddSingleton<IProductLens, ProductLens>();
builder.Services.AddSingleton<IInventoryLens, InventoryLens>();

// Service Bus consumer - receives events and commands
var consumerOptions = new ServiceBusConsumerOptions();
// Event subscription - receives all events published to "products" topic
consumerOptions.Subscriptions.Add(new TopicSubscription("products", "inventory-service"));
// Inbox subscription - receives point-to-point messages with SQL filter
// Note: Subscription name must match the one registered in AppHost
consumerOptions.Subscriptions.Add(new TopicSubscription("inbox", "inbox-inventory", "Destination = 'inventory-service'"));
builder.Services.AddSingleton(consumerOptions);
builder.Services.AddHostedService<ServiceBusConsumerWorker>();

// Outbox publisher - publishes pending outbox messages to Service Bus
builder.Services.AddHostedService<OutboxPublisherWorker>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
