# TUnit Testing Standards

**Critical Documentation for Modern .NET Testing**

TUnit is a modern, source-generation-based testing framework that works fundamentally differently from xUnit/NUnit/MSTest. This document covers the critical differences Claude Code must understand.

---

## Table of Contents

1. [Critical TUnit CLI Differences](#critical-tunit-cli-differences)
2. [TUnit Test Patterns](#tunit-test-patterns)
3. [Rocks Mocking Library](#rocks-mocking-library)
4. [Bogus Fake Data Generation](#bogus-fake-data-generation)
5. [Common Mistakes](#common-mistakes)

---

## Critical TUnit CLI Differences

### ❌ WRONG - Standard dotnet test filtering

TUnit does **NOT** use the standard `--filter` argument like other frameworks!

```bash
# ❌ DOESN'T WORK with TUnit!
dotnet test --filter "FullyQualifiedName~MyTest"
dotnet test --filter "TestCategory=Integration"
```

### ✅ CORRECT - TUnit uses `--treenode-filter`

TUnit is built on Microsoft.Testing.Platform and uses tree-based filtering:

```bash
# With dotnet run (PREFERRED)
dotnet run --treenode-filter "/{Assembly}/{Namespace}/{Class}/{Method}"
dotnet run --treenode-filter "/*/*/MyTestClass/*"
dotnet run --treenode-filter "/*/*/*/*[Category=Integration]"

# With dotnet test (requires -- before flags)
dotnet test -- --treenode-filter "/*/*/MyTestClass/*"
dotnet test -- --treenode-filter "/*/*/*/*[Category=Integration]"
```

### TreeNode Filter Syntax

**Format:** `/{AssemblyName}/{Namespace}/{ClassName}/{MethodName}`

**Wildcards:**
- `*` matches any single segment
- `**` matches any number of segments (not widely supported yet)

**Property Filters:**
- Use `[PropertyName=Value]` syntax
- Example: `/*/*/*/*[Category=Integration]`

**Examples:**

```bash
# Run all tests in a specific class
dotnet run --treenode-filter "/*/*/OrderServiceTests/*"

# Run specific test method
dotnet run --treenode-filter "/*/*/OrderServiceTests/ProcessOrder_ValidInput_ReturnsSuccessAsync"

# Run all tests with Category property
dotnet run --treenode-filter "/*/*/*/*[Category=Integration]"

# Exclusion (NOT operator)
dotnet run --treenode-filter "/*/(! *SmokeTests)/*/*"
```

---

## Coverage Collection

### ❌ WRONG - Old coverage approach

```bash
# Doesn't work with TUnit/Microsoft.Testing.Platform
dotnet test --collect:"XPlat Code Coverage"
```

### ✅ CORRECT - TUnit coverage collection

TUnit uses `dotnet run` with Microsoft.Testing.Extensions.CodeCoverage:

```bash
# Navigate to test project directory
cd tests/Whizbang.Core.Tests

# Run tests with coverage using dotnet run
dotnet run -- --coverage --coverage-output-format cobertura --coverage-output coverage.xml

# Coverage file location
# bin/Debug/net10.0/TestResults/coverage.xml
```

### Coverage Options

```bash
--coverage                              # Enable coverage collection
--coverage-output-format cobertura      # Format: cobertura, xml, or coverage (binary)
--coverage-output coverage.xml          # Output file path
```

---

## Additional TUnit CLI Flags

```bash
--timeout 30s                  # Global test timeout (format: [h|m|s])
--report-trx                   # Generate TRX test results
--minimum-expected-tests 50    # Fail if fewer tests found
--help                         # Show all available options
```

### Complete Example

```bash
# Full test run with coverage and TRX report
cd tests/Whizbang.Core.Tests
dotnet run -- \
  --coverage \
  --coverage-output-format cobertura \
  --coverage-output coverage.xml \
  --report-trx \
  --timeout 5m \
  --treenode-filter "/*/*/*/*[!Category=Slow]"
```

---

## TUnit Test Patterns

### Test Structure

```csharp
using TUnit.Assertions;
using TUnit.Core;

namespace Whizbang.Core.Tests;

public class OrderServiceTests {

    [Test]
    public async Task ProcessOrder_ValidInput_ReturnsSuccessAsync() {
        // Arrange
        var order = new Order { Id = Guid.CreateVersion7() };
        var service = new OrderService();

        // Act
        var result = await service.ProcessAsync(order);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Status).IsEqualTo(OrderStatus.Completed);
    }
}
```

### Critical Naming Convention

✅ **ALL async test methods MUST end with `Async` suffix:**

```csharp
// ✅ CORRECT
[Test]
public async Task ProcessOrder_ValidInput_ReturnsSuccessAsync() { }

[Test]
public async Task GetCustomer_NotFound_ThrowsExceptionAsync() { }

// ❌ WRONG - Missing Async suffix
[Test]
public async Task ProcessOrder_ValidInput_ReturnsSuccess() { }  // WRONG!
```

**Naming Pattern:** `MethodUnderTest_Scenario_ExpectedOutcomeAsync`

---

## TUnit Assertions

### ✅ CORRECT - TUnit Fluent Assertions

```csharp
// Equality
await Assert.That(actual).IsEqualTo(expected);
await Assert.That(value).IsNotEqualTo(other);

// Null checks
await Assert.That(result).IsNotNull();
await Assert.That(result).IsNull();

// Boolean
await Assert.That(condition).IsTrue();
await Assert.That(condition).IsFalse();

// Collections
await Assert.That(list).HasCount().EqualTo(3);
await Assert.That(list).IsEmpty();
await Assert.That(list).Contains(item);

// Strings
await Assert.That(text).IsEqualTo("expected");
await Assert.That(text).Contains("substring");
await Assert.That(text).StartsWith("prefix");

// Exceptions
await Assert.That(async () => await service.FailAsync())
    .ThrowsAsync<InvalidOperationException>();

// Numeric comparisons
await Assert.That(value).IsGreaterThan(10);
await Assert.That(value).IsLessThan(100);
await Assert.That(value).IsBetween(10, 100);
```

### ❌ WRONG - Other framework assertions

```csharp
// ❌ xUnit style - DON'T USE
Assert.Equal(expected, actual);
Assert.NotNull(result);
Assert.True(condition);
Assert.Throws<Exception>(() => method());

// ❌ NUnit style - DON'T USE
Assert.AreEqual(expected, actual);
Assert.IsNotNull(result);
Assert.IsTrue(condition);
Assert.Throws<Exception>(() => method());

// ❌ MSTest style - DON'T USE
Assert.AreEqual(expected, actual);
Assert.IsNotNull(result);
Assert.IsTrue(condition);
```

---

## Property-Based Test Metadata

Use `[Property]` attribute for categorization and filtering:

```csharp
[Test]
[Property("Category", "Integration")]
[Property("Priority", "High")]
public async Task Integration_CreateOrderFlow_ProcessesCorrectlyAsync() {
    // Test implementation
}

// Run only integration tests
// dotnet run -- --treenode-filter "/*/*/*/*[Category=Integration]"
```

---

## Rocks Mocking Library

Rocks is a **source-generated** mocking library (AOT compatible, zero reflection).

### Basic Pattern

```csharp
using Rocks;

[Test]
public async Task ProcessOrder_CallsRepository_SavesOrderAsync() {
    // Arrange - Create expectations
    var expectations = Rock.Create<IOrderRepository>();

    // Setup method expectations
    expectations.Methods()
        .SaveAsync(Arg.Any<Order>())
        .Returns(Task.CompletedTask);

    expectations.Methods()
        .GetByIdAsync(Arg.Is<Guid>(id => id != Guid.Empty))
        .Returns(Task.FromResult(new Order { Id = Guid.CreateVersion7() }));

    // Get mock instance
    var mockRepository = expectations.Instance();
    var service = new OrderService(mockRepository);

    // Act
    var order = new Order { Id = Guid.CreateVersion7() };
    await service.ProcessAsync(order);

    // Assert - Verify expectations were met
    expectations.Verify();
}
```

### Key Rocks Concepts

**1. Create Expectations:**
```csharp
var expectations = Rock.Create<IMyInterface>();
```

**2. Setup Method Returns:**
```csharp
// Simple return value
expectations.Methods().CalculateTotal()
    .Returns(100.50m);

// Async return
expectations.Methods().GetDataAsync()
    .Returns(Task.FromResult(data));

// Multiple setups for same method (different args)
expectations.Methods().ProcessAsync(Arg.Is<Order>(o => o.Id == orderId1))
    .Returns(Task.FromResult(true));
expectations.Methods().ProcessAsync(Arg.Is<Order>(o => o.Id == orderId2))
    .Returns(Task.FromResult(false));
```

**3. Argument Matching:**
```csharp
// Any value
expectations.Methods().ProcessAsync(Arg.Any<Order>())
    .Returns(Task.CompletedTask);

// Specific value
expectations.Methods().GetByIdAsync(Arg.Is<Guid>(specificId))
    .Returns(Task.FromResult(order));

// Predicate matching
expectations.Methods().ProcessAsync(Arg.Is<Order>(o => o.TotalAmount > 100))
    .Returns(Task.FromResult(true));
```

**4. Get Mock Instance:**
```csharp
var mock = expectations.Instance();
```

**5. Verify Calls:**
```csharp
expectations.Verify();  // Throws if expected calls didn't happen
```

### Rocks vs Moq

**Key Differences:**

| Feature | Rocks | Moq |
|---------|-------|-----|
| Generation | Source-generated | Reflection-based |
| AOT Compatible | ✅ Yes | ❌ No |
| Debuggable | ✅ Can step into mock code | ❌ Dynamic proxy |
| Performance | ✅ Faster (compile-time) | ⚠️ Slower (runtime) |
| API Style | Different | Traditional |

**Migration Note:** Don't use Moq patterns with Rocks - the API is different!

---

## Bogus Fake Data Generation

Bogus generates realistic fake data for tests.

### Basic Faker<T> Pattern

```csharp
using Bogus;

var orderFaker = new Faker<Order>()
    .RuleFor(o => o.Id, f => Guid.CreateVersion7())  // UUIDv7 for time-ordered IDs
    .RuleFor(o => o.CustomerId, f => Guid.CreateVersion7())
    .RuleFor(o => o.TotalAmount, f => f.Finance.Amount(10, 1000))
    .RuleFor(o => o.CreatedAt, f => f.Date.Recent(30))
    .RuleFor(o => o.Status, f => f.PickRandom<OrderStatus>())
    .RuleFor(o => o.Items, f => new Faker<OrderItem>()
        .RuleFor(i => i.ProductId, f => Guid.CreateVersion7())
        .RuleFor(i => i.Quantity, f => f.Random.Int(1, 10))
        .RuleFor(i => i.Price, f => f.Finance.Amount(5, 500))
        .Generate(f.Random.Int(1, 5)));

// Generate single instance
var order = orderFaker.Generate();

// Generate collection
var orders = orderFaker.Generate(50);
```

### Common Bogus Patterns

**Person Data:**
```csharp
var customerFaker = new Faker<Customer>()
    .RuleFor(c => c.Id, f => Guid.CreateVersion7())
    .RuleFor(c => c.FirstName, f => f.Name.FirstName())
    .RuleFor(c => c.LastName, f => f.Name.LastName())
    .RuleFor(c => c.FullName, f => f.Name.FullName())
    .RuleFor(c => c.Email, f => f.Internet.Email())
    .RuleFor(c => c.Phone, f => f.Phone.PhoneNumber())
    .RuleFor(c => c.DateOfBirth, f => f.Date.Past(50, DateTime.Now.AddYears(-18)));
```

**Address Data:**
```csharp
var addressFaker = new Faker<Address>()
    .RuleFor(a => a.Street, f => f.Address.StreetAddress())
    .RuleFor(a => a.City, f => f.Address.City())
    .RuleFor(a => a.State, f => f.Address.State())
    .RuleFor(a => a.PostalCode, f => f.Address.ZipCode())
    .RuleFor(a => a.Country, f => f.Address.Country());
```

**Commerce Data:**
```csharp
var productFaker = new Faker<Product>()
    .RuleFor(p => p.Id, f => Guid.CreateVersion7())
    .RuleFor(p => p.Name, f => f.Commerce.ProductName())
    .RuleFor(p => p.Description, f => f.Commerce.ProductDescription())
    .RuleFor(p => p.Price, f => f.Finance.Amount(10, 1000))
    .RuleFor(p => p.Category, f => f.Commerce.Categories(1)[0])
    .RuleFor(p => p.Ean, f => f.Commerce.Ean13());
```

**Date/Time Data:**
```csharp
var eventFaker = new Faker<Event>()
    .RuleFor(e => e.StartDate, f => f.Date.Future())
    .RuleFor(e => e.EndDate, (f, e) => f.Date.Future(refDate: e.StartDate))
    .RuleFor(e => e.CreatedAt, f => f.Date.Recent(7))
    .RuleFor(e => e.UpdatedAt, (f, e) => f.Date.Between(e.CreatedAt, DateTime.Now));
```

### Locale Support

```csharp
// Default: en (English)
var faker = new Faker();

// Brazilian Portuguese
var fakerPtBr = new Faker("pt_BR");
var name = fakerPtBr.Name.FullName();  // "Carlos Henrique"

// French
var fakerFr = new Faker("fr");
var name = fakerFr.Name.FullName();  // "Jean-Pierre Dubois"

// Set locale for Faker<T>
var customerFaker = new Faker<Customer>("pt_BR")
    .RuleFor(c => c.Name, f => f.Name.FullName());
```

### Deterministic Generation (Reproducible Tests)

```csharp
// Use seed for reproducible fake data
var seed = 12345;
var faker = new Faker<Order>()
    .UseSeed(seed)
    .RuleFor(o => o.Id, f => Guid.CreateVersion7())
    .RuleFor(o => o.TotalAmount, f => f.Finance.Amount(10, 1000));

// Same seed = same data
var orders1 = faker.Generate(10);
var orders2 = faker.Generate(10);
// orders1 and orders2 will be identical
```

### Integration with Tests

```csharp
public class OrderServiceTests {
    private readonly Faker<Order> _orderFaker;
    private readonly Faker<Customer> _customerFaker;

    public OrderServiceTests() {
        _orderFaker = new Faker<Order>()
            .RuleFor(o => o.Id, f => Guid.CreateVersion7())
            .RuleFor(o => o.TotalAmount, f => f.Finance.Amount(10, 1000))
            .RuleFor(o => o.Status, f => OrderStatus.Pending);

        _customerFaker = new Faker<Customer>()
            .RuleFor(c => c.Id, f => Guid.CreateVersion7())
            .RuleFor(c => c.Email, f => f.Internet.Email());
    }

    [Test]
    public async Task BulkProcessOrders_ManyOrders_ProcessesAllAsync() {
        // Arrange - Generate realistic test data
        var orders = _orderFaker.Generate(100);
        var service = new OrderService();

        // Act
        var results = await service.ProcessManyAsync(orders);

        // Assert
        await Assert.That(results).HasCount().EqualTo(100);
        await Assert.That(results.All(r => r.Success)).IsTrue();
    }
}
```

---

## Common Mistakes

### ❌ Mistake 1: Using wrong filter syntax

```bash
# WRONG
dotnet test --filter "ClassName=OrderServiceTests"

# CORRECT
dotnet run -- --treenode-filter "/*/*/OrderServiceTests/*"
```

### ❌ Mistake 2: Forgetting Async suffix

```csharp
// WRONG
[Test]
public async Task ProcessOrder_ValidInput_ReturnsSuccess() { }

// CORRECT
[Test]
public async Task ProcessOrder_ValidInput_ReturnsSuccessAsync() { }
```

### ❌ Mistake 3: Using xUnit/NUnit assertions

```csharp
// WRONG
Assert.Equal(expected, actual);
Assert.NotNull(result);

// CORRECT
await Assert.That(actual).IsEqualTo(expected);
await Assert.That(result).IsNotNull();
```

### ❌ Mistake 4: Using Moq patterns with Rocks

```csharp
// WRONG (Moq style)
var mock = new Mock<IRepository>();
mock.Setup(r => r.GetAsync(It.IsAny<Guid>())).ReturnsAsync(data);

// CORRECT (Rocks style)
var expectations = Rock.Create<IRepository>();
expectations.Methods().GetAsync(Arg.Any<Guid>()).Returns(Task.FromResult(data));
var mock = expectations.Instance();
```

### ❌ Mistake 5: Wrong coverage collection

```bash
# WRONG
dotnet test --collect:"XPlat Code Coverage"

# CORRECT
dotnet run -- --coverage --coverage-output-format cobertura
```

### ❌ Mistake 6: Using Guid.NewGuid() instead of Guid.CreateVersion7()

```csharp
// WRONG - Random GUIDs cause database index fragmentation
var order = new Order { Id = Guid.NewGuid() };

// CORRECT - UUIDv7 is time-ordered, database-friendly
var order = new Order { Id = Guid.CreateVersion7() };
```

---

## Quick Reference

### Running Tests
```bash
# All tests
dotnet test

# All tests (preferred with TUnit)
dotnet run

# Specific class
dotnet run -- --treenode-filter "/*/*/MyTestClass/*"

# With coverage
dotnet run -- --coverage --coverage-output-format cobertura

# Integration tests only
dotnet run -- --treenode-filter "/*/*/*/*[Category=Integration]"
```

### Test Template
```csharp
[Test]
public async Task Method_Scenario_ExpectedOutcomeAsync() {
    // Arrange
    var sut = new SystemUnderTest();

    // Act
    var result = await sut.MethodAsync();

    // Assert
    await Assert.That(result).IsNotNull();
}
```

### Mock Template (Rocks)
```csharp
var expectations = Rock.Create<IMyInterface>();
expectations.Methods().MyMethodAsync(Arg.Any<T>())
    .Returns(Task.FromResult(expectedValue));
var mock = expectations.Instance();

// Use mock...

expectations.Verify();
```

### Fake Data Template (Bogus)
```csharp
var faker = new Faker<MyClass>()
    .RuleFor(x => x.Id, f => Guid.CreateVersion7())
    .RuleFor(x => x.Name, f => f.Name.FullName())
    .RuleFor(x => x.Amount, f => f.Finance.Amount(10, 1000));

var instance = faker.Generate();
var collection = faker.Generate(50);
```

---

## See Also

- [TUnit Official Documentation](https://tunit.dev/)
- [Rocks GitHub Repository](https://github.com/JasonBock/Rocks)
- [Bogus GitHub Repository](https://github.com/bchavez/Bogus)
- [Microsoft.Testing.Platform Documentation](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-platform-intro)
