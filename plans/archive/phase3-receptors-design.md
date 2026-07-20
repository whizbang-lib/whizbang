# Phase 3: ProductInventoryService Receptors - Design Document

## Overview

This document outlines the design and implementation strategy for Phase 3 of the Product Catalog & Inventory System: implementing receptors for product catalog and inventory management commands.

## Goals

1. Implement 5 new receptors following the existing `ReserveInventoryReceptor` pattern
2. Achieve 100% code coverage through TDD (RED-GREEN-REFACTOR)
3. Ensure AOT compatibility (zero reflection)
4. Follow Whizbang receptor patterns and conventions
5. Integrate with IDispatcher for event publishing

## Receptors to Implement

### 1. CreateProductReceptor
- **Command**: `CreateProductCommand`
- **Events Published**:
  - `ProductCreatedEvent` - always
  - `InventoryRestockedEvent` - if `InitialStock > 0`
- **Business Logic**: Creates a new product and optionally initializes inventory
- **File**: `ECommerce.InventoryWorker/Receptors/CreateProductReceptor.cs`

### 2. UpdateProductReceptor
- **Command**: `UpdateProductCommand`
- **Events Published**: `ProductUpdatedEvent`
- **Business Logic**: Updates product details (partial update pattern with nullables)
- **File**: `ECommerce.InventoryWorker/Receptors/UpdateProductReceptor.cs`

### 3. DeleteProductReceptor
- **Command**: `DeleteProductCommand`
- **Events Published**: `ProductDeletedEvent`
- **Business Logic**: Soft-deletes a product from catalog
- **File**: `ECommerce.InventoryWorker/Receptors/DeleteProductReceptor.cs`

### 4. RestockInventoryReceptor
- **Command**: `RestockInventoryCommand`
- **Events Published**: `InventoryRestockedEvent`
- **Business Logic**: Adds inventory quantity (restocking operation)
- **File**: `ECommerce.InventoryWorker/Receptors/RestockInventoryReceptor.cs`

### 5. AdjustInventoryReceptor
- **Command**: `AdjustInventoryCommand`
- **Events Published**: `InventoryAdjustedEvent`
- **Business Logic**: Manually adjusts inventory (corrections, damages, etc.)
- **File**: `ECommerce.InventoryWorker/Receptors/AdjustInventoryReceptor.cs`

## Receptor Pattern

Based on the existing `ReserveInventoryReceptor`, all receptors will follow this pattern:

```csharp
using Whizbang.Core;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;

namespace ECommerce.InventoryWorker.Receptors;

/// <summary>
/// Handles [CommandName] and publishes [EventName]
/// </summary>
public class [Name]Receptor : IReceptor<[CommandType], [EventType]> {
  private readonly IDispatcher _dispatcher;
  private readonly ILogger<[Name]Receptor> _logger;

  public [Name]Receptor(IDispatcher dispatcher, ILogger<[Name]Receptor> logger) {
    _dispatcher = dispatcher;
    _logger = logger;
  }

  public async ValueTask<[EventType]> HandleAsync(
    [CommandType] message,
    CancellationToken cancellationToken = default) {

    _logger.LogInformation("...", ...);

    // Business logic here
    // For Phase 3, this will be simple: just map command to event

    var @event = new [EventType] {
      // Map properties from command
    };

    await _dispatcher.PublishAsync(@event);

    _logger.LogInformation("...", ...);

    return @event;
  }
}
```

## Special Case: CreateProductReceptor

The `CreateProductReceptor` is unique because it publishes TWO events when `InitialStock > 0`:

1. `ProductCreatedEvent` - always published
2. `InventoryRestockedEvent` - conditionally published if `InitialStock > 0`

This requires special handling in the receptor and test design.

## Test Strategy

### Test Project
- **Location**: `ECommerce.InventoryWorker.Tests/Receptors/`
- **Test Files**:
  - `CreateProductReceptorTests.cs`
  - `UpdateProductReceptorTests.cs`
  - `DeleteProductReceptorTests.cs`
  - `RestockInventoryReceptorTests.cs`
  - `AdjustInventoryReceptorTests.cs`

### Test Categories

Each receptor test file will have these test categories:

#### 1. Command Processing Tests
- Verify command is processed successfully
- Verify event is created with correct data mapping
- Verify event is published via IDispatcher

#### 2. Event Publishing Tests
- Verify IDispatcher.PublishAsync is called
- Verify correct event type is published
- Verify event data matches command data

#### 3. Logging Tests
- Verify informational logs are written
- Verify log messages contain relevant data

