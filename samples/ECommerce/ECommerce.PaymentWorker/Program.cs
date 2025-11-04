var builder = Host.CreateApplicationBuilder(args);

// Add service defaults (telemetry, health checks, service discovery)
builder.AddServiceDefaults();

var host = builder.Build();
host.Run();
