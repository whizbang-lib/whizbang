using ECommerce.OrderService.API.GraphQL.Mutations;
using ECommerce.OrderService.API.GraphQL.Queries;
using FastEndpoints;
using FastEndpoints.Swagger;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Transports.AzureServiceBus;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults (telemetry, health checks, service discovery)
builder.AddServiceDefaults();

// Add HotChocolate GraphQL
builder.Services
  .AddGraphQLServer()
  .AddMutationType<OrderMutations>()
  .AddQueryType<OrderQueries>();

// Add FastEndpoints
builder.Services.AddFastEndpoints();
builder.Services.SwaggerDocument();

// Add OpenAPI for traditional endpoints
builder.Services.AddOpenApi();

// Get connection strings from Aspire configuration
var postgresConnection = builder.Configuration.GetConnectionString("ordersdb")
    ?? throw new InvalidOperationException("PostgreSQL connection string 'ordersdb' not found");
var serviceBusConnection = builder.Configuration.GetConnectionString("servicebus")
    ?? throw new InvalidOperationException("Azure Service Bus connection string 'servicebus' not found");

// Register Whizbang Postgres stores
builder.Services.AddWhizbangPostgres(postgresConnection);
builder.Services.AddWhizbangPostgresHealthChecks();

// Register Azure Service Bus transport
builder.Services.AddAzureServiceBusTransport(serviceBusConnection);
builder.Services.AddAzureServiceBusHealthChecks();

// Add trace store for observability
builder.Services.AddSingleton<ITraceStore, InMemoryTraceStore>();

// Register Whizbang dispatcher with source-generated receptors
builder.Services.AddReceptors();
builder.Services.AddWhizbangDispatcher();

// Register outbox publisher worker for reliable event publishing
builder.Services.AddHostedService<OutboxPublisherWorker>();

var app = builder.Build();

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

// Keep the WeatherForecast endpoint as a demo
var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () => {
  var forecast = Enumerable.Range(1, 5).Select(index =>
      new WeatherForecast
      (
          DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
          Random.Shared.Next(-20, 55),
          summaries[Random.Shared.Next(summaries.Length)]
      ))
      .ToArray();
  return forecast;
})
.WithName("GetWeatherForecast");

// Aspire health checks and diagnostics
app.MapDefaultEndpoints();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary) {
  public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
