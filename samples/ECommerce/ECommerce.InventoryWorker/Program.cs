using ECommerce.Contracts.Generated;
using ECommerce.InventoryWorker;
using ECommerce.InventoryWorker.Lenses;
using ECommerce.InventoryWorker.Services;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Transports.AzureServiceBus;

var builder = Host.CreateApplicationBuilder(args);

// Add service defaults (telemetry, health checks, service discovery)
builder.AddServiceDefaults();

// Get connection strings from Aspire configuration
var serviceBusConnection = builder.Configuration.GetConnectionString("servicebus")
    ?? throw new InvalidOperationException("Azure Service Bus connection string 'servicebus' not found");

// TODO: Migrate to EF Core implementations
// The InventoryWorker needs EF Core implementations of:
// - Event Store (for storing inventory events)
// - Inbox (for reliable event consumption from Service Bus)
// - Outbox (for reliable event publishing)
// - Perspectives (for inventory materialized views)
// Migration plan: builder.Services.AddWhizbang().WithEFCore<InventoryDbContext>().WithDriver.Postgres

// Register Azure Service Bus transport
builder.Services.AddAzureServiceBusTransport(serviceBusConnection, ECommerce.Contracts.Generated.WhizbangJsonContext.Default);
builder.Services.AddAzureServiceBusHealthChecks();

// Add trace store for observability
builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

// TODO: Re-enable after EF Core implementations
// builder.Services.AddReceptors();
// builder.Services.AddWhizbangAggregateIdExtractor();
// builder.Services.AddWhizbangPerspectiveInvoker();
// builder.Services.AddWhizbangDispatcher();

// TODO: Re-enable after EF Core perspectives
// builder.Services.AddSingleton<IProductLens, ProductLens>();
// builder.Services.AddSingleton<IInventoryLens, InventoryLens>();

// TODO: Re-enable after EF Core dispatcher
// ProductSeedService depends on IDispatcher to send CreateProductCommand
// builder.Services.AddHostedService<ProductSeedService>();

// TODO: Re-enable after EF Core outbox
// builder.Services.AddHostedService<OutboxPublisherWorker>();

// TODO: Re-enable after EF Core inbox
// var consumerOptions = new ServiceBusConsumerOptions();
// consumerOptions.Subscriptions.Add(new TopicSubscription("orders", "inventory-service"));
// builder.Services.AddSingleton(consumerOptions);
// builder.Services.AddHostedService<ServiceBusConsumerWorker>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
