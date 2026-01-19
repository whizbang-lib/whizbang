# Code Standards

**Formatting, Naming, and Quality Requirements**

This document defines the code standards that apply to all C# code in the Whizbang project. These standards are enforced by EditorConfig and `dotnet format`.

---

## Table of Contents

1. [dotnet format - NON-NEGOTIABLE](#dotnet-format---non-negotiable)
2. [Async Naming Convention](#async-naming-convention)
3. [Naming Conventions](#naming-conventions)
4. [UUIDv7 for Identifiers](#uuidv7-for-identifiers)
5. [EditorConfig Standards](#editorconfig-standards)
6. [XML Documentation](#xml-documentation)
7. [Code Quality](#code-quality)

---

## dotnet format - NON-NEGOTIABLE

### ALWAYS Run dotnet format

> **`dotnet format` must be run before every completion.**

**This is NOT optional. This is NOT a suggestion. This is MANDATORY.**

```bash
# Before claiming work is complete
dotnet format

# Check if formatting is needed (CI/CD)
dotnet format --verify-no-changes
```

---

### Why This Matters

**Consistency:**
- Entire team uses same formatting
- No "style wars" in code reviews
- Diffs show real changes, not formatting

**Automation:**
- Tool enforces standards
- No manual checking needed
- Catches issues automatically

**Professionalism:**
- Clean, consistent code
- Easy to read and maintain
- Reflects quality culture

---

### When to Run

```
✅ Before every commit
✅ After refactoring
✅ After code generation
✅ When in doubt
```

**If you forget:**
- CI/CD will catch it
- PR will be blocked
- Wastes everyone's time

**Just run it every time.**

---

## Async Naming Convention

### ALL Async Methods Must End with "Async"

**This includes:**
- ✅ Public async methods
- ✅ Private async methods
- ✅ Test methods (even tests!)
- ✅ Interface methods
- ✅ Extension methods

**No exceptions.**

---

### ✅ CORRECT - Async Suffix

```csharp
// Public methods
public async Task<Order> ProcessOrderAsync(CreateOrder command) { }
public async Task SaveChangesAsync() { }

// Private methods
private async Task ValidateOrderAsync(Order order) { }
private async Task<bool> CheckInventoryAsync(ProductId id) { }

// Interface methods
public interface IOrderRepository {
    Task<Order> GetByIdAsync(OrderId id);
    Task SaveAsync(Order order);
}

// Test methods
[Test]
public async Task ProcessOrder_ValidInput_ReturnsOrderCreatedAsync() { }

[Test]
public async Task SaveOrder_NullOrder_ThrowsExceptionAsync() { }
```

---

### ❌ WRONG - Missing Async Suffix

```csharp
// ❌ WRONG
public async Task<Order> ProcessOrder(CreateOrder command) { }
private async Task ValidateOrder(Order order) { }

// ❌ WRONG - Even in tests!
[Test]
public async Task ProcessOrder_ValidInput_ReturnsOrderCreated() { }
```

**Why this is wrong:**
- Violates .NET conventions
- Makes it unclear method is async
- Inconsistent with ecosystem
- Fails code review

---

## Naming Conventions

### PascalCase - Types, Methods, Properties

```csharp
// Types
public class OrderService { }
public interface IOrderRepository { }
public record OrderCreated(Guid OrderId);
public enum OrderStatus { }

// Methods
public async Task ProcessOrderAsync() { }
public decimal CalculateTotal() { }

// Properties
public OrderId Id { get; private set; }
public OrderStatus Status { get; private set; }
public List<OrderItem> Items { get; private set; }

// Constants
public const int MaxRetries = 3;
public const string DefaultCurrency = "USD";
```

---

### camelCase - Parameters, Local Variables

```csharp
public async Task<OrderCreated> ProcessOrderAsync(CreateOrder command) {
    //                                             ^^^^^^^ camelCase parameter

    var orderId = Guid.CreateVersion7();
    //  ^^^^^^^ camelCase local variable

    var totalAmount = command.Items.Sum(i => i.Price * i.Quantity);
    //  ^^^^^^^^^^^ camelCase

    return new OrderCreated(orderId);
}
```

---

### _camelCase - Private Fields

```csharp
public class OrderService {
    private readonly IOrderRepository _repository;
    //                                ^^^^^^^^^^^ underscore + camelCase

    private readonly ILogger _logger;
    private readonly IMetrics _metrics;

    public OrderService(
        IOrderRepository repository,
        ILogger logger,
        IMetrics metrics) {

        _repository = repository;
        _logger = logger;
        _metrics = metrics;
    }
}
```

---

### IPascalCase - Interfaces

```csharp
// ✅ CORRECT
public interface IOrderRepository { }
public interface IMessageDispatcher { }
public interface IReceptor<TMessage, TResponse> { }

// ❌ WRONG
public interface OrderRepository { }  // Missing 'I' prefix
public interface iOrderRepository { } // Wrong casing
```

---

### MethodAsync - Async Methods

```csharp
// ✅ CORRECT
public async Task<Order> GetOrderAsync(OrderId id) { }
public async Task SaveAsync(Order order) { }
public async Task DeleteAsync(OrderId id) { }

// ❌ WRONG
public async Task<Order> GetOrder(OrderId id) { }
public async Task Save(Order order) { }
```

---

## UUIDv7 for Identifiers

### ALWAYS Use Guid.CreateVersion7()

**For all entity IDs:**

```csharp
// ✅ CORRECT - UUIDv7 (time-ordered)
var orderId = OrderId.From(Guid.CreateVersion7());
var customerId = CustomerId.From(Guid.CreateVersion7());
var productId = ProductId.From(Guid.CreateVersion7());
```

**❌ WRONG - Guid.NewGuid() (random):**

```csharp
// ❌ WRONG
var orderId = OrderId.From(Guid.NewGuid());  // Random GUID
```

---

### Why UUIDv7?

**Benefits:**
- ✅ Time-ordered (sortable by creation time)
- ✅ Database index-friendly (no fragmentation)
- ✅ Clustered index performance
- ✅ Compatible with standard GUID/UUID
- ✅ Native PostgreSQL 18+ support (`uuidv7()`)

**Problems with Guid.NewGuid():**
- ❌ Random (no ordering)
- ❌ Causes index fragmentation
- ❌ Poor database performance
- ❌ Page splits in B-trees

---

## EditorConfig Standards

### Our EditorConfig Rules

The `.editorconfig` file enforces these standards automatically.

**Key Rules:**

```ini
# Indentation
indent_style = space
indent_size = 4

# New lines
end_of_line = lf
insert_final_newline = true
trim_trailing_whitespace = true

# Naming
dotnet_naming_rule.async_methods_must_end_with_async.severity = error

# Modern C# features
csharp_prefer_simple_using_statement = true
csharp_style_namespace_declarations = file_scoped

# Null safety
dotnet_diagnostic.CS8618.severity = error  # Non-nullable field uninitialized
dotnet_diagnostic.CS8625.severity = error  # Cannot convert null literal
```

---

### File-Scoped Namespaces

```csharp
// ✅ CORRECT - File-scoped namespace
namespace Whizbang.Core.Domain;

public class Order {
    // No extra indentation
}
```

```csharp
// ❌ WRONG - Block-scoped namespace
namespace Whizbang.Core.Domain {
    public class Order {
        // Extra indentation level
    }
}
```

---

### Using Directives

```csharp
// ✅ CORRECT - Outside namespace, System first
using System;
using System.Collections.Generic;
using System.Linq;
using Whizbang.Core;
using Whizbang.Core.Domain;

namespace Whizbang.Core.Services;

public class OrderService { }
```

---

## XML Documentation

### All Public APIs MUST Have XML Docs

```csharp
/// <summary>
/// Represents a receptor that handles CreateOrder commands.
/// </summary>
public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {

    /// <summary>
    /// Processes a CreateOrder command and creates a new order in the system.
    /// </summary>
    /// <param name="message">The CreateOrder command containing order details.</param>
    /// <returns>An OrderCreated event with the new order's ID.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the order contains no items.
    /// </exception>
    public async Task<OrderCreated> ReceiveAsync(CreateOrder message) {
        if (message.Items.Length == 0)
            throw new InvalidOperationException("Order must have at least one item");

        // Implementation...
    }
}
```

---

### XML Doc Requirements

**MUST document:**
- [ ] `<summary>` - What the type/member does
- [ ] `<param>` - Each parameter's purpose
- [ ] `<returns>` - What the method returns
- [ ] `<exception>` - Exceptions that can be thrown
- [ ] `<remarks>` - Additional context (when needed)

**Examples:**

```csharp
/// <summary>
/// Calculates the total amount for an order including all items.
/// </summary>
/// <param name="items">The order items to total.</param>
/// <returns>The sum of all item prices multiplied by quantities.</returns>
/// <exception cref="ArgumentNullException">
/// Thrown when <paramref name="items"/> is null.
/// </exception>
/// <remarks>
/// This method does not include tax or shipping costs.
/// </remarks>
public decimal CalculateTotal(List<OrderItem> items) {
    // Implementation
}
```

---

## Code Quality

### Modern C# Features

**Use modern C# patterns:**

```csharp
// ✅ Pattern matching
if (message is CreateOrder createOrder) {
    await ProcessAsync(createOrder);
}

// ✅ Switch expressions
var status = orderState switch {
    OrderState.Pending => OrderStatus.Pending,
    OrderState.Processed => OrderStatus.Completed,
    _ => OrderStatus.Unknown
};

// ✅ Null coalescing
var name = customer.Name ?? "Unknown";

// ✅ Index/range operators
var lastItem = items[^1];
var firstThree = items[..3];

// ✅ Target-typed new
OrderId id = new(Guid.CreateVersion7());
```

---

### Avoid Code Smells

**❌ Magic Numbers:**
```csharp
// ❌ WRONG
if (order.Total > 1000) { }

// ✅ CORRECT
private const decimal HighValueThreshold = 1000m;
if (order.Total > HighValueThreshold) { }
```

**❌ Deep Nesting:**
```csharp
// ❌ WRONG - Deep nesting
public void Process(Order order) {
    if (order != null) {
        if (order.Items.Count > 0) {
            if (order.Total > 0) {
                // Deeply nested logic
            }
        }
    }
}

// ✅ CORRECT - Guard clauses
public void Process(Order order) {
    if (order == null) throw new ArgumentNullException(nameof(order));
    if (order.Items.Count == 0) throw new InvalidOperationException("No items");
    if (order.Total <= 0) throw new InvalidOperationException("Invalid total");

    // Main logic at top level
}
```

**❌ Long Methods:**
```csharp
// ❌ WRONG - 100+ line method
public async Task ProcessOrderAsync(CreateOrder command) {
    // 100 lines of code...
}

// ✅ CORRECT - Extract methods
public async Task ProcessOrderAsync(CreateOrder command) {
    await ValidateOrderAsync(command);
    var order = await CreateOrderAsync(command);
    await NotifyCustomerAsync(order);
    return order;
}

private async Task ValidateOrderAsync(CreateOrder command) { }
private async Task<Order> CreateOrderAsync(CreateOrder command) { }
private async Task NotifyCustomerAsync(Order order) { }
```

---

### Nullable Reference Types

**Enable nullable reference types:**

```csharp
#nullable enable

public class Order {
    // Non-nullable by default
    public OrderId Id { get; private set; }
    public CustomerId CustomerId { get; private set; }

    // Explicitly nullable
    public string? Notes { get; set; }
    public DateTime? ShippedAt { get; set; }

    // List initialized to avoid null
    public List<OrderItem> Items { get; private set; } = new();
}
```

---

## Pre-Commit Checklist

**Before every commit:**

- [ ] `dotnet format` run (NON-NEGOTIABLE)
- [ ] All async methods end with "Async"
- [ ] Naming conventions followed
- [ ] UUIDs use `Guid.CreateVersion7()`
- [ ] XML docs on all public APIs
- [ ] No magic numbers
- [ ] No deep nesting
- [ ] Methods are focused and short
- [ ] Modern C# features used appropriately

---

## Quick Reference

### Formatting
```bash
# ALWAYS run before committing
dotnet format

# Verify formatting (CI/CD)
dotnet format --verify-no-changes
```

### Naming
```csharp
PascalCase:  OrderService, ProcessOrderAsync(), OrderId
camelCase:   command, orderId, totalAmount
_camelCase:  _repository, _logger, _metrics
IPascalCase: IOrderRepository, IReceptor<TMessage, TResponse>
MethodAsync: ProcessOrderAsync(), SaveAsync(), GetByIdAsync()
```

### IDs
```csharp
// ✅ ALWAYS
Guid.CreateVersion7()

// ❌ NEVER
Guid.NewGuid()
```

### Documentation
```csharp
/// <summary>What it does</summary>
/// <param name="x">Parameter purpose</param>
/// <returns>What it returns</returns>
/// <exception cref="Exception">When thrown</exception>
```

---

## See Also

- [TDD Strict](tdd-strict.md) - Test-driven development
- [Boy Scout Rule](boy-scout-rule.md) - REFACTOR phase applies these standards
- [AOT Requirements](aot-requirements.md) - Zero reflection
- [EF Core 10 Usage](efcore-10-usage.md) - UUIDv7 with PostgreSQL
