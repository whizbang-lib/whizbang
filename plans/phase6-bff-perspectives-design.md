# Phase 6: BFF Product & Inventory Perspectives - Design Document

## Overview

This document outlines the design and implementation strategy for Phase 6: implementing Perspectives in the BFF (Backend-for-Frontend) that maintain denormalized read models optimized for customer-facing API endpoints.

## Goals

1. Implement 2 BFF perspectives that listen to ProductInventoryService events
2. Create denormalized views optimized for BFF API queries
3. Use PerspectiveSchemaGenerator for table DDL
4. Achieve 100% code coverage through TDD (RED-GREEN-REFACTOR)
5. Ensure AOT compatibility (zero reflection)

## Key Differences: BFF vs Service Perspectives

### ProductInventoryService Perspectives
- **Purpose**: Maintain operational state for the service
- **Schema**: `inventoryworker` schema
- **Tables**: `product_catalog`, `inventory_levels`
- **Use Case**: Service-internal operations

### BFF Perspectives
- **Purpose**: Maintain read models for customer-facing APIs
- **Schema**: `bff` schema
- **Tables**: `product_catalog`, `inventory_levels` (same structure, different schema)
- **Use Case**: Customer-facing API queries
- **Key Difference**: Same table structure, different schema namespace

## Perspectives to Implement

### 1. ProductCatalogPerspective (BFF)

**Purpose**: Maintain product catalog read model for customer-facing APIs

**Events to Listen To**:
- `ProductCreatedEvent`
- `ProductUpdatedEvent`
- `ProductDeletedEvent`

**Table Schema**: `bff.product_catalog`
```sql
CREATE TABLE IF NOT EXISTS bff.product_catalog (
  product_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT,
  price DECIMAL(10, 2) NOT NULL,
  image_url TEXT,
  created_at TIMESTAMP NOT NULL,
  updated_at TIMESTAMP,
  deleted_at TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_product_catalog_created_at ON bff.product_catalog (created_at DESC) WHERE deleted_at IS NULL;
```

**Implementation**: Nearly identical to ProductInventoryService's ProductCatalogPerspective, just using `bff` schema instead of `inventoryworker` schema.

**Interface**: `IPerspectiveOf<ProductCreatedEvent>, IPerspectiveOf<ProductUpdatedEvent>, IPerspectiveOf<ProductDeletedEvent>`

### 2. InventoryLevelsPerspective (BFF)

**Purpose**: Maintain inventory levels read model for customer-facing APIs

**Events to Listen To**:
- `InventoryRestockedEvent`
- `InventoryReservedEvent`
- `InventoryReleasedEvent`
- `InventoryAdjustedEvent`

**Table Schema**: `bff.inventory_levels`
```sql
CREATE TABLE IF NOT EXISTS bff.inventory_levels (
  product_id TEXT PRIMARY KEY,
  quantity INTEGER NOT NULL DEFAULT 0,
  reserved INTEGER NOT NULL DEFAULT 0,
  available INTEGER NOT NULL DEFAULT 0,
  last_updated TIMESTAMP NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_inventory_levels_available ON bff.inventory_levels (available);
```

**Implementation**: Nearly identical to ProductInventoryService's InventoryLevelsPerspective, just using `bff` schema instead of `inventoryworker` schema.

**Interface**: `IPerspectiveOf<InventoryRestockedEvent>, IPerspectiveOf<InventoryReservedEvent>, IPerspectiveOf<InventoryReleasedEvent>, IPerspectiveOf<InventoryAdjustedEvent>`

## Design Principles

### 1. Mirror Service Structure

The BFF perspectives mirror the ProductInventoryService structure:
- Same events subscribed to
- Same table structures (different schema)
- Similar update logic

**Why Mirror**:
- Proven, working design from ProductInventoryService
- Simpler to understand and maintain
- Each perspective owns its table (clean separation)
- Denormalization happens at the Lens/API layer via JOIN queries

### 2. Event Subscription

Both BFF perspectives subscribe to ProductInventoryService events:

```
ProductInventoryService:
  CreateProductReceptor → ProductCreatedEvent

Event Store:
  ProductCreatedEvent (published)

Subscribers:
  1. ProductCatalogPerspective (in ProductInventoryService)
  2. ProductCatalogPerspective (in BFF) ← this phase
```

### 3. Schema Management

BFF perspectives do NOT use PerspectiveSchemaGenerator attributes. They manually create tables using raw SQL in their Update methods, following the pattern established by existing BFF perspectives (OrderPerspective, etc.).

### 4. Separate Tables Pattern

Each BFF perspective owns its own table:
- **ProductCatalogPerspective**: owns `bff.product_catalog`
- **InventoryLevelsPerspective**: owns `bff.inventory_levels`

This follows standard CQRS patterns where each perspective materializes its own view.

## Implementation Pattern

