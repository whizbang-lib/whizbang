# Whizbang.Core.Tests

Unit tests for Whizbang.Core v0.1.0 functionality.

## Test Organization

### Receptors/
Tests for receptor functionality:
- Type-safe interfaces
- Async operations
- Multi-destination routing
- Stateless behavior
- Flexible response types
- Validation and error handling

### Dispatcher/
Tests for dispatcher functionality:
- Message routing
- Context tracking (correlation/causation IDs)
- Handler discovery
- Error handling (HandlerNotFoundException)
- Batch operations

## Current Status

All tests are currently **FAILING** by design. This is test-driven development:

1. **Red Phase** (Current) - Tests define behavior but fail
2. **Green Phase** (Next) - Implement to make tests pass
3. **Refactor Phase** (Then) - Optimize and improve

## Running Tests

```bash
# Run all tests
dotnet test

# Run specific test class
dotnet test --filter "FullyQualifiedName~ReceptorTests"

# Run with detailed output
dotnet test --verbosity detailed
```

## Test Framework

- **TUnit** - Modern source-generation test framework
- **TUnit.Assertions** - Native assertions with fluent API
- **Rocks** - Compile-time safe mocking
- **Bogus** - Test data generation

## Test Naming Convention

```
MethodName_Scenario_ExpectedBehavior
```

Examples:
- `Receive_ValidCommand_ShouldReturnTypeSafeResponse`
- `Send_WithUnknownMessageType_ShouldThrowHandlerNotFoundException`

## Writing Tests

Follow the Given/When/Then pattern:

```csharp
[Test]
public async Task Receive_ValidCommand_ShouldReturnResult() {
    // Arrange (Given)
    var receptor = new OrderReceptor();
    var command = new CreateOrder(Guid.NewGuid(), items);

    // Act (When)
    var result = await receptor.Receive(command);

    // Assert (Then)
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<OrderCreated>();
}
```

## Version

**0.1.0** - Foundation tests for stateless receptors and basic dispatcher
