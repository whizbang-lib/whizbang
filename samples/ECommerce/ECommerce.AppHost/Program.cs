#if AZURESERVICEBUS
using Whizbang.Hosting.Azure.ServiceBus;
#elif RABBITMQ
using Whizbang.Hosting.RabbitMQ;
#endif

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

#if AZURESERVICEBUS
// Add Azure Service Bus Emulator for local development (persistent container)
// The emulator runs in a Docker container and provides a local Service Bus instance
// When publishing to production, Aspire generates the correct Bicep for real Azure Service Bus
var messagingInfra = builder.AddAzureServiceBus("servicebus")
    .RunAsEmulator(configureContainer => configureContainer
        .WithLifetime(ContainerLifetime.Persistent));  // Keep container running (data in container)

// Configure the "orders" topic with subscriptions for each worker service
var ordersTopic = messagingInfra.AddServiceBusTopic("orders");
ordersTopic.AddServiceBusSubscription("sub-payment-orders");
ordersTopic.AddServiceBusSubscription("sub-shipping-orders");
ordersTopic.AddServiceBusSubscription("sub-inventory-orders");
ordersTopic.AddServiceBusSubscription("sub-notification-orders");
ordersTopic.AddServiceBusSubscription("sub-bff-orders");

// Configure the "products" topic with subscriptions
var productsTopic = messagingInfra.AddServiceBusTopic("products");
productsTopic.AddServiceBusSubscription("sub-bff-products");
productsTopic.AddServiceBusSubscription("sub-inventory-products");

// Configure the "payments" topic with BFF subscription
var paymentsTopic = messagingInfra.AddServiceBusTopic("payments");
paymentsTopic.AddServiceBusSubscription("sub-bff-payments");

// Configure the "shipping" topic with BFF subscription
var shippingTopic = messagingInfra.AddServiceBusTopic("shipping");
shippingTopic.AddServiceBusSubscription("sub-bff-shipping");

// Configure the "inbox" topic for point-to-point messaging (commands/queries)
// Each service has its own subscription with CorrelationFilter: Destination = 'service-name'
// Filters are provisioned by Aspire (emulator/Bicep) via WithDestinationFilter extension
// Note: Subscription names must be globally unique across ALL topics in Aspire
var inboxTopic = messagingInfra.AddServiceBusTopic("inbox");
inboxTopic.AddServiceBusSubscription("sub-inbox-inventory").WithDestinationFilter("inventory-service");
inboxTopic.AddServiceBusSubscription("sub-inbox-payment").WithDestinationFilter("payment-service");
inboxTopic.AddServiceBusSubscription("sub-inbox-shipping").WithDestinationFilter("shipping-service");
inboxTopic.AddServiceBusSubscription("sub-inbox-notification").WithDestinationFilter("notification-service");
inboxTopic.AddServiceBusSubscription("sub-inbox-order").WithDestinationFilter("order-service");
inboxTopic.AddServiceBusSubscription("sub-inbox-bff").WithDestinationFilter("bff-service");

#elif RABBITMQ
// Add RabbitMQ for local development (persistent container)
// The RabbitMQ container provides AMQP messaging with Management UI
var messagingInfra = builder.AddRabbitMQ("rabbitmq")
    .WithManagementPlugin()  // Enable management UI at http://localhost:15672
    .WithLifetime(ContainerLifetime.Persistent);  // Keep container running

// Configure event exchanges (topic exchanges for pub/sub)
messagingInfra.WithExchange("orders", "topic");
messagingInfra.WithExchange("products", "topic");
messagingInfra.WithExchange("payments", "topic");
messagingInfra.WithExchange("shipping", "topic");

// Configure inbox exchange (direct exchange for point-to-point messaging)
messagingInfra.WithExchange("inbox", "direct");

// Configure queue bindings for "orders" exchange
messagingInfra.WithQueueBinding("payment-worker-queue", "orders", "#");
messagingInfra.WithQueueBinding("shipping-worker-queue", "orders", "#");
messagingInfra.WithQueueBinding("inventory-worker-queue", "orders", "#");
messagingInfra.WithQueueBinding("notification-worker-queue", "orders", "#");
messagingInfra.WithQueueBinding("bff-orders-queue", "orders", "#");