#### 4. Return Value Tests
- Verify HandleAsync returns the published event
- Verify returned event matches published event

#### 5. Cancellation Tests
- Verify cancellation token is passed to async operations

#### 6. Special Cases (where applicable)
- CreateProductReceptor: Test both with and without initial stock
- UpdateProductReceptor: Test partial updates with nullable properties
- AdjustInventoryReceptor: Test both positive and negative adjustments

### Mocking Strategy

We'll use **Rocks** for mocking:
- Mock `IDispatcher` to verify event publishing
- Mock `ILogger<T>` to verify logging behavior
- Use `Rock.Create<T>()` for creating mock instances
- Use `.SetupAsync()` for async method mocking
- Use `.Verify()` to assert mock interactions

### Test Example Structure

```csharp
[Test]
public async Task HandleAsync_WithValidCommand_PublishesEventAsync() {
  // Arrange
  var dispatcherMock = Rock.Create<IDispatcher>();
  dispatcherMock.SetupAsync(x => x.PublishAsync(Arg.Any<ProductCreatedEvent>()));

  var loggerMock = Rock.Create<ILogger<CreateProductReceptor>>();

  var receptor = new CreateProductReceptor(
    dispatcherMock.Instance(),
    loggerMock.Instance()
  );

  var command = new CreateProductCommand {
    ProductId = "prod-123",
    Name = "Widget",
    Description = "A widget",
    Price = 29.99m,
    ImageUrl = null,
    InitialStock = 10
  };

  // Act
  var result = await receptor.HandleAsync(command);

  // Assert
  await Assert.That(result).IsNotNull();
  await Assert.That(result.ProductId).IsEqualTo("prod-123");
  dispatcherMock.Verify();
}
```

## Implementation Strategy (TDD)

### Phase 1: RED - Write Failing Tests

For each receptor:
1. Create test file with ~8-10 tests
2. Tests will fail because receptor doesn't exist yet
3. Expected failures: type not found, compilation errors

### Phase 2: GREEN - Implement Receptors

For each receptor:
1. Create receptor class implementing `IReceptor<TCommand, TEvent>`
2. Add constructor injection for `IDispatcher` and `ILogger`
3. Implement `HandleAsync` method:
   - Log command received
   - Create event from command data
   - Publish event via `_dispatcher.PublishAsync()`
   - Log success
   - Return event
4. Run tests - should pass

### Phase 3: REFACTOR - Quality & Formatting

1. Review code for quality
2. Apply `dotnet format`
3. Verify no regressions
4. Update plan document

## Business Logic (Simplified for Phase 3)

For this phase, we're implementing the simplest possible business logic:

1. **No validation**: Assume commands are valid (validation will come in later phases)
2. **No database**: No actual inventory tracking (perspectives will come in later phases)
3. **Simple mapping**: Direct property mapping from command to event
4. **UTC timestamps**: Use `DateTime.UtcNow` for all timestamp fields
5. **Event publishing**: Always publish events via IDispatcher

Real business logic (inventory checks, validation, etc.) will be added in future phases.

## Dependencies

Each receptor requires:
- `IDispatcher` - for publishing events
- `ILogger<T>` - for logging

Both are injected via constructor.

## Success Criteria

- ✅ All 5 receptors implemented
- ✅ ~40-50 tests written and passing
- ✅ 100% code coverage
- ✅ Zero reflection (AOT compatible)
- ✅ Follows existing receptor pattern
- ✅ All tests use Rocks for mocking
- ✅ Code formatted with `dotnet format`
- ✅ No build warnings or errors
- ✅ Plan document updated

## File Locations

```
ECommerce.InventoryWorker/
└── Receptors/
    ├── ReserveInventoryReceptor.cs (existing)
    ├── CreateProductReceptor.cs (new)
    ├── UpdateProductReceptor.cs (new)
    ├── DeleteProductReceptor.cs (new)
    ├── RestockInventoryReceptor.cs (new)
    └── AdjustInventoryReceptor.cs (new)

ECommerce.InventoryWorker.Tests/
└── Receptors/
    ├── CreateProductReceptorTests.cs (new)
    ├── UpdateProductReceptorTests.cs (new)
    ├── DeleteProductReceptorTests.cs (new)
    ├── RestockInventoryReceptorTests.cs (new)
    └── AdjustInventoryReceptorTests.cs (new)
```

## Next Steps

1. Create `ECommerce.InventoryWorker.Tests` project if it doesn't exist
2. Write failing tests for all 5 receptors (RED)
3. Implement all 5 receptors (GREEN)
4. Apply formatting and verify quality (REFACTOR)
5. Update main plan document with completion status
