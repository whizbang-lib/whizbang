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

// Add Azure Service Bus
// Note: For now using connection string from configuration
// In production, would use Azure Service Bus Emulator or real Azure Service Bus
var serviceBus = builder.AddAzureServiceBus("servicebus");

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

builder.Build().Run();
