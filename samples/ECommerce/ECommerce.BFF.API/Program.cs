using ECommerce.BFF.API;
using ECommerce.BFF.API.Generated;
using ECommerce.BFF.API.GraphQL;
using ECommerce.BFF.API.Hubs;
using ECommerce.BFF.API.Lenses;
using ECommerce.BFF.API.Perspectives;
using ECommerce.Contracts.Events;
using ECommerce.Contracts.Generated;
using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres;
using Whizbang.Data.EFCore.Postgres.Generated;
using Whizbang.Transports.AzureServiceBus;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults (telemetry, health checks, service discovery)
builder.AddServiceDefaults();

// Get connection strings from Aspire configuration
var postgresConnection = builder.Configuration.GetConnectionString("bffdb")
    ?? throw new InvalidOperationException("PostgreSQL connection string 'bffdb' not found");
var serviceBusConnection = builder.Configuration.GetConnectionString("servicebus")
    ?? throw new InvalidOperationException("Azure Service Bus connection string 'servicebus' not found");
var angularUrl = builder.Configuration["services:ui:http:0"]
    ?? builder.Configuration.GetConnectionString("ui")
    ?? "http://localhost:4200";  // Fallback for local development without Aspire

// Register Azure Service Bus transport
builder.Services.AddAzureServiceBusTransport(serviceBusConnection, ECommerce.Contracts.Generated.WhizbangJsonContext.Default);
builder.Services.AddAzureServiceBusHealthChecks();

// Add trace store for observability
builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

// Register EF Core DbContext for perspectives with PostgreSQL
builder.Services.AddDbContext<BffDbContext>(options =>
  options.UseNpgsql(postgresConnection));

// Register unified Whizbang API with EF Core Postgres driver
// Source generator discovers perspective models from BffDbContext
// and automatically registers IPerspectiveStore<T> and ILensQuery<T> for each model
_ = builder.Services
  .AddWhizbang()
  .WithEFCore<BffDbContext>()
  .WithDriver.Postgres;

// Register lenses (readonly repositories - high-level interface)
builder.Services.AddScoped<IOrderLens, OrderLens>();
builder.Services.AddScoped<IProductCatalogLens, ProductCatalogLens>();
builder.Services.AddScoped<IInventoryLevelsLens, InventoryLevelsLens>();

// Add HotChocolate GraphQL server with filtering/sorting/projection support
builder.Services
  .AddGraphQLServer()
  .AddQueryType<CatalogQueries>()
  .AddFiltering()  // Enable WHERE clauses
  .AddSorting()    // Enable ORDER BY clauses
  .AddProjections();  // Enable field selection optimization

// TODO: Add outbox publisher worker when EF Core outbox implementation is ready
// Currently using pure EF Core for perspectives only (no inbox/outbox yet)

// TODO: Re-enable after figuring out perspective integration with dispatcher
// Service Bus consumer - receives ALL events from all services
// var consumerOptions = new ServiceBusConsumerOptions();
// consumerOptions.Subscriptions.Add(new TopicSubscription("orders", "bff-service"));
// builder.Services.AddSingleton(consumerOptions);
// builder.Services.AddHostedService<ServiceBusConsumerWorker>();

// Add FastEndpoints for REST API (AOT-compatible)
builder.Services.AddFastEndpoints();
builder.Services.SwaggerDocument();

// Add SignalR for real-time client updates (AOT-compatible with typed hub)
builder.Services.AddSignalR()
  .AddJsonProtocol(options => {
    // Use source-generated JSON context for AOT compatibility
    options.PayloadSerializerOptions = ECommerce.Contracts.Generated.WhizbangJsonContext.CreateOptions();
  });

// Add CORS for Angular - uses Aspire service discovery to get Angular URL
builder.Services.AddCors(options => {
  options.AddDefaultPolicy(policy => {
    policy.WithOrigins(angularUrl)  // Angular URL from Aspire service discovery
      .AllowAnyHeader()
      .AllowAnyMethod()
      .AllowCredentials();  // Required for SignalR
  });
});

var app = builder.Build();

// Log discovered Angular URL for debugging
app.Logger.LogInformation("CORS configured for Angular UI at: {AngularUrl}", angularUrl);

// Initialize database schema on startup
// Uses generated EnsureWhizbangTablesCreatedAsync() extension method
// Creates all PerspectiveRow<T> entities + Inbox/Outbox/EventStore tables
using (var scope = app.Services.CreateScope()) {
  var dbContext = scope.ServiceProvider.GetRequiredService<BffDbContext>();
  await dbContext.EnsureWhizbangTablesCreatedAsync();
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment()) {
  app.UseDeveloperExceptionPage();
  app.UseSwaggerGen(); // FastEndpoints Swagger UI
}

app.UseHttpsRedirection();

// Enable CORS before other middleware
app.UseCors();

// FastEndpoints (REST API)
app.UseFastEndpoints(config => {
  config.Endpoints.RoutePrefix = "api";
});

// Map SignalR hubs
app.MapHub<OrderStatusHub>("/hubs/order-status");
app.MapHub<ProductInventoryHub>("/hubs/product-inventory");

// Map GraphQL endpoint with Banana Cake Pop UI (GraphQL IDE)
app.MapGraphQL("/graphql");

// Aspire health checks and diagnostics
app.MapDefaultEndpoints();

app.Run();
