# Sample Project Standards

**Dogfooding the Library - Real-World Usage Demonstration**

Sample projects exist to **dogfood** the Whizbang library - demonstrating real-world patterns that library users will follow. They are NOT playgrounds for experimentation or workarounds.

---

## Table of Contents

1. [Purpose and Philosophy](#purpose-and-philosophy)
2. [Strict Separation Rules](#strict-separation-rules)
3. [AOT Requirement](#aot-requirement)
4. [Integration Testing](#integration-testing)
5. [What Samples Are NOT](#what-samples-are-not)
6. [What Samples ARE](#what-samples-are-are)
7. [ECommerce Sample Structure](#ecommerce-sample-structure)
8. [Common Patterns to Demonstrate](#common-patterns-to-demonstrate)

---

## Purpose and Philosophy

### Why Samples Exist

Samples serve **three critical purposes**:

1. **Dogfooding** - We use our own library to discover issues
2. **Documentation** - Show users how to use the library in real scenarios
3. **Validation** - Prove library works in production-like environments

### What "Dogfooding" Means

**Dogfooding** = Using your own product the same way customers would.

**If we won't use it, why should users?**

- ✅ Sample hits a library limitation → **Library needs improvement**
- ❌ Sample works around library limitation → **Bad example for users**

---

## Strict Separation Rules

### The Golden Rule

> **When a sample needs a feature, the library MUST provide it.**

**NEVER work around library limitations in samples.**

---

### ❌ WRONG - Workaround in Sample

```csharp
// In ECommerce.OrderService sample project

public class OrderService {
    // ❌ WRONG - Manually managing causation because library doesn't support it
    private void ManualCausationTracking(Order order, CreateOrder command) {
        // Hack: Store causation manually in custom field
        // This is a MASSIVE RED FLAG!
        order.Metadata["ParentMessageId"] = command.MessageId.ToString();
        order.Metadata["CausationChain"] = JsonSerializer.Serialize(causationChain);
    }

    public async Task<OrderCreated> ProcessAsync(CreateOrder command) {
        var order = new Order();
        ManualCausationTracking(order, command);  // Workaround!
        // ...
    }
}
```

**Why this is WRONG:**
- Users will copy this pattern (bad example)
- Proves library is incomplete
- Creates confusion ("Why isn't this built-in?")
- Sample doesn't actually dogfood the library
- Hides real user pain points

---

### ✅ CORRECT - Implement in Library First

**Process when sample needs a feature:**

```
1. Recognize Need
   ┌──────────────────────────────────────┐
   │ Sample: "I need causation tracking"  │
   └──────────────────────────────────────┘
           ↓
2. STOP Sample Work
   ┌──────────────────────────────────────┐
   │ Do NOT implement workaround          │
   │ Do NOT continue with sample          │
   └──────────────────────────────────────┘
           ↓
3. Design Library Feature
   ┌──────────────────────────────────────┐
   │ Design: MessageEnvelope with hops    │
   │ Design: Causation chain tracking     │
   │ Document: API design decisions       │
   └──────────────────────────────────────┘
           ↓
4. Implement in Library (TDD)
   ┌──────────────────────────────────────┐
   │ RED: Write failing tests             │
   │ GREEN: Implement MessageHop          │
   │ REFACTOR: Clean up                   │
   │ 100% branch coverage                 │
   └──────────────────────────────────────┘
           ↓
5. Document Library Feature
   ┌──────────────────────────────────────┐
   │ Update API documentation             │
   │ Add code examples                    │
   │ Update tutorials                     │
   └──────────────────────────────────────┘
           ↓
6. THEN Use in Sample
   ┌──────────────────────────────────────┐
   │ Now sample can use built-in feature  │
   │ Shows users the CORRECT pattern      │
   │ Validates library design             │
   └──────────────────────────────────────┘
```

**Result:** Library improves, sample demonstrates correct usage, users benefit.

---

### Real-World Example: Causation Tracking

**Before (Library didn't support causation):**
```csharp
// What we COULD have done (WRONG):
// Workaround in sample with custom metadata fields

// What we ACTUALLY did (CORRECT):
// 1. Stopped sample work
// 2. Designed MessageEnvelope with MessageHop
// 3. Implemented in library with tests
// 4. Documented the feature
// 5. Used it in sample
```

**After (Library provides causation tracking):**
```csharp
// In sample project - clean usage
public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
    public async Task<OrderCreated> ReceiveAsync(CreateOrder message) {
        // Library automatically tracks causation via MessageEnvelope
        // Sample just uses the feature naturally
        var order = await _orderService.CreateOrderAsync(message);
        return new OrderCreated(order.Id);
    }
}
```

**Users see:** "This is how I use Whizbang in my app" (correct pattern)

---

## AOT Requirement

### ABSOLUTE - All Samples Must Be AOT Compatible

**Requirements:**
- ✅ ZERO reflection in sample code
- ✅ Must compile with `<PublishAot>true</PublishAot>`
- ✅ Must publish to native AOT binary
- ✅ Must run successfully as native binary
- ✅ Demonstrate end-to-end AOT workflow

**Why STRICT for samples:**
- Proves library is truly AOT compatible
- Shows users that AOT is achievable
- Validates all library features work with AOT
- Demonstrates real-world AOT patterns

---

### Sample Project Configuration

**Every sample project `.csproj`:**

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <!-- AOT Publishing -->
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>false</InvariantGlobalization>

    <!-- Trim warnings as errors -->
    <EnableAotAnalyzer>true</EnableAotAnalyzer>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\..\src\Whizbang.Core\Whizbang.Core.csproj" />
  </ItemGroup>
</Project>
```

---

### Verification Commands

```bash
# Navigate to sample project
cd samples/ECommerce/ECommerce.OrderService.API

# Build with AOT analysis
dotnet build -c Release

# Publish as native AOT
dotnet publish -c Release -r linux-x64

# Verify binary is native
file bin/Release/net10.0/linux-x64/publish/ECommerce.OrderService.API
# Should show: "ELF 64-bit LSB executable"

# Run native binary
./bin/Release/net10.0/linux-x64/publish/ECommerce.OrderService.API

# Test all endpoints
curl http://localhost:5000/api/orders
```

**All samples must pass this verification.**

---

## Integration Testing

### Samples MUST Have Integration Tests

**Purpose:**
- Validate end-to-end workflows
- Prove features work together
- Catch integration issues
- Document expected behavior

---

### Test Structure

```
samples/ECommerce/
├── tests/
│   ├── ECommerce.Integration.Tests/
│   │   ├── OrderFlowTests.cs
│   │   ├── PaymentFlowTests.cs
│   │   ├── ShippingFlowTests.cs
│   │   └── EndToEndTests.cs
│   ├── ECommerce.OrderService.Tests/
│   ├── ECommerce.BFF.API.Tests/
│   └── ...
```

---

### Integration Test Example

```csharp
public class OrderFlowIntegrationTests {
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [Before(Test)]
    public async Task SetupAsync() {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => {
                builder.ConfigureServices(services => {
                    // Use test database
                    services.AddDbContext<AppDbContext>(options =>
                        options.UseNpgsql(TestDatabase.ConnectionString));
                });
            });

        _client = _factory.CreateClient();

        await TestDatabase.ResetAsync();
    }

    [Test]
    public async Task OrderFlow_CreateToShipped_ProcessesCorrectlyAsync() {
        // Arrange
        var createOrderRequest = new CreateOrderRequest {
            CustomerId = Guid.CreateVersion7(),
            Items = new[] {
                new OrderItemRequest {
                    ProductId = Guid.CreateVersion7(),
                    Quantity = 2,
                    Price = 29.99m
                }
            }
        };

        // Act - Create order
        var createResponse = await _client.PostAsJsonAsync(
            "/api/orders",
            createOrderRequest);

        await Assert.That(createResponse.IsSuccessStatusCode).IsTrue();

        var order = await createResponse.Content.ReadFromJsonAsync<OrderResponse>();
        await Assert.That(order).IsNotNull();
        await Assert.That(order!.Status).IsEqualTo("Pending");

        // Act - Process payment
        var paymentResponse = await _client.PostAsync(
            $"/api/orders/{order.Id}/process-payment",
            null);

        await Assert.That(paymentResponse.IsSuccessStatusCode).IsTrue();

        // Act - Ship order
        var shipResponse = await _client.PostAsync(
            $"/api/orders/{order.Id}/ship",
            null);

        await Assert.That(shipResponse.IsSuccessStatusCode).IsTrue();

        // Assert - Verify final state
        var finalOrder = await _client.GetFromJsonAsync<OrderResponse>(
            $"/api/orders/{order.Id}");

        await Assert.That(finalOrder).IsNotNull();
        await Assert.That(finalOrder!.Status).IsEqualTo("Shipped");
        await Assert.That(finalOrder.Events).HasCount().GreaterThan(0);
    }

    [After(Test)]
    public async Task CleanupAsync() {
        _client.Dispose();
        await _factory.DisposeAsync();
    }
}
```

---

### Integration Test Requirements

**Every sample MUST test:**
- ✅ Happy path (full workflow succeeds)
- ✅ Error handling (failures handled gracefully)
- ✅ Message flow (commands → events)
- ✅ Read models (perspectives updated correctly)
- ✅ Concurrent operations (no race conditions)
- ✅ Idempotency (duplicate messages handled)

---

## What Samples Are NOT

### ❌ NOT Experimental Playgrounds

**WRONG:**
```csharp
// "Let me try this experimental pattern in the sample"
public class ExperimentalOrderProcessor {
    // Trying out ideas before library supports them
}
```

**Samples demonstrate STABLE library features, not experiments.**

---

### ❌ NOT Independent Applications

**WRONG:**
```csharp
// "The sample can use a different messaging library for this part"
services.AddMassTransit(x => {
    // Using MassTransit instead of Whizbang
});
```

**Samples use Whizbang exclusively (that's the whole point).**

---

### ❌ NOT Workaround Showcases

**WRONG:**
```csharp
// "Here's how to work around Whizbang's limitations"
public static class WhizbangWorkarounds {
    // Hacks that users shouldn't need
}
```

**If workarounds are needed, library is incomplete.**

---

### ❌ NOT Testing Grounds

**WRONG:**
```csharp
// "Let's see if this half-baked library feature works in the sample"
public async Task TryUnstableFeature() {
    // Testing incomplete library code
}
```

**Library features must be complete BEFORE use in samples.**

---

## What Samples ARE

### ✅ Production-Quality Code

**Samples represent:**
- Best practices
- Real-world patterns
- Production-ready code
- What users should copy

**Code quality same as library:**
- 100% branch coverage in tests
- Proper error handling
- Comprehensive logging
- Clear naming
- XML doc comments

---

### ✅ Best Practices Showcase

**Samples demonstrate:**
- CQRS patterns (commands vs events)
- Event sourcing (order of events matters)
- Read models (perspectives for queries)
- Distributed tracing (causation chains)
- Error handling (transient vs permanent failures)
- Idempotency (duplicate message handling)
- Outbox pattern (reliable messaging)

---

### ✅ Real-World Patterns

**Not toy examples:**
```csharp
// ❌ NOT THIS (toy example)
public class Order {
    public int Id { get; set; }
    public string Name { get; set; }
}

// ✅ THIS (production-quality)
public class Order {
    public OrderId Id { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public List<OrderItem> Items { get; private set; } = new();
    public Address ShippingAddress { get; private set; }
    public decimal TotalAmount { get; private set; }

    private Order() { } // EF Core

    public static Order Create(
        OrderId id,
        CustomerId customerId,
        List<OrderItem> items,
        Address shippingAddress) {

        if (items.Count == 0)
            throw new InvalidOperationException("Order must have at least one item");

        var order = new Order {
            Id = id,
            CustomerId = customerId,
            Status = OrderStatus.Pending,
            Items = items,
            ShippingAddress = shippingAddress,
            TotalAmount = items.Sum(i => i.Quantity * i.Price)
        };

        return order;
    }

    public void ProcessPayment() {
        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException($"Cannot process payment for order in {Status} status");

        Status = OrderStatus.PaymentProcessed;
    }
}
```

---

### ✅ What Users Should Copy

**Samples answer:**
- "How do I structure my domain?"
- "How do I handle commands?"
- "How do I build read models?"
- "How do I handle errors?"
- "How do I test this?"
- "How do I deploy with AOT?"

---

## ECommerce Sample Structure

### Current Structure

```
samples/ECommerce/
├── ECommerce.Contracts/          # Shared messages (commands/events)
│   ├── Commands/
│   │   ├── CreateOrder.cs
│   │   ├── ProcessPayment.cs
│   │   └── ShipOrder.cs
│   ├── Events/
│   │   ├── OrderCreated.cs
│   │   ├── PaymentProcessed.cs
│   │   └── OrderShipped.cs
│   └── ValueObjects/
│       ├── OrderId.cs
│       ├── CustomerId.cs
│       └── ProductId.cs
│
├── ECommerce.OrderService.API/   # Order domain service
│   ├── Receptors/
│   │   ├── OrderReceptor.cs
│   │   └── PaymentReceptor.cs
│   ├── Perspectives/
│   │   └── OrderSummaryPerspective.cs
│   └── Program.cs
│
├── ECommerce.BFF.API/            # Backend-for-Frontend
│   ├── Controllers/
│   │   ├── OrdersController.cs
│   │   └── CustomersController.cs
│   ├── Perspectives/
│   │   └── CustomerOrdersPerspective.cs
│   └── Program.cs
│
├── ECommerce.InventoryWorker/    # Background worker
├── ECommerce.PaymentWorker/      # Background worker
├── ECommerce.ShippingWorker/     # Background worker
├── ECommerce.NotificationWorker/ # Background worker
│
├── ECommerce.UI/                 # Angular frontend
│
└── tests/
    ├── ECommerce.Integration.Tests/
    ├── ECommerce.OrderService.Tests/
    ├── ECommerce.BFF.API.Tests/
    └── ...
```

---

### What This Demonstrates

**Architecture Patterns:**
- Microservices (OrderService, InventoryWorker, etc.)
- CQRS (commands vs queries)
- Event sourcing (event-driven workflows)
- BFF pattern (backend-for-frontend)
- Background workers (async processing)

**Whizbang Features:**
- Receptors (IReceptor<TMessage, TResponse>)
- Perspectives (read model projections)
- Message routing
- Distributed tracing
- Outbox pattern
- Event store integration

**Real-World Scenarios:**
- Order processing workflow
- Payment processing
- Inventory management
- Shipping coordination
- Customer notifications
- Read model updates

---

## Common Patterns to Demonstrate

### 1. Command Handling

```csharp
public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
    private readonly IOrderRepository _repository;

    public async Task<OrderCreated> ReceiveAsync(CreateOrder command) {
        // Validate
        if (command.Items.Length == 0)
            throw new InvalidOperationException("Order must have items");

        // Create aggregate
        var order = Order.Create(
            id: OrderId.From(Guid.CreateVersion7()),
            customerId: command.CustomerId,
            items: command.Items.Select(i => new OrderItem(
                ProductId.From(i.ProductId),
                i.Quantity,
                i.Price
            )).ToList(),
            shippingAddress: command.ShippingAddress
        );

        // Persist
        await _repository.SaveAsync(order);

        // Return event
        return new OrderCreated(order.Id, order.CustomerId, order.TotalAmount);
    }
}
```

---

### 2. Event Handling (Side Effects)

```csharp
public class OrderCreatedReceptor : IReceptor<OrderCreated, VoidResponse> {
    private readonly INotificationService _notifications;

    public async Task<VoidResponse> ReceiveAsync(OrderCreated @event) {
        // Send confirmation email
        await _notifications.SendOrderConfirmationAsync(
            @event.CustomerId,
            @event.OrderId
        );

        return VoidResponse.Instance;
    }
}
```

---

### 3. Read Model Projection (Perspective)

```csharp
public class OrderSummaryPerspective : IPerspective {
    private readonly AppDbContext _context;

    public async Task ProjectAsync(IEvent @event) {
        switch (@event) {
            case OrderCreated created:
                await _context.OrderSummaries.AddAsync(new OrderSummary {
                    OrderId = created.OrderId,
                    CustomerId = created.CustomerId,
                    Status = "Pending",
                    TotalAmount = created.TotalAmount,
                    CreatedAt = DateTime.UtcNow
                });
                break;

            case PaymentProcessed processed:
                var order = await _context.OrderSummaries
                    .FindAsync(processed.OrderId);
                if (order != null) {
                    order.Status = "PaymentProcessed";
                    order.UpdatedAt = DateTime.UtcNow;
                }
                break;

            case OrderShipped shipped:
                var shippedOrder = await _context.OrderSummaries
                    .FindAsync(shipped.OrderId);
                if (shippedOrder != null) {
                    shippedOrder.Status = "Shipped";
                    shippedOrder.ShippedAt = DateTime.UtcNow;
                    shippedOrder.UpdatedAt = DateTime.UtcNow;
                }
                break;
        }

        await _context.SaveChangesAsync();
    }
}
```

---

## Quick Reference

### When Sample Needs Feature

```
1. ⛔ STOP sample work
2. 📝 Document what's needed
3. 🔧 Implement in LIBRARY
4. ✅ Test in library (100% coverage)
5. 📚 Document library feature
6. ✅ THEN use in sample
```

### Sample Requirements Checklist

- [ ] Uses Whizbang exclusively (no workarounds)
- [ ] AOT compatible (`PublishAot=true`)
- [ ] Publishes to native binary successfully
- [ ] Production-quality code
- [ ] Integration tests covering all workflows
- [ ] Demonstrates best practices
- [ ] Clear, self-documenting code
- [ ] Proper error handling
- [ ] XML doc comments on public APIs

---

## See Also

- [AOT Requirements](aot-requirements.md) - Zero reflection rules
- [Boy Scout Rule](boy-scout-rule.md) - Leave it better
- [TDD Strict](tdd-strict.md) - Test-driven development
- [Code Standards](code-standards.md) - Formatting and naming
