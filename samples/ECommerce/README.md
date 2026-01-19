# ECommerce Sample Application

A production-ready distributed e-commerce application demonstrating the Whizbang messaging library with .NET Aspire orchestration.

## Architecture Overview

This sample implements a microservices-based e-commerce system using event-driven architecture, CQRS patterns, and a Backend for Frontend (BFF) architecture for the user interface.

### Frontend

**ECommerce.UI** (Angular 20, Port 4200)
- Modern Angular 20 application with Clarity Design System
- Real-time order updates via SignalR WebSocket connection
- State management: NgRx hybrid approach (Signal Store + traditional Store/Effects)
- Features:
  - Product catalog with 12 soccer team swag items
  - Shopping cart with live total calculations
  - Order tracking with real-time status updates
  - Admin dashboard with statistics and inventory management

### Backend Services

1. **BFF.API** (Backend for Frontend, Port 7001)
   - Dedicated API for the Angular UI
   - SignalR hub at `/hubs/order-status` for real-time updates
   - **Perspectives**: Event listeners that update denormalized read models
     - `OrderSummaryPerspective`: Maintains order status snapshots
     - `OrderStatsPerspective`: Calculates aggregate statistics
     - `ProductInventoryPerspective`: Tracks real-time inventory
   - **Lenses**: Query-optimized read repositories using Dapper
     - `OrderLens`: Fast queries for order data
     - `ProductLens`: Product catalog queries
   - **Fire-and-forget Commands**: Returns correlation IDs immediately, processes async
   - Real-time SignalR notifications pushed to connected clients via Perspectives

2. **OrderService.API** (Port 5000)
   - REST API (FastEndpoints) at `/api/*`
   - GraphQL API (HotChocolate) at `/graphql`
   - Handles order creation and queries
   - Publishes `OrderCreatedEvent` to Azure Service Bus

3. **InventoryWorker** (Background Service)
   - Consumes `OrderCreatedEvent` from Azure Service Bus
   - Processes inventory reservations
   - Publishes `InventoryReservedEvent`

4. **PaymentWorker** (Background Service)
   - Consumes `Order Created Event` from Azure Service Bus
   - Processes payment transactions
   - Publishes `PaymentProcessedEvent`

5. **ShippingWorker** (Background Service)
   - Consumes `InventoryReservedEvent` and `PaymentProcessedEvent`
   - Coordinates shipping fulfillment
   - Publishes `ShipmentCreatedEvent`

6. **NotificationWorker** (Background Service)
   - Consumes all events from Azure Service Bus
   - Sends notifications (email, SMS, etc.)

### Infrastructure

- **PostgreSQL**: Persistent storage for outbox/inbox patterns and message state
  - `bffdb`: BFF API database (perspectives, read models)
  - `ordersdb`: Order service database
  - `inventorydb`: Inventory service database
  - `paymentdb`: Payment service database
  - `shippingdb`: Shipping service database
  - `notificationdb`: Notification service database

- **Azure Service Bus**: Cross-service message transport
  - Topic: `orders` - For order-related events
  - Subscriptions: One per worker service for independent event consumption

- **SignalR**: Real-time WebSocket communication
  - BFF API hosts SignalR hub for pushing live updates to Angular UI
  - Perspectives automatically trigger SignalR notifications on event handling

### Messaging Patterns

#### BFF Architecture with Perspectives

The **Backend for Frontend (BFF)** pattern provides a dedicated API layer optimized for the Angular UI:

**Perspectives** - Event-driven read model updates:
- Implement `IPerspectiveOf<TEvent>` to listen for specific events
- Automatically update denormalized tables optimized for queries
- Trigger SignalR notifications to push updates to connected UI clients
- Examples:
  - `OrderSummaryPerspective`: Updates order status table on every order event
  - `OrderStatsPerspective`: Maintains aggregate statistics for dashboard
  - `ProductInventoryPerspective`: Real-time inventory tracking

**Lenses** - Query-optimized repositories:
- Read-only repositories using Dapper for fast queries
- Query denormalized data updated by Perspectives
- No business logic - pure data access layer
- Examples:
  - `OrderLens`: Fast order lookups by ID, customer, status
  - `ProductLens`: Product catalog queries

**Fire-and-forget Commands**:
- API immediately returns correlation ID to caller
- Command processing happens asynchronously
- UI polls or receives SignalR updates for status
- Prevents long-running HTTP requests

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
├── ECommerce.UI/                    # Angular 20 frontend application
│   ├── src/app/
│   │   ├── components/              # Catalog, Cart, Orders, Admin
│   │   ├── services/                # ProductService, OrderService, SignalRService
│   │   └── store/                   # NgRx state management
│   │       ├── cart/                # Cart Signal Store
│   │       └── orders/              # Orders Store, Effects, Actions
│   └── public/images/               # Product images
├── ECommerce.BFF.API/               # Backend for Frontend API
│   ├── Endpoints/                   # BFF API endpoints
│   ├── Hubs/                        # SignalR hubs (OrderStatusHub)
│   ├── Lenses/                      # Read repositories (OrderLens, ProductLens)
│   └── Perspectives/                # Event-driven read model updates
│       ├── OrderSummaryPerspective
│       ├── OrderStatsPerspective
│       └── ProductInventoryPerspective
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
- Node.js 20+ and pnpm 10+ (for Angular UI)
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
1. Start PostgreSQL containers for all service databases (including bffdb)
2. Start Azure Service Bus emulator (Azurite)
3. Apply database migrations
4. Start all 6 backend services (OrderService, BFF, 4 workers)
5. Start the Angular UI at http://localhost:4200
6. Open Aspire Dashboard at https://localhost:17195

Access the application:
- **Angular UI**: http://localhost:4200 - Shopping interface with catalog, cart, orders, and admin
- **BFF API**: Check Aspire Dashboard for assigned port
- **OrderService API**: Check Aspire Dashboard for assigned port
- **Aspire Dashboard**: Monitor all services, logs, and telemetry

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
- **Perspectives**: Event-driven read model updates (`IPerspectiveOf<TEvent>`)
- **Lenses**: Query-optimized read repositories
- **Outbox Pattern**: Reliable cross-service event publishing
- **Inbox Pattern**: Exactly-once message processing
- **Fire-and-forget Commands**: Async processing with immediate response

### Modern Frontend Architecture

- **Angular 20**: Latest Angular with standalone components
- **Clarity Design System**: VMware's enterprise UI component library
- **NgRx Hybrid State**: Signal Store (cart) + traditional Store/Effects (orders)
- **Real-time Updates**: SignalR WebSocket integration
- **BFF Pattern**: Backend optimized for frontend needs
- **Responsive Design**: Mobile-first UI with Clarity components

### .NET Aspire

- **Service Orchestration**: Automatic infrastructure provisioning
- **Service Discovery**: Inter-service communication
- **Resilience**: Built-in retry policies and circuit breakers
- **Observability**: Unified telemetry and monitoring
- **Node.js Integration**: Automatic Angular dev server orchestration

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
