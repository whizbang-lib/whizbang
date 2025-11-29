using ECommerce.Contracts.Generated;
// using ECommerce.OrderService.API.GraphQL.Mutations; // TODO: Re-enable after EF Core dispatcher
using ECommerce.OrderService.API.GraphQL.Queries;
// using FastEndpoints; // TODO: Re-enable after EF Core dispatcher
// using FastEndpoints.Swagger; // TODO: Re-enable after EF Core dispatcher
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Transports.AzureServiceBus;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults (telemetry, health checks, service discovery)
builder.AddServiceDefaults();

// Add HotChocolate GraphQL
builder.Services
  .AddGraphQLServer()
  // TODO: Re-enable after EF Core dispatcher is implemented
  // OrderMutations.CreateOrderAsync depends on IDispatcher
  // .AddMutationType<OrderMutations>()
  .AddQueryType<OrderQueries>(); // OrderQueries is fine (no IDispatcher dependency)

// TODO: Re-enable after EF Core dispatcher is implemented
// FastEndpoints - CreateOrderEndpoint depends on IDispatcher
// builder.Services.AddFastEndpoints();
// builder.Services.SwaggerDocument();

// Add OpenAPI for traditional endpoints
builder.Services.AddOpenApi();

// Get connection strings from Aspire configuration
var serviceBusConnection = builder.Configuration.GetConnectionString("servicebus")
    ?? throw new InvalidOperationException("Azure Service Bus connection string 'servicebus' not found");

// TODO: Migrate to EF Core implementations
// The following Whizbang infrastructure needs EF Core implementations:
// - Event Store (for event sourcing)
// - Inbox (for reliable command/event consumption)
// - Outbox (for reliable event publishing)
// - Outbox Publisher Worker
//
// Currently using Dapper implementations (Whizbang.Data.Dapper.Postgres).
// Migration plan:
// 1. Implement IEventStore<T> using EF Core with PerspectiveRow-like pattern
// 2. Implement IInbox/IOutbox using EF Core
// 3. Create OutboxPublisherWorker that works with EF Core
// 4. Update this service to use: builder.Services.AddWhizbang().WithEFCore<OrderDbContext>()

// Register Azure Service Bus transport
builder.Services.AddAzureServiceBusTransport(serviceBusConnection, ECommerce.Contracts.Generated.WhizbangJsonContext.Default);
builder.Services.AddAzureServiceBusHealthChecks();

// Add trace store for observability
builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

// TODO: Re-enable after EF Core event store is implemented
// Register Whizbang dispatcher with source-generated receptors
// builder.Services.AddReceptors();
// builder.Services.AddWhizbangDispatcher();

// TODO: Re-enable after EF Core outbox is implemented
// Register outbox publisher worker for reliable event publishing
// builder.Services.AddHostedService<OutboxPublisherWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment()) {
  app.MapOpenApi();
  // TODO: Re-enable after EF Core dispatcher is implemented
  // app.UseSwaggerGen(); // FastEndpoints Swagger UI
}

app.UseHttpsRedirection();

// TODO: Re-enable after EF Core dispatcher is implemented
// FastEndpoints (REST API at /api/*)
// app.UseFastEndpoints(config => {
//   config.Endpoints.RoutePrefix = "api";
// });

// HotChocolate GraphQL (at /graphql)
app.MapGraphQL("/graphql");

// Aspire health checks and diagnostics
app.MapDefaultEndpoints();

app.Run();
