var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL for Whizbang (shared by both services)
// Use Session lifetime for tests to get clean state on every test run
var postgres = builder.AddPostgres("postgres")
  .WithLifetime(ContainerLifetime.Session)
  .AddDatabase("whizbang-integration-test");

// Azure Service Bus Emulator
// Aspire automatically creates $Default TrueFilter rules for subscriptions without explicit filters
// Session lifetime ensures clean state on every test run
var serviceBus = builder.AddAzureServiceBus("servicebus")
  .RunAsEmulator(configureContainer => configureContainer
    .WithLifetime(ContainerLifetime.Session));

// Create topics and subscriptions
// Aspire automatically provisions $Default TrueFilter rules (all messages delivered)
// Subscription names must match the service instance names used in AzureServiceBusTransport
// In tests, we create subscriptions per service even though both services run in-process
var productsTopic = serviceBus.AddServiceBusTopic("products");
productsTopic.AddServiceBusSubscription("bff-service");
productsTopic.AddServiceBusSubscription("inventory-worker");

var inventoryTopic = serviceBus.AddServiceBusTopic("inventory");
inventoryTopic.AddServiceBusSubscription("bff-service");
inventoryTopic.AddServiceBusSubscription("inventory-worker");

// Export connection strings for tests to consume
builder.Build().Run();
