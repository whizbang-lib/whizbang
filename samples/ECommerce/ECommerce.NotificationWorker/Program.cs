using ECommerce.Contracts.Generated;
using ECommerce.NotificationWorker;
using ECommerce.NotificationWorker.Generated;
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

builder.AddServiceDefaults();

var postgresConnection = builder.Configuration.GetConnectionString("notificationdb")
    ?? throw new InvalidOperationException("PostgreSQL connection string 'notificationdb' not found");
var serviceBusConnection = builder.Configuration.GetConnectionString("servicebus")
    ?? throw new InvalidOperationException("Azure Service Bus connection string 'servicebus' not found");

// Register Azure Service Bus transport
// Note: Transport uses JsonContextRegistry internally for serialization
builder.Services.AddAzureServiceBusTransport(serviceBusConnection);
builder.Services.AddAzureServiceBusHealthChecks();
builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

builder.Services.AddDbContext<NotificationDbContext>(options =>
  options.UseNpgsql(postgresConnection));

_ = builder.Services
  .AddWhizbang()
  .WithEFCore<NotificationDbContext>()
  .WithDriver.Postgres;

builder.Services.AddReceptors();
builder.Services.AddWhizbangDispatcher();
builder.Services.AddWhizbangAggregateIdExtractor();

// Register transport readiness check (ServiceBusReadinessCheck for Azure Service Bus)
builder.Services.AddSingleton<ITransportReadinessCheck, Whizbang.Hosting.Azure.ServiceBus.ServiceBusReadinessCheck>();

// Register IMessagePublishStrategy for WorkCoordinatorPublisherWorker
var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
builder.Services.AddSingleton<IMessagePublishStrategy>(sp =>
  new TransportPublishStrategy(
    sp.GetRequiredService<ITransport>(),
    jsonOptions,
    sp.GetRequiredService<ITransportReadinessCheck>()
  )
);

// WorkCoordinator publisher - atomic coordination with lease-based work claiming
builder.Services.AddHostedService<WorkCoordinatorPublisherWorker>();

var consumerOptions = new ServiceBusConsumerOptions();
consumerOptions.Subscriptions.Add(new TopicSubscription("orders", "sub-notification-orders"));
builder.Services.AddSingleton(consumerOptions);
builder.Services.AddHostedService<ServiceBusConsumerWorker>();

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
