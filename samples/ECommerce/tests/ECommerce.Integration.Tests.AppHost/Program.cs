var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL for Whizbang (shared by both services)
// Use Session lifetime for tests to get clean state on every test run
var postgres = builder.AddPostgres("postgres")
  .WithLifetime(ContainerLifetime.Session)
  .AddDatabase("whizbang-integration-test");

// NOTE: Azure Service Bus Emulator is NOT managed by Aspire
// Aspire's built-in emulator orchestration causes OOM crashes on ARM64 (Mac M-series) when
// provisioning topics via Config.json or dynamic topic creation.
//
// Instead, AspireIntegrationFixture manages the emulator directly via docker-compose using
// DirectServiceBusEmulatorFixture with a pre-configured Config-TrueFilter.json containing:
// - products topic with products-worker and products-bff subscriptions
// - inventory topic with inventory-worker and inventory-bff subscriptions
// - All subscriptions use TrueFilter ($Default) to accept all messages
//
// This approach provides:
// - Stable emulator operation without OOM crashes
// - Proper Config.json format with all required properties
// - Direct docker-compose control without Aspire interference
//
// See: DirectServiceBusEmulatorFixture.cs and Config-TrueFilter.json

// Export connection strings for tests to consume
builder.Build().Run();