// Configure queue bindings for "products" exchange
messagingInfra.WithQueueBinding("bff-products-queue", "products", "#");
messagingInfra.WithQueueBinding("inventory-products-queue", "products", "#");

// Configure queue bindings for "payments" exchange
messagingInfra.WithQueueBinding("bff-payments-queue", "payments", "#");

// Configure queue bindings for "shipping" exchange
messagingInfra.WithQueueBinding("bff-shipping-queue", "shipping", "#");

// Configure inbox queue bindings (direct exchange with service-specific routing keys)
messagingInfra.WithQueueBinding("inventory-inbox-queue", "inbox", "inventory-service");
messagingInfra.WithQueueBinding("payment-inbox-queue", "inbox", "payment-service");
messagingInfra.WithQueueBinding("shipping-inbox-queue", "inbox", "shipping-service");
messagingInfra.WithQueueBinding("notification-inbox-queue", "inbox", "notification-service");
messagingInfra.WithQueueBinding("order-inbox-queue", "inbox", "order-service");
messagingInfra.WithQueueBinding("bff-inbox-queue", "inbox", "bff-service");

#endif

// Add all ECommerce services with infrastructure dependencies
// IMPORTANT: Using --no-build to prevent concurrent rebuild conflicts on shared libraries (Whizbang.Core)
// The preLaunchTask in VSCode builds all services once before launching Aspire
var orderService = builder.AddProject("orderservice", "../ECommerce.OrderService.API/ECommerce.OrderService.API.csproj")
    .WithArgs("--no-build")
    .WithReference(ordersDb)
    .WithReference(messagingInfra)
    .WaitFor(ordersDb)
    .WaitFor(messagingInfra)
    .WithExternalHttpEndpoints();

var inventoryWorker = builder.AddProject("inventoryworker", "../ECommerce.InventoryWorker/ECommerce.InventoryWorker.csproj")
    .WithArgs("--no-build")
    .WithReference(inventoryDb)
    .WithReference(messagingInfra)
    .WaitFor(inventoryDb)
    .WaitFor(messagingInfra);

var paymentWorker = builder.AddProject("paymentworker", "../ECommerce.PaymentWorker/ECommerce.PaymentWorker.csproj")
    .WithArgs("--no-build")
    .WithReference(paymentDb)
    .WithReference(messagingInfra)
    .WaitFor(paymentDb)
    .WaitFor(messagingInfra);

var shippingWorker = builder.AddProject("shippingworker", "../ECommerce.ShippingWorker/ECommerce.ShippingWorker.csproj")
    .WithArgs("--no-build")
    .WithReference(shippingDb)
    .WithReference(messagingInfra)
    .WaitFor(shippingDb)
    .WaitFor(messagingInfra);

var notificationWorker = builder.AddProject("notificationworker", "../ECommerce.NotificationWorker/ECommerce.NotificationWorker.csproj")
    .WithArgs("--no-build")
    .WithReference(notificationDb)
    .WithReference(messagingInfra)
    .WaitFor(notificationDb)
    .WaitFor(messagingInfra);

// Angular UI integration - Fixed port 4200 via PORT environment variable
// Uses run-script-os in package.json to pass $PORT to ng serve
var angularApp = builder.AddNpmApp("ui", "../ECommerce.UI", "start")
    .WithHttpEndpoint(port: 4200, env: "PORT")
    .WithExternalHttpEndpoints();

var bffService = builder.AddProject("bff", "../ECommerce.BFF.API/ECommerce.BFF.API.csproj")
    .WithArgs("--no-build")
    .WithReference(bffDb)
    .WithReference(messagingInfra)
    .WithReference(angularApp)  // BFF can discover Angular URL for CORS
    .WaitFor(bffDb)
    .WaitFor(messagingInfra)
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
