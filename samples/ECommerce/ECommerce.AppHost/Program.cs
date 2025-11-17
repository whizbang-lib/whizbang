var builder = DistributedApplication.CreateBuilder(args);

// Add PostgreSQL database
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume();  // Persist data

// Create databases for each service (or use a shared database)
var ordersDb = postgres.AddDatabase("ordersdb");
var inventoryDb = postgres.AddDatabase("inventorydb");
var paymentDb = postgres.AddDatabase("paymentdb");
var shippingDb = postgres.AddDatabase("shippingdb");
var notificationDb = postgres.AddDatabase("notificationdb");
var bffDb = postgres.AddDatabase("bffdb");

// Add Azure Service Bus Emulator for local development
// The emulator runs in a Docker container and provides a local Service Bus instance
// When publishing to production, Aspire generates the correct Bicep for real Azure Service Bus
var serviceBus = builder.AddAzureServiceBus("servicebus")
    .RunAsEmulator();

// Configure the "orders" topic with subscriptions for each worker service
var ordersTopic = serviceBus.AddServiceBusTopic("orders");
ordersTopic.AddServiceBusSubscription("payment-service");
ordersTopic.AddServiceBusSubscription("shipping-service");
ordersTopic.AddServiceBusSubscription("inventory-service");
ordersTopic.AddServiceBusSubscription("notification-service");

// Add all ECommerce services with infrastructure dependencies
var orderService = builder.AddProject("orderservice", "../ECommerce.OrderService.API/ECommerce.OrderService.API.csproj")
    .WithReference(ordersDb)
    .WithReference(serviceBus)
    .WithExternalHttpEndpoints();

var inventoryWorker = builder.AddProject("inventoryworker", "../ECommerce.InventoryWorker/ECommerce.InventoryWorker.csproj")
    .WithReference(inventoryDb)
    .WithReference(serviceBus);

var paymentWorker = builder.AddProject("paymentworker", "../ECommerce.PaymentWorker/ECommerce.PaymentWorker.csproj")
    .WithReference(paymentDb)
    .WithReference(serviceBus);

var shippingWorker = builder.AddProject("shippingworker", "../ECommerce.ShippingWorker/ECommerce.ShippingWorker.csproj")
    .WithReference(shippingDb)
    .WithReference(serviceBus);

var notificationWorker = builder.AddProject("notificationworker", "../ECommerce.NotificationWorker/ECommerce.NotificationWorker.csproj")
    .WithReference(notificationDb)
    .WithReference(serviceBus);

var bffService = builder.AddProject("bff", "../ECommerce.BFF.API/ECommerce.BFF.API.csproj")
    .WithReference(bffDb)
    .WithReference(serviceBus)
    .WithExternalHttpEndpoints();  // BFF needs external access for Angular app

// NOTE: Angular UI integration commented out - requires Aspire.Hosting.NodeJs package
// The Angular app can be run independently with 'npm start' in ECommerce.UI directory
// TODO: Add Aspire.Hosting.NodeJs package reference to enable npm app hosting
// var angularApp = builder.AddNpmApp("ui", "../ECommerce.UI", "start")
//     .WithHttpEndpoint(port: 4200, env: "PORT")
//     .WithExternalHttpEndpoints()
//     .WaitFor(bffService);  // Wait for BFF to be ready before starting UI

builder.Build().Run();
