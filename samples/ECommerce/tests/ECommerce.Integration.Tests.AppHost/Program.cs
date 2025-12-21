var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL for Whizbang (shared by both services)
// Use Session lifetime for tests to get clean state on every test run
var postgres = builder.AddPostgres("postgres")
  .WithLifetime(ContainerLifetime.Session)
  .AddDatabase("whizbang-integration-test");

// Azure Service Bus Emulator (use Session lifetime to get clean state)
var serviceBus = builder.AddAzureServiceBus("servicebus")
  .RunAsEmulator(configureContainer => configureContainer
    .WithLifetime(ContainerLifetime.Session));

// Configure topics and subscriptions for integration tests
// Note: AddServiceBusSubscription(name, subscriptionName) where:
//   - name: Aspire resource name (must be globally unique)
//   - subscriptionName: Azure Service Bus subscription name (scoped to topic)
var productsTopic = serviceBus.AddServiceBusTopic("products");
productsTopic.AddServiceBusSubscription("products-inventory-worker", "inventory-worker");
productsTopic.AddServiceBusSubscription("products-bff-service", "bff-service");

var inventoryTopic = serviceBus.AddServiceBusTopic("inventory");
inventoryTopic.AddServiceBusSubscription("inventory-inventory-worker", "inventory-worker");
inventoryTopic.AddServiceBusSubscription("inventory-bff-service", "bff-service");

// Export connection strings for tests to consume
builder.Build().Run();
