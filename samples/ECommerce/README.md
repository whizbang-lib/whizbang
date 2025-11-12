# ECommerce Sample Application

A production-ready distributed e-commerce application demonstrating the Whizbang messaging library with .NET Aspire orchestration.

## Architecture Overview

This sample implements a microservices-based e-commerce system using event-driven architecture and CQRS patterns.

### Services

1. **OrderService.API** (Port 5000)
   - REST API (FastEndpoints) at `/api/*`
   - GraphQL API (HotChocolate) at `/graphql`
   - Handles order creation and queries
   - Publishes `OrderCreatedEvent` to Azure Service Bus

2. **InventoryWorker** (Background Service)
   - Consumes `OrderCreatedEvent` from Azure Service Bus
   - Processes inventory reservations
   - Publishes `InventoryReservedEvent`

3. **PaymentWorker** (Background Service)
   - Consumes `Order Created Event` from Azure Service Bus
   - Processes payment transactions
   - Publishes `PaymentProcessedEvent`

4. **ShippingWorker** (Background Service)
   - Consumes `InventoryReservedEvent` and `PaymentProcessedEvent`
   - Coordinates shipping fulfillment
   - Publishes `ShipmentCreatedEvent`

5. **NotificationWorker** (Background Service)
   - Consumes all events from Azure Service Bus
   - Sends notifications (email, SMS, etc.)

### Infrastructure

- **PostgreSQL**: Persistent storage for outbox/inbox patterns and message state
  - `ordersdb`: Order service database
  - `inventorydb`: Inventory service database
  - `paymentdb`: Payment service database
  - `shippingdb`: Shipping service database
  - `notificationdb`: Notification service database

- **Azure Service Bus**: Cross-service message transport
  - Topic: `orders` - For order-related events
  - Subscriptions: One per worker service for independent event consumption

### Messaging Patterns

#### Outbox Pattern
- Events are first stored in PostgreSQL outbox table
- `OutboxPublisherWorker` reliably publishes events to Azure Service Bus
- Guarantees at-least-once delivery across service boundaries

#### Inbox Pattern
- `ServiceBusConsumerWorker` deduplicates incoming messages
- Tracks processed message IDs in PostgreSQL inbox table
- Ensures exactly-once processing within each service

## Project Structure

```
ECommerce/
├── ECommerce.AppHost/              # .NET Aspire orchestration
├── ECommerce.ServiceDefaults/       # Shared telemetry, health checks
├── ECommerce.OrderService.API/      # REST/GraphQL API service
│   ├── Endpoints/                   # FastEndpoints
│   ├── GraphQL/                     # HotChocolate queries/mutations
│   └── Receptors/                   # Command/event handlers
├── ECommerce.InventoryWorker/       # Inventory management service
│   └── Receptors/                   # Event handlers
├── ECommerce.PaymentWorker/         # Payment processing service
│   └── Receptors/                   # Event handlers
├── ECommerce.ShippingWorker/        # Shipping fulfillment service
│   └── Receptors/                   # Event handlers
├── ECommerce.NotificationWorker/    # Notification service
│   └── Receptors/                   # Event handlers
├── ECommerce.Contracts/             # Shared message contracts
│   ├── Commands/                    # Command messages
│   └── Events/                      # Event messages
└── ECommerce.IntegrationTests/      # Integration tests (TUnit)
```

## Prerequisites

- .NET 10.0 RC2 SDK or later
- Docker Desktop (for PostgreSQL and Azure Service Bus emulator)
- Azure CLI (optional, for real Azure Service Bus)

## Setup and Running

### Option 1: Using .NET Aspire (Recommended)

.NET Aspire automatically provisions and orchestrates all services and infrastructure.

```bash
cd samples/ECommerce/ECommerce.AppHost
dotnet run
```

This will:
1. Start PostgreSQL containers for each service database
2. Start Azure Service Bus emulator (Azurite)
3. Apply database migrations
4. Start all 5 services
5. Open Aspire Dashboard at https://localhost:17195

### Option 2: Manual Setup

If running services individually:

1. **Start Infrastructure**

```bash
# Start PostgreSQL
docker run -d --name postgres -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:16

# Start Azure Service Bus emulator (requires Azure CLI)
# Or use connection string to real Azure Service Bus
```

2. **Configure Connection Strings**

Update `appsettings.Development.json` in each service:

```json
{
  "ConnectionStrings": {
    "ordersdb": "Host=localhost;Database=ordersdb;Username=postgres;Password=postgres",
    "servicebus": "Endpoint=sb://localhost..."
  }
}
```

3. **Run Services**

