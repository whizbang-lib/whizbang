var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL for Whizbang (shared by both services)
// Use Session lifetime for tests to get clean state on every test run
var postgres = builder.AddPostgres("postgres")
  .WithLifetime(ContainerLifetime.Session)
  .AddDatabase("whizbang-integration-test");

// Azure Service Bus Emulator
// CRITICAL: Emulator requires namespace name to be exactly "sbemulatorns" (non-modifiable)
// Aspire automatically creates $Default TrueFilter rules for subscriptions without explicit filters
// Session lifetime ensures clean state on every test run
var serviceBus = builder.AddAzureServiceBus("sbemulatorns")
  .RunAsEmulator(configureContainer => configureContainer
    .WithLifetime(ContainerLifetime.Session));

// Create topics and subscriptions
// Aspire automatically provisions $Default TrueFilter rules (all messages delivered)
// Subscription names must be globally unique within the namespace
// Format: {topic}-{service} to avoid collisions across topics
var productsTopic = serviceBus.AddServiceBusTopic("products");
productsTopic.AddServiceBusSubscription("products-bff-service");
productsTopic.AddServiceBusSubscription("products-inventory-worker");

var inventoryTopic = serviceBus.AddServiceBusTopic("inventory");
inventoryTopic.AddServiceBusSubscription("inventory-bff-service");
inventoryTopic.AddServiceBusSubscription("inventory-inventory-worker");

// Export connection strings for tests to consume
builder.Build().Run();
