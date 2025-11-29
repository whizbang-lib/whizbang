using ECommerce.Contracts.Generated;
using ECommerce.NotificationWorker;
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
// The NotificationWorker needs EF Core implementations of:
// - Inbox (for reliable event consumption)
// - Outbox (for reliable notification sending)
// Migration plan: builder.Services.AddWhizbang().WithEFCore<NotificationDbContext>().WithDriver.Postgres

// Register Azure Service Bus transport
builder.Services.AddAzureServiceBusTransport(serviceBusConnection, ECommerce.Contracts.Generated.WhizbangJsonContext.Default);
builder.Services.AddAzureServiceBusHealthChecks();

// Add trace store for observability
builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

// TODO: Re-enable after EF Core implementations
// builder.Services.AddReceptors();
// builder.Services.AddWhizbangDispatcher();
// builder.Services.AddHostedService<OutboxPublisherWorker>();

// TODO: Re-enable after EF Core inbox
// var consumerOptions = new ServiceBusConsumerOptions();
// consumerOptions.Subscriptions.Add(new TopicSubscription("orders", "notification-service"));
// builder.Services.AddSingleton(consumerOptions);
// builder.Services.AddHostedService<ServiceBusConsumerWorker>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