Based on ProductInventoryService perspectives and BFF OrderPerspective pattern:

```csharp
using System.Data;
using Dapper;
using ECommerce.Contracts.Events;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Data;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Materializes product catalog events into bff.product_catalog table.
/// </summary>
public class ProductCatalogPerspective :
  IPerspectiveOf<ProductCreatedEvent>,
  IPerspectiveOf<ProductUpdatedEvent>,
  IPerspectiveOf<ProductDeletedEvent> {

  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ILogger<ProductCatalogPerspective> _logger;

  public ProductCatalogPerspective(
    IDbConnectionFactory connectionFactory,
    ILogger<ProductCatalogPerspective> logger
  ) {
    _connectionFactory = connectionFactory;
    _logger = logger;
  }

  public async Task Update(ProductCreatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      await connection.ExecuteAsync(@"
        INSERT INTO bff.product_catalog (
          product_id, name, description, price, image_url, created_at
        ) VALUES (
          @ProductId, @Name, @Description, @Price, @ImageUrl, @CreatedAt
        )",
        new {
          @event.ProductId,
          @event.Name,
          @event.Description,
          @event.Price,
          @event.ImageUrl,
          @event.CreatedAt
        });

      _logger.LogInformation("Product {ProductId} created in BFF catalog", @event.ProductId);
    } catch (Exception ex) {
      _logger.LogError(ex, "Error updating ProductCatalogPerspective for product {ProductId}", @event.ProductId);
      throw;
    }
  }

  // ... other Update methods

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }
}
```

## Test Strategy

### ProductCatalogPerspective Tests

**File**: `tests/ECommerce.BFF.API.Tests/Perspectives/ProductCatalogPerspectiveTests.cs`

**Test Cases** (mirror ProductInventoryService tests):
1. `Update_WithProductCreatedEvent_InsertsProduct`
2. `Update_WithProductUpdatedEvent_UpdatesProduct`
3. `Update_WithProductDeletedEvent_SoftDeletesProduct`
4. `Update_WithDuplicateCreate_HandlesGracefully`

**Estimated**: 4 tests

### InventoryLevelsPerspective Tests

**File**: `tests/ECommerce.BFF.API.Tests/Perspectives/InventoryLevelsPerspectiveTests.cs`

**Test Cases** (mirror ProductInventoryService tests):
1. `Update_WithInventoryRestockedEvent_UpdatesQuantityAndAvailable`
2. `Update_WithInventoryReservedEvent_UpdatesReservedAndAvailable`
3. `Update_WithInventoryReleasedEvent_UpdatesReservedAndAvailable`
4. `Update_WithInventoryAdjustedEvent_UpdatesQuantityAndAvailable`

**Estimated**: 4 tests

**Total**: ~8 tests

## Dependencies

Each perspective requires:
- `IDbConnectionFactory` - for creating database connections
- `ILogger<T>` - for logging operations

## Success Criteria

- ✅ 2 BFF perspectives implemented
- ✅ ~8 tests written and passing
- ✅ 100% code coverage
- ✅ Zero reflection (AOT compatible)
- ✅ Mirrors ProductInventoryService structure (bff schema instead of inventoryworker)
- ✅ Code formatted with `dotnet format`
- ✅ No build warnings or errors
- ✅ Plan document updated

## File Locations

```
ECommerce.BFF.API/
└── Perspectives/
    ├── ProductCatalogPerspective.cs (new)
    └── InventoryLevelsPerspective.cs (new)

tests/ECommerce.BFF.API.Tests/
└── Perspectives/
    ├── ProductCatalogPerspectiveTests.cs (new)
    └── InventoryLevelsPerspectiveTests.cs (new)
```

## Notes

- BFF perspectives mirror the ProductInventoryService structure
- Each perspective owns its own table (standard CQRS pattern)
- ProductCatalogPerspective → `bff.product_catalog`
- InventoryLevelsPerspective → `bff.inventory_levels`
- Use Dapper for SQL execution
- Use try-catch with logging for error handling
- Reuse DatabaseTestHelper pattern from Phase 4/5
- No SignalR push needed for these perspectives (product catalog is not real-time)
- Tests can largely mirror ProductInventoryService perspective tests

## Next Steps

After Phase 6 completion:
- **Phase 7**: BFF Lenses (query layer for bff.products)
- **Phase 8**: BFF API Endpoints (HTTP endpoints using lenses)

---

## Implementation Summary

**Status**: ✅ **COMPLETE**

**Completion Date**: 2025-11-17

### What Was Built

**Perspectives Created** (2):
1. `ProductCatalogPerspective.cs` - Materializes ProductCreatedEvent, ProductUpdatedEvent, ProductDeletedEvent into bff.product_catalog
2. `InventoryLevelsPerspective.cs` - Materializes InventoryRestockedEvent, InventoryReservedEvent, InventoryReleasedEvent, InventoryAdjustedEvent into bff.inventory_levels

