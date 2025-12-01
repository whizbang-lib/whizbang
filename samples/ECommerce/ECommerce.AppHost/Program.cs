var builder = DistributedApplication.CreateBuilder(args);

// Add PostgreSQL database with pgAdmin (persistent across restarts)
// Password configured via Parameters section in appsettings.json
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("postgres-data")  // Named volume for persistent PostgreSQL data at /var/lib/postgresql/data
    .WithLifetime(ContainerLifetime.Persistent)  // Keep container running
    .WithPgAdmin(pgadmin => pgadmin
        .WithLifetime(ContainerLifetime.Persistent));  // Keep pgAdmin running (settings in container)

// Create databases for each service (or use a shared database)
var ordersDb = postgres.AddDatabase("ordersdb");
var inventoryDb = postgres.AddDatabase("inventorydb");
var paymentDb = postgres.AddDatabase("paymentdb");
var shippingDb = postgres.AddDatabase("shippingdb");
var notificationDb = postgres.AddDatabase("notificationdb");
var bffDb = postgres.AddDatabase("bffdb");

// Add Azure Service Bus Emulator for local development (persistent container)
// The emulator runs in a Docker container and provides a local Service Bus instance
// When publishing to production, Aspire generates the correct Bicep for real Azure Service Bus
var serviceBus = builder.AddAzureServiceBus("servicebus")
    .RunAsEmulator(configureContainer => configureContainer
        .WithLifetime(ContainerLifetime.Persistent));  // Keep container running (data in container)

// Configure the "orders" topic with subscriptions for each worker service
var ordersTopic = serviceBus.AddServiceBusTopic("orders");
ordersTopic.AddServiceBusSubscription("payment-service");
ordersTopic.AddServiceBusSubscription("shipping-service");
ordersTopic.AddServiceBusSubscription("inventory-service");
ordersTopic.AddServiceBusSubscription("notification-service");

// Add all ECommerce services with infrastructure dependencies
// IMPORTANT: Using --no-build to prevent concurrent rebuild conflicts on shared libraries (Whizbang.Core)
// The preLaunchTask in VSCode builds all services once before launching Aspire
var orderService = builder.AddProject("orderservice", "../ECommerce.OrderService.API/ECommerce.OrderService.API.csproj")
    .WithArgs("--no-build")
    .WithReference(ordersDb)
    .WithReference(serviceBus)
    .WaitFor(ordersDb)
    .WaitFor(serviceBus)
    .WithExternalHttpEndpoints();

var inventoryWorker = builder.AddProject("inventoryworker", "../ECommerce.InventoryWorker/ECommerce.InventoryWorker.csproj")
    .WithArgs("--no-build")
    .WithReference(inventoryDb)
    .WithReference(serviceBus)
    .WaitFor(inventoryDb)
    .WaitFor(serviceBus);

var paymentWorker = builder.AddProject("paymentworker", "../ECommerce.PaymentWorker/ECommerce.PaymentWorker.csproj")
    .WithArgs("--no-build")
    .WithReference(paymentDb)
    .WithReference(serviceBus)
    .WaitFor(paymentDb)
    .WaitFor(serviceBus);

var shippingWorker = builder.AddProject("shippingworker", "../ECommerce.ShippingWorker/ECommerce.ShippingWorker.csproj")
    .WithArgs("--no-build")
    .WithReference(shippingDb)
    .WithReference(serviceBus)
    .WaitFor(shippingDb)
    .WaitFor(serviceBus);

var notificationWorker = builder.AddProject("notificationworker", "../ECommerce.NotificationWorker/ECommerce.NotificationWorker.csproj")
    .WithArgs("--no-build")
    .WithReference(notificationDb)
    .WithReference(serviceBus)
    .WaitFor(notificationDb)
    .WaitFor(serviceBus);

// Angular UI integration - Fixed port 4200 via PORT environment variable
// Uses run-script-os in package.json to pass $PORT to ng serve
var angularApp = builder.AddNpmApp("ui", "../ECommerce.UI", "start")
    .WithHttpEndpoint(port: 4200, env: "PORT")
    .WithExternalHttpEndpoints();

var bffService = builder.AddProject("bff", "../ECommerce.BFF.API/ECommerce.BFF.API.csproj")
    .WithArgs("--no-build")
    .WithReference(bffDb)
    .WithReference(serviceBus)
    .WithReference(angularApp)  // BFF can discover Angular URL for CORS
    .WaitFor(bffDb)
    .WaitFor(serviceBus)
    .WaitFor(angularApp)  // Wait for Angular to be ready
    .WithExternalHttpEndpoints();

// Add custom URLs for easy navigation in Aspire dashboard
bffService
    .WithUrlForEndpoint("http", url => {
      url.DisplayText = "📖 Swagger UI";
      url.Url = "/swagger";
    })
     .WithUrlForEndpoint("http", url => {
       url.DisplayText = "🚀 GraphQL";
       url.Url = "/graphql";
     });

builder.Build().Run();
