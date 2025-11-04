var builder = DistributedApplication.CreateBuilder(args);

// Add all ECommerce services
var orderService = builder.AddProject<Projects.ECommerce_OrderService_API>("orderservice")
    .WithExternalHttpEndpoints();

var inventoryWorker = builder.AddProject<Projects.ECommerce_InventoryWorker>("inventoryworker");

var notificationWorker = builder.AddProject<Projects.ECommerce_NotificationWorker>("notificationworker");

var paymentWorker = builder.AddProject<Projects.ECommerce_PaymentWorker>("paymentworker");

var shippingWorker = builder.AddProject<Projects.ECommerce_ShippingWorker>("shippingworker");

builder.Build().Run();
