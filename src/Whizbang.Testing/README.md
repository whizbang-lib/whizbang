# Whizbang.Testing

Testing utilities and helpers for Whizbang applications.

## What's In This Package

### Test Fixtures
- **`WhizbangTestFixture`** - Setup helper for tests (planned)
- **In-memory implementations** - Fast test doubles

### Fluent Assertions
- Extensions for TUnit.Assertions
- Event verification helpers
- Context assertions

### Test Data Generation
- Bogus integration for realistic test data
- Scenario builders for common patterns

## Usage (Planned for v0.1.0)

```csharp
using Whizbang.Testing;
using TUnit.Core;

[TestClass]
public class OrderTests {
    private readonly WhizbangTestFixture _fixture;

    public OrderTests() {
        _fixture = new WhizbangTestFixture()
            .UseInMemoryEventStore()
            .UseInMemoryProjections();
    }

    [Test]
    public async Task CreateOrder_ShouldEmitOrderCreated() {
        // Arrange
        var dispatcher = _fixture.GetDispatcher();
        var command = new CreateOrder(Guid.NewGuid(), new[] { /* items */ });

        // Act
        var result = await dispatcher.Send<OrderCreated>(command);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);
    }
}
```

## Dependencies

- **Whizbang.Core** - Core library
- **TUnit.Core** - Test framework
- **TUnit.Assertions** - Assertions
- **Bogus** - Test data generation

## Version

**0.1.0** - Initial test utilities (in progress)