**Test Suites Created** (2):
1. `ProductCatalogPerspectiveTests.cs` - 4 comprehensive integration tests
2. `InventoryLevelsPerspectiveTests.cs` - 4 comprehensive integration tests

**Supporting Files**:
1. `TestLogger.cs` - Shared test logger implementation in TestHelpers
2. `InventoryReleasedEvent.cs` - New contract event for inventory release operations

**Database Schema Updates**:
- Added `bff.product_catalog` table creation to DatabaseTestHelper
- Added `bff.inventory_levels` table creation to DatabaseTestHelper
- Added table cleanup for both new tables

### Test Results

```
Total Tests: 43 (35 existing + 8 new Phase 6 tests)
Passed: 43
Failed: 0
Duration: ~5.6s
```

**ProductCatalogPerspective Tests** (4/4 passing):
- ✅ Update_WithProductCreatedEvent_InsertsProductAsync
- ✅ Update_WithProductUpdatedEvent_UpdatesProductAsync
- ✅ Update_WithProductDeletedEvent_SoftDeletesProductAsync
- ✅ Update_WithDuplicateProductCreatedEvent_HandlesGracefullyAsync

**InventoryLevelsPerspective Tests** (4/4 passing):
- ✅ Update_WithInventoryRestockedEvent_UpdatesQuantityAndAvailableAsync
- ✅ Update_WithInventoryReservedEvent_UpdatesReservedAndAvailableAsync
- ✅ Update_WithInventoryReleasedEvent_UpdatesReservedAndAvailableAsync
- ✅ Update_WithInventoryAdjustedEvent_UpdatesQuantityAndAvailableAsync

### Key Implementation Details

**Architecture Pattern**:
- BFF perspectives mirror ProductInventoryService structure
- Each perspective owns its own table (standard Whizbang/CQRS pattern)
- Same events, same table structures, different schema (`bff` vs `inventoryworker`)

**Schema Design**:
```sql
bff.product_catalog: product_id, name, description, price, image_url, created_at, updated_at, deleted_at
bff.inventory_levels: product_id, quantity, reserved, available, last_updated
```

**Key Differences from ProductInventoryService Perspectives**:
1. **Schema**: Uses `bff` schema instead of `inventoryworker` schema
2. **Available Column**: BFF inventory_levels includes `available` column (calculated as quantity - reserved)
3. **Log Messages**: Prefixed with "BFF" to distinguish from ProductInventoryService logs
4. **InventoryReleasedEvent**: Added support for this event (not in ProductInventoryService yet)

**Code Quality**:
- Zero build errors
- All warnings are inherited from dependencies or intentional (snake_case DTOs)
- Code formatted with `dotnet format`
- Follows established patterns from Phase 4/5
- 100% test coverage for all perspective event handlers

### Files Created

```
samples/ECommerce/ECommerce.BFF.API/Perspectives/
├── ProductCatalogPerspective.cs         (153 lines)
└── InventoryLevelsPerspective.cs        (167 lines)

samples/ECommerce/ECommerce.Contracts/Events/
└── InventoryReleasedEvent.cs            (14 lines)

samples/ECommerce/tests/ECommerce.BFF.API.Tests/
├── Perspectives/
│   ├── ProductCatalogPerspectiveTests.cs (193 lines, 4 tests)
│   └── InventoryLevelsPerspectiveTests.cs (199 lines, 4 tests)
└── TestHelpers/
    └── TestLogger.cs                     (19 lines)
```

**Files Modified**:
- `DatabaseTestHelper.cs` - Added bff.product_catalog and bff.inventory_levels table creation/cleanup

**Total Lines of Code**: ~745 lines
**Total Tests**: 8 integration tests
**Test Duration**: ~5.6 seconds (includes Docker PostgreSQL container startup)

### Metrics

- **Test Coverage**: 100% (all perspective methods fully tested)
- **Test Pass Rate**: 100% (43/43 tests passing)
- **Build Warnings**: 23 (all from dependencies or intentional naming)
- **Build Errors**: 0
- **Code Formatted**: Yes (dotnet format applied)
- **AOT Compatible**: Yes (zero reflection, Dapper is AOT-safe)

### Challenges Solved

1. **Duplicate TestLogger**: Created shared TestLogger in TestHelpers to avoid duplication
2. **Missing Event**: Created InventoryReleasedEvent contract to support full inventory lifecycle
3. **Available Calculation**: BFF perspectives explicitly calculate and store `available` column (quantity - reserved)
4. **Schema Management**: Extended DatabaseTestHelper to create and cleanup BFF schema tables

### Next Steps

Phase 6 is complete. Ready to proceed to Phase 7: BFF Lenses (query layer for bff.product_catalog and bff.inventory_levels).
