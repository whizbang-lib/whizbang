# TDD Strict - Test-Driven Development

**RED → GREEN → REFACTOR - No Exceptions**

Test-Driven Development is not optional in Whizbang. This document defines the strict TDD process that must be followed for all production code.

---

## Table of Contents

1. [The TDD Cycle](#the-tdd-cycle)
2. [Test-First Rule](#test-first-rule)
3. [Coverage Requirements](#coverage-requirements)
4. [TDD Phase Breakdown](#tdd-phase-breakdown)
5. [Common Mistakes](#common-mistakes)
6. [Integration with Boy Scout Rule](#integration-with-boy-scout-rule)

---

## The TDD Cycle

### The Three Phases

```
🔴 RED
  ↓
🟢 GREEN
  ↓
🔵 REFACTOR
  ↓
🔴 RED (next test)
```

**Each phase has specific goals and constraints.**

---

## Test-First Rule

### ✅ CORRECT - Write Tests First

```
1. Write failing test (RED)
   ↓
2. Write minimal code to pass (GREEN)
   ↓
3. Refactor and improve (REFACTOR)
```

### ❌ WRONG - Implementation First

```
1. Write implementation code    ← WRONG!
   ↓
2. Write tests after the fact   ← Tests become validations, not designs
   ↓
3. Tests pass (maybe)           ← Doesn't drive design
```

**Why test-first matters:**
- Tests define the API before implementation
- Forces thinking about usage patterns
- Catches design issues early
- Ensures testability from the start
- Prevents over-engineering

---

### The Golden Rule

> **If you write implementation before tests, you're doing it wrong.**

**No exceptions. No excuses.**

---

## Coverage Requirements

### 100% Branch Coverage Goal

**Not a suggestion - a goal we actively work toward.**

```bash
# Run coverage for project
cd tests/Whizbang.Core.Tests
dotnet run -- --coverage --coverage-output-format cobertura

# Check coverage percentage
# Goal: 100% branch coverage
```

### What 100% Branch Coverage Means

**Every code path must be tested:**

```csharp
// This method has 3 branches
public OrderStatus CalculateStatus(Order order) {
    if (order.Items.Count == 0)
        return OrderStatus.Invalid;     // Branch 1

    if (order.TotalAmount > 1000)
        return OrderStatus.HighValue;   // Branch 2

    return OrderStatus.Normal;           // Branch 3
}

// Need 3 tests minimum to cover all branches:
[Test] public async Task CalculateStatus_NoItems_ReturnsInvalidAsync() { }
[Test] public async Task CalculateStatus_HighValue_ReturnsHighValueAsync() { }
[Test] public async Task CalculateStatus_Normal_ReturnsNormalAsync() { }
```

### Boy Scout Rule Applies

**If you discover uncovered code:**
1. Don't ignore it
2. Add tests to cover the gaps
3. Strive toward 100%

**"But it was already uncovered"** is NOT an excuse (see [Boy Scout Rule](boy-scout-rule.md)).

---

## TDD Phase Breakdown

### 🔴 Phase 1: RED - Write Failing Test

**Goal:** Define expected behavior through a failing test.

**Process:**
1. Think about the next small piece of functionality
2. Write a test that describes the expected behavior
3. Run the test - it MUST fail
4. If it passes, you're not testing new functionality

**Example:**

```csharp
[Test]
public async Task ProcessOrder_ValidOrder_ReturnsOrderCreatedAsync() {
    // Arrange
    var receptor = new OrderReceptor();  // Doesn't exist yet!
    var command = new CreateOrder {
        CustomerId = Guid.CreateVersion7(),
        Items = new[] {
            new OrderItem { ProductId = Guid.CreateVersion7(), Quantity = 1, Price = 29.99m }
        }
    };

    // Act
    var result = await receptor.ReceiveAsync(command);  // Method doesn't exist yet!

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);
}
```

**Run test:** ❌ Fails (compilation error - types don't exist)

---

### 🟢 Phase 2: GREEN - Make Test Pass

**Goal:** Write MINIMAL code to make the test pass.

**Constraints:**
- Write only enough code to pass THIS test
- Don't worry about perfect code yet
- Don't add features not covered by tests
- Resist the urge to over-engineer

**Example:**

```csharp
// Minimal implementation to make test pass
public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
    public async Task<OrderCreated> ReceiveAsync(CreateOrder message) {
        // Simplest thing that works
        return new OrderCreated(Guid.CreateVersion7());
    }
}
```

**Run test:** ✅ Passes

**This code is intentionally simple.** Improvements come in REFACTOR phase.

---

### 🔵 Phase 3: REFACTOR - Improve the Code

**Goal:** Clean up code while keeping tests green.

**This is where Boy Scout Rule shines:**
- Improve new code
- Improve existing code
- Extract duplication
- Fix naming inconsistencies
- Add XML doc comments
- Run `dotnet format`
- Update documentation (if public APIs changed - see [documentation-maintenance.md](documentation-maintenance.md))

**Example:**

```csharp
// After REFACTOR - cleaner, more complete
public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
    private readonly IOrderRepository _repository;

    public OrderReceptor(IOrderRepository repository) {
        _repository = repository;
    }

    /// <summary>
    /// Receives a CreateOrder command and creates a new order.
    /// </summary>
    public async Task<OrderCreated> ReceiveAsync(CreateOrder message) {
        // Validate
        if (message.Items.Length == 0)
            throw new InvalidOperationException("Order must have at least one item");

        // Create aggregate
        var order = Order.Create(
            id: OrderId.From(Guid.CreateVersion7()),
            customerId: message.CustomerId,
            items: message.Items.Select(i => new OrderItem(
                ProductId.From(i.ProductId),
                i.Quantity,
                i.Price
            )).ToList()
        );

        // Persist
        await _repository.SaveAsync(order);

        // Return event
        return new OrderCreated(order.Id);
    }
}
```

**Run tests:** ✅ Still passes (but code is much better)

**Also:**
- Added validation test
- Added repository test
- Fixed naming in other files
- Added XML doc comments
- Ran `dotnet format`

---

### The Cycle Continues

After REFACTOR, write the NEXT test (back to RED phase):

```csharp
[Test]
public async Task ProcessOrder_EmptyItems_ThrowsExceptionAsync() {
    // RED: Testing validation
    var receptor = new OrderReceptor(_mockRepository);
    var command = new CreateOrder {
        CustomerId = Guid.CreateVersion7(),
        Items = Array.Empty<OrderItem>()  // Invalid!
    };

    // Should throw
    await Assert.That(async () => await receptor.ReceiveAsync(command))
        .ThrowsAsync<InvalidOperationException>();
}
```

---

## Common Mistakes

### ❌ Mistake 1: Writing Implementation First

**WRONG:**
```
1. Write OrderReceptor class
2. Write implementation logic
3. Write tests afterward
```

**Why wrong:**
- Tests don't drive design
- May test implementation, not behavior
- Often miss edge cases
- Creates untestable code

**✅ CORRECT:**
```
1. Write test defining expected behavior
2. Write minimal implementation
3. Refactor to improve
```

---

### ❌ Mistake 2: Skipping RED Phase

**WRONG:**
```csharp
// Test passes immediately - you're not testing new code!
[Test]
public async Task ExistingFeature_Works_PassesAsync() {
    var result = await _service.ExistingMethodAsync();
    await Assert.That(result).IsNotNull();  // Already passes
}
```

**Why wrong:**
- Not testing new functionality
- False sense of coverage
- Doesn't drive development

**✅ CORRECT:**
- Test must FAIL first
- Then write code to make it pass
- Proves test actually tests something

---

### ❌ Mistake 3: Over-Engineering in GREEN Phase

**WRONG:**
```csharp
// GREEN phase - wrote way too much!
public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
    // Added caching (not tested)
    private readonly ICache _cache;

    // Added retry logic (not tested)
    private readonly RetryPolicy _retryPolicy;

    // Added metrics (not tested)
    private readonly IMetrics _metrics;

    public async Task<OrderCreated> ReceiveAsync(CreateOrder message) {
        // 100 lines of code for one test!
    }
}
```

**Why wrong:**
- Added features without tests
- Makes refactoring harder
- Violates "minimal code to pass"

**✅ CORRECT:**
```csharp
// GREEN phase - just enough to pass
public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
    public async Task<OrderCreated> ReceiveAsync(CreateOrder message) {
        return new OrderCreated(Guid.CreateVersion7());
    }
}
// Add features incrementally with more tests
```

---

### ❌ Mistake 4: Ignoring REFACTOR Phase

**WRONG:**
```csharp
// Test passes, move to next test without refactoring
public async Task<OrderCreated> ReceiveAsync(CreateOrder message) {
    // Messy code, duplication, poor naming - but it works!
    var o = new Order();
    o.Id = Guid.CreateVersion7();
    o.Items = message.Items.Select(i => new OrderItem { ProductId = i.ProductId }).ToList();
    _repo.Save(o);
    return new OrderCreated(o.Id);
}
// Move to next test → Technical debt accumulates
```

**Why wrong:**
- Technical debt accumulates
- Code becomes harder to change
- Violates Boy Scout Rule

**✅ CORRECT:**
- After GREEN, always REFACTOR
- Clean up code
- Fix naming
- Extract duplication
- Run `dotnet format`
- Then move to next test

---

### ❌ Mistake 5: Accepting Low Coverage

**WRONG:**
```
Developer: "I got 60% branch coverage, that's good enough."
```

**Why wrong:**
- Goal is 100%
- 40% of code paths untested
- Ignores Boy Scout Rule
- Creates false confidence

**✅ CORRECT:**
```
Developer: "I got 60% branch coverage. Let me:
1. Identify uncovered branches
2. Add tests for each branch
3. Reach 100% before considering this complete"
```

---

## Integration with Boy Scout Rule

### REFACTOR Phase is Boy Scout Time

The REFACTOR phase is your opportunity to leave code better than you found it:

**During REFACTOR:**
- ✅ Clean up new code
- ✅ Clean up existing code you touched
- ✅ Improve related tests
- ✅ Fix naming inconsistencies
- ✅ Remove duplication
- ✅ Add missing docs
- ✅ Run `dotnet format`

**Example workflow:**

```
🔴 RED: Write test for OrderReceptor
  - Notice: Existing CustomerReceptor has poor naming
  - UPDATE PLAN: Fix during REFACTOR

🟢 GREEN: Implement OrderReceptor (minimal)

🔵 REFACTOR:
  - Clean up OrderReceptor
  - Fix CustomerReceptor naming (Boy Scout!)
  - Extract common validation logic
  - Add XML doc comments to both
  - Run dotnet format
  - Tests still green ✅

🔴 RED: Next test...
```

---

## Pre-Commit Checklist

Before every commit:

- [ ] All tests passing (GREEN)
- [ ] Code refactored (REFACTOR complete)
- [ ] `dotnet format` run
- [ ] Coverage collected
- [ ] Working toward 100% branch coverage
- [ ] Boy Scout Rule applied
- [ ] No TODO comments (create issues instead)
- [ ] Documentation updated (if public APIs changed in ANY project)

---

## Quick Reference

### The Cycle

```
🔴 RED:
- Write failing test
- Define expected behavior
- Test MUST fail

🟢 GREEN:
- Write minimal code
- Make test pass
- Don't over-engineer

🔵 REFACTOR:
- Clean up code
- Apply Boy Scout Rule
- Run dotnet format
- Keep tests green

Repeat for next feature
```

### Test-First Rule

> **Tests before implementation. Always. No exceptions.**

### Coverage Goal

> **100% branch coverage - Not a limit, a goal we work toward.**

### Boy Scout Integration

> **REFACTOR phase = Boy Scout time.**

---

## See Also

- [Testing TUnit](testing-tunit.md) - TUnit patterns, Rocks, Bogus
- [Documentation Maintenance](documentation-maintenance.md) - Keep docs synchronized with code
- [Boy Scout Rule](boy-scout-rule.md) - Leave code better
- [Code Standards](code-standards.md) - Formatting and naming
- [AOT Requirements](aot-requirements.md) - Zero reflection
