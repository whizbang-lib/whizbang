using ECommerce.OrderService.API.GraphQL.Mutations;
using ECommerce.OrderService.API.GraphQL.Queries;
using FastEndpoints;
using FastEndpoints.Swagger;
using Whizbang.Core;

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

// TODO: Register Whizbang dispatcher implementation
// For now, register a placeholder that will be implemented later
builder.Services.AddScoped<IDispatcher, PlaceholderDispatcher>();

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

/// <summary>
/// Placeholder dispatcher implementation for demonstration purposes
/// TODO: Replace with actual Whizbang dispatcher when available
/// </summary>
class PlaceholderDispatcher : IDispatcher {
  private readonly ILogger<PlaceholderDispatcher> _logger;

  public PlaceholderDispatcher(ILogger<PlaceholderDispatcher> logger) {
    _logger = logger;
  }

  public Task<TResult> SendAsync<TResult>(object message) {
    _logger.LogInformation("Dispatching message: {MessageType}", message.GetType().Name);
    // For now, just log and return default
    // In a real implementation, this would route to the appropriate handler
    return Task.FromResult(default(TResult)!);
  }

  public Task<TResult> SendAsync<TResult>(object message, IMessageContext context) {
    _logger.LogInformation("Dispatching message with context: {MessageType}", message.GetType().Name);
    return Task.FromResult(default(TResult)!);
  }

  public Task PublishAsync<TEvent>(TEvent @event) {
    _logger.LogInformation("Publishing event: {EventType}", typeof(TEvent).Name);
    // For now, just log
    // In a real implementation, this would publish to all interested handlers
    return Task.CompletedTask;
  }

  public Task<IEnumerable<TResult>> SendManyAsync<TResult>(IEnumerable<object> messages) {
    _logger.LogInformation("Dispatching {Count} messages", messages.Count());
    return Task.FromResult(Enumerable.Empty<TResult>());
  }
}
