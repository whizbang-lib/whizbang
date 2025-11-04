using ECommerce.InventoryWorker;

var builder = Host.CreateApplicationBuilder(args);

// Add service defaults (telemetry, health checks, service discovery)
builder.AddServiceDefaults();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
