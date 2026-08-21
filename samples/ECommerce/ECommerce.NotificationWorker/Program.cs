using ECommerce.Contracts.Generated;
using ECommerce.NotificationWorker;
using ECommerce.NotificationWorker.Generated;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres;
#if AZURESERVICEBUS
using Whizbang.Transports.AzureServiceBus;
#elif RABBITMQ
using Whizbang.Transports.RabbitMQ;
#endif

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var postgresConnection = builder.Configuration.GetConnectionString("notificationdb")
    ?? throw new InvalidOperationException("PostgreSQL connection string 'notificationdb' not found");

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

builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

builder.Services.AddDbContext<NotificationDbContext>(options =>
  options.UseNpgsql(postgresConnection));

// WithRouting() configures message routing and AddTransportConsumer() auto-generates subscriptions.
//
// MIGRATION REFERENCE — this worker deliberately runs the MID-MIGRATION topology while the rest of
// the fleet runs the default. KeepSharedInbox() is the explicit inverse of the default retirement:
// the per-namespace command inboxes are subscribed AND the legacy catch-all "inbox" subscription is
// kept, so this service is a strict SUPERSET — it receives whether a publisher has been upgraded
// (per-namespace inbox) or not (catch-all). That is the shape a fleet runs in while it migrates one
// service at a time; drop the call once every publisher is upgraded. The publisher-side counterpart
// is RouteCommandNamespaceToInbox(ns) — flip one namespace per deploy — with
// RouteNoCommandNamespacesToInbox() as the full rollback. Both are also configuration-bindable
// (Whizbang:Routing:RetireSharedInbox / :RouteAllCommandNamespacesToInbox / :CommandNamespacesToInbox),
// so a migration step or a rollback needs no code change.
_ = builder.Services
  .AddWhizbang()
  .WithRouting(routing => {
    routing
      .OwnDomains("ecommerce.notification.commands")
      .SubscribeTo("ecommerce.orders.events")
      .KeepSharedInbox();
  })
  .WithEFCore<NotificationDbContext>()
  .WithDriver.Postgres
  .AddTransportConsumer();

builder.Services.AddReceptors();
builder.Services.AddWhizbangDispatcher();

// WorkCoordinator publisher - atomic coordination with lease-based work claiming
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// Initialize database schema on startup
// Creates Inbox/Outbox/EventStore tables + PostgreSQL functions
using (var scope = host.Services.CreateScope()) {
  var dbContext = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
  var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
  await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger);
}

host.Run();
