using ECommerce.Contracts.Generated;
using ECommerce.OrderService.API;
using ECommerce.OrderService.API.Generated;
using ECommerce.OrderService.API.GraphQL.Mutations;
using ECommerce.OrderService.API.GraphQL.Queries;
using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres;
#if AZURESERVICEBUS
using Whizbang.Transports.AzureServiceBus;
#elif RABBITMQ
using Whizbang.Transports.RabbitMQ;
#endif

var builder = WebApplication.CreateBuilder(args);

// Add service defaults (telemetry, health checks, service discovery)
builder.AddServiceDefaults();

// Add HotChocolate GraphQL
builder.Services
  .AddGraphQLServer()
  .AddMutationType<OrderMutations>()
  .AddQueryType<OrderQueries>();

// Add FastEndpoints for REST API
builder.Services.AddFastEndpoints();
builder.Services.SwaggerDocument();

// Add OpenAPI for traditional endpoints
builder.Services.AddOpenApi();

// Get connection strings from Aspire configuration
var postgresConnection = builder.Configuration.GetConnectionString("ordersdb")
    ?? throw new InvalidOperationException("PostgreSQL connection string 'ordersdb' not found");

#if AZURESERVICEBUS
var serviceBusConnection = builder.Configuration.GetConnectionString("servicebus")
    ?? throw new InvalidOperationException("Azure Service Bus connection string 'servicebus' not found");

#elif RABBITMQ
var rabbitMqConnection = builder.Configuration.GetConnectionString("rabbitmq")
    ?? throw new InvalidOperationException("RabbitMQ connection string 'rabbitmq' not found");

#endif

// Register transport
#if AZURESERVICEBUS
// Note: Transport uses JsonContextRegistry internally for serialization
builder.Services.AddAzureServiceBusTransport(serviceBusConnection);
builder.Services.AddAzureServiceBusHealthChecks();

#elif RABBITMQ
builder.Services.AddRabbitMQTransport(rabbitMqConnection);
builder.Services.AddRabbitMQHealthChecks();

#endif

// Add trace store for observability
builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

// Register service instance provider (MUST be before workers that depend on it)
builder.Services.AddSingleton<IServiceInstanceProvider, ServiceInstanceProvider>();

// Register OrderedStreamProcessor for message ordering in transport consumer workers
builder.Services.AddSingleton<OrderedStreamProcessor>();

// Register EF Core DbContext for Inbox/Outbox/EventStore
builder.Services.AddDbContext<OrderDbContext>(options =>
  options.UseNpgsql(postgresConnection));

// Register unified Whizbang API with EF Core Postgres driver
// This automatically registers ALL infrastructure:
// - IInbox, IOutbox, IEventStore (using EF Core implementations)
// Source generator discovers infrastructure from [WhizbangDbContext] attribute
_ = builder.Services
  .AddWhizbang()
  .WithEFCore<OrderDbContext>()
  .WithDriver.Postgres;

// Register Whizbang generated services (from ECommerce.Contracts)
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

var app = builder.Build();

// Initialize database schema on startup
// Uses generated EnsureWhizbangDatabaseInitializedAsync() extension method
// Creates Inbox/Outbox/EventStore tables + PostgreSQL functions
using (var scope = app.Services.CreateScope()) {
  var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
  var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
  await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment()) {
  app.MapOpenApi();
  app.UseSwaggerGen(); // FastEndpoints Swagger UI
}

app.UseHttpsRedirection();

// FastEndpoints (REST API at /api/*)
app.UseFastEndpoints(config => {
  config.Endpoints.RoutePrefix = "api";
});

// HotChocolate GraphQL (at /graphql)
app.MapGraphQL("/graphql");

// Aspire health checks and diagnostics
app.MapDefaultEndpoints();

app.Run();