```bash
# Terminal 1 - OrderService.API
cd ECommerce.OrderService.API
dotnet run

# Terminal 2 - InventoryWorker
cd ECommerce.InventoryWorker
dotnet run

# Terminal 3 - PaymentWorker
cd ECommerce.PaymentWorker
dotnet run

# Terminal 4 - ShippingWorker
cd ECommerce.ShippingWorker
dotnet run

# Terminal 5 - NotificationWorker
cd ECommerce.NotificationWorker
dotnet run
```

## Testing

### Running Integration Tests

```bash
cd ECommerce.IntegrationTests
dotnet run
```

### Manual API Testing

#### Using REST API (FastEndpoints)

```bash
# Create an order
curl -X POST https://localhost:5000/api/orders \
  -H "Content-Type: application/json" \
  -d '{
    "orderId": "ORDER-001",
    "customerId": "CUST-123",
    "lineItems": [
      {
        "productId": "PROD-001",
        "productName": "Widget",
        "quantity": 2,
        "unitPrice": 19.99
      }
    ],
    "totalAmount": 39.98
  }'
```

#### Using GraphQL API

Navigate to `https://localhost:5000/graphql` in your browser to access the GraphQL playground.

```graphql
mutation {
  createOrder(
    orderId: "ORDER-002"
    customerId: "CUST-456"
    lineItems: [
      {
        productId: "PROD-002"
        productName: "Gadget"
        quantity: 1
        unitPrice: 29.99
      }
    ]
    totalAmount: 29.99
  )
}
```

## Monitoring and Observability

### Health Checks

All services expose health check endpoints (in development mode):

- `/health` - Overall health status
- `/alive` - Liveness probe

```bash
curl https://localhost:5000/health
```

### OpenTelemetry

Services are instrumented with OpenTelemetry for:
- **Distributed Tracing**: Request flows across services
- **Metrics**: Performance and usage metrics
- **Structured Logging**: Searchable, contextual logs

View telemetry in the Aspire Dashboard when running via AppHost.

### Message Registry

Each service generates a `.whizbang/message-registry.json` file containing:
- All message types (commands, events)
- Dispatchers and their message flows
- Receptors and their message handling
- Perspectives (read models/projections)

The solution automatically merges these into a master registry for the VSCode extension.

## Key Features Demonstrated

### Whizbang Messaging Library

- **Source-Generated Dispatch**: Zero-reflection message routing
- **Type-Safe Handlers**: Compile-time verified message handling
- **Message Envelope**: Distributed tracing with hop-based metadata
- **Outbox Pattern**: Reliable cross-service event publishing
- **Inbox Pattern**: Exactly-once message processing

### .NET Aspire

- **Service Orchestration**: Automatic infrastructure provisioning
- **Service Discovery**: Inter-service communication
- **Resilience**: Built-in retry policies and circuit breakers
- **Observability**: Unified telemetry and monitoring

### ASP.NET Core

- **FastEndpoints**: Minimal API framework for high-performance REST
- **HotChocolate**: GraphQL server with schema-first approach
- **Health Checks**: Readiness and liveness probes

### Persistence

- **Dapper**: Lightweight ORM for PostgreSQL
- **Transactional Outbox**: Database-backed reliable messaging
- **Inbox Deduplication**: Idempotent message processing

## Architecture Decisions

### Why Outbox Pattern?

The outbox pattern ensures that:
1. Domain events are atomically committed with business data
2. Events are eventually published to Azure Service Bus
3. No events are lost due to infrastructure failures

### Why Inbox Pattern?

The inbox pattern provides:
1. Idempotent message handling (exactly-once semantics)
2. Protection against duplicate messages from Azure Service Bus
3. Audit trail of processed messages

### Why Separate Databases?

Each service has its own database for:
1. Service autonomy and independent scaling
2. Clear ownership of data
3. Ability to evolve schemas independently
4. Failure isolation

## Troubleshooting

### Services Won't Start

1. Check Docker is running
2. Verify ports 5000-5005 are available
3. Check Aspire Dashboard logs for errors

### Messages Not Being Delivered

1. Verify Azure Service Bus connection string
2. Check outbox tables for pending messages
3. Review OutboxPublisherWorker logs
4. Confirm ServiceBusConsumerWorker is running

### Database Connection Errors

1. Verify PostgreSQL containers are running
2. Check connection strings in appsettings
3. Ensure databases are created and migrations applied

## Further Reading

- [Whizbang Documentation](https://whizbang-lib.github.io)
- [.NET Aspire Documentation](https://learn.microsoft.com/dotnet/aspire)
- [Outbox Pattern](https://microservices.io/patterns/data/transactional-outbox.html)
- [FastEndpoints](https://fast-endpoints.com)
- [HotChocolate GraphQL](https://chillicream.com/docs/hotchocolate)

## License

This sample application is provided for demonstration purposes and is available under the same license as the Whizbang library.
