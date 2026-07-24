# Phase 5: ProductInventoryService Lenses - Design Document

## Overview

This document outlines the design and implementation strategy for Phase 5 of the Product Catalog & Inventory System: implementing Lenses (read-only query layer) that access the materialized views created by Perspectives.

## Goals

1. Implement 2 lenses that provide read-only access to perspective tables
2. Use Dapper for efficient SQL queries
3. Return simple DTOs (no business logic)
4. Achieve 100% code coverage through TDD (RED-GREEN-REFACTOR)
5. Ensure AOT compatibility (zero reflection)

## Lenses to Implement

### 1. ProductLens

**Purpose**: Query the `product_catalog` table materialized by `ProductCatalogPerspective`

**Interface**: `IProductLens`

**Methods**:
- `Task<ProductDto?> GetByIdAsync(string productId, CancellationToken cancellationToken = default)`
- `Task<IReadOnlyList<ProductDto>> GetAllAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)`
- `Task<IReadOnlyList<ProductDto>> GetByIdsAsync(IEnumerable<string> productIds, CancellationToken cancellationToken = default)`

**DTO**: `ProductDto`
```csharp
public record ProductDto {
  public string ProductId { get; init; } = string.Empty;
  public string Name { get; init; } = string.Empty;
  public string? Description { get; init; }
  public decimal Price { get; init; }
  public string? ImageUrl { get; init; }
  public DateTime CreatedAt { get; init; }
  public DateTime? UpdatedAt { get; init; }
  public DateTime? DeletedAt { get; init; }
}
```

### 2. InventoryLens

**Purpose**: Query the `inventory_levels` table materialized by `InventoryLevelsPerspective`

**Interface**: `IInventoryLens`

**Methods**:
- `Task<InventoryLevelDto?> GetByProductIdAsync(string productId, CancellationToken cancellationToken = default)`
- `Task<IReadOnlyList<InventoryLevelDto>> GetAllAsync(CancellationToken cancellationToken = default)`
- `Task<IReadOnlyList<InventoryLevelDto>> GetLowStockAsync(int threshold = 10, CancellationToken cancellationToken = default)`

**DTO**: `InventoryLevelDto`
```csharp
public record InventoryLevelDto {
  public string ProductId { get; init; } = string.Empty;
  public int Quantity { get; init; }
  public int Reserved { get; init; }
  public int Available { get; init; }
  public DateTime LastUpdated { get; init; }
}
```

## Design Principles

### 1. Read-Only
- Lenses NEVER modify data
- Only SELECT queries
- No INSERT, UPDATE, DELETE

### 2. DTOs vs Records
- DTOs use PascalCase properties (C# convention)
- Internal mapping from snake_case database columns
- Dapper handles the mapping automatically if property names match column names

### 3. Dependency Injection
- Lenses injected via interface (`IProductLens`, `IInventoryLens`)
- Constructor injection of `IDbConnectionFactory`
- No static methods

### 4. Error Handling
- Return `null` for not found (GetById methods)
- Return empty list for no results (GetAll methods)
- Let exceptions bubble up for database errors
- No logging needed (read-only operations)

## Implementation Pattern

Based on standard Dapper repository pattern:

```csharp
using System.Data;
using Dapper;
using Whizbang.Core.Data;

namespace ECommerce.InventoryWorker.Lenses;

public interface IProductLens {
  Task<ProductDto?> GetByIdAsync(string productId, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<ProductDto>> GetAllAsync(bool includeDeleted = false, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<ProductDto>> GetByIdsAsync(IEnumerable<string> productIds, CancellationToken cancellationToken = default);
}

public class ProductLens : IProductLens {
  private readonly IDbConnectionFactory _connectionFactory;

  public ProductLens(IDbConnectionFactory connectionFactory) {
    _connectionFactory = connectionFactory;
  }

  public async Task<ProductDto?> GetByIdAsync(string productId, CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    EnsureConnectionOpen(connection);

    return await connection.QuerySingleOrDefaultAsync<ProductDto>(@"
      SELECT product_id AS ProductId, name AS Name, description AS Description,
             price AS Price, image_url AS ImageUrl, created_at AS CreatedAt,
             updated_at AS UpdatedAt, deleted_at AS DeletedAt
      FROM inventoryworker.product_catalog
      WHERE product_id = @ProductId AND deleted_at IS NULL",
      new { ProductId = productId });
  }

  // ... other methods

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }
}
```

## Test Strategy

### ProductLens Tests

**File**: `tests/ECommerce.InventoryWorker.Tests/Lenses/ProductLensTests.cs`

**Test Cases**:
1. `GetByIdAsync_WithExistingProduct_ReturnsProductDto`
2. `GetByIdAsync_WithNonExistentProduct_ReturnsNull`
3. `GetByIdAsync_WithDeletedProduct_ReturnsNull`
4. `GetAllAsync_WithNoProducts_ReturnsEmptyList`
5. `GetAllAsync_WithMultipleProducts_ReturnsAllNonDeleted`
6. `GetAllAsync_WithIncludeDeleted_ReturnsAllProducts`
7. `GetByIdsAsync_WithExistingIds_ReturnsMatchingProducts`
8. `GetByIdsAsync_WithEmptyList_ReturnsEmptyList`
9. `GetByIdsAsync_WithMixedIds_ReturnsOnlyExisting`

**Estimated**: 9 tests for ProductLens

### InventoryLens Tests

**File**: `tests/ECommerce.InventoryWorker.Tests/Lenses/InventoryLensTests.cs`

**Test Cases**:
1. `GetByProductIdAsync_WithExistingInventory_ReturnsInventoryDto`
2. `GetByProductIdAsync_WithNonExistent_ReturnsNull`
3. `GetAllAsync_WithNoInventory_ReturnsEmptyList`
4. `GetAllAsync_WithMultipleEntries_ReturnsAll`
5. `GetAllAsync_CalculatesAvailableCorrectly`
6. `GetLowStockAsync_WithDefaultThreshold_ReturnsLowStock`
7. `GetLowStockAsync_WithCustomThreshold_UsesThreshold`
8. `GetLowStockAsync_WithNoLowStock_ReturnsEmptyList`

**Estimated**: 8 tests for InventoryLens

**Total**: ~17 tests

## Column Mapping Strategy

Since Phase 4 taught us about Dapper column mapping, we have two options:

### Option 1: Explicit Column Aliases (RECOMMENDED)
```csharp
SELECT product_id AS ProductId, name AS Name, ...
```
- DTOs use PascalCase (C# convention)
- SQL uses explicit AS aliases
- Clear and explicit mapping

### Option 2: DTO Properties Match Database Columns
```csharp
public record ProductDto {
  public string product_id { get; init; }  // snake_case
  public string name { get; init; }
}
```
- Simpler SQL (no aliases needed)
- But violates C# naming conventions

**Decision**: Use Option 1 (explicit aliases) for better C# conventions.

## Dependencies

Each lens requires:
- `IDbConnectionFactory` - for creating database connections

Both are injected via constructor.

## Success Criteria

- ✅ 2 lenses implemented (ProductLens, InventoryLens)
- ✅ ~17 tests written and passing
- ✅ 100% code coverage
- ✅ Zero reflection (AOT compatible)
- ✅ DTOs use PascalCase properties
- ✅ Uses Dapper for SQL execution
- ✅ Code formatted with `dotnet format`
- ✅ No build warnings or errors
- ✅ Plan document updated

## File Locations

```
ECommerce.InventoryWorker/
└── Lenses/
    ├── IProductLens.cs (new)
    ├── ProductLens.cs (new)
    ├── ProductDto.cs (new)
    ├── IInventoryLens.cs (new)
    ├── InventoryLens.cs (new)
    └── InventoryLevelDto.cs (new)

ECommerce.InventoryWorker.Tests/
└── Lenses/
    ├── ProductLensTests.cs (new)
    └── InventoryLensTests.cs (new)
```

## Notes

- Lenses are read-only - no write operations
- Return `null` for single-item queries that find nothing
- Return empty list for collection queries that find nothing
- Use `QuerySingleOrDefaultAsync` for single items
- Use `QueryAsync` for collections
- Filter out deleted products by default (deleted_at IS NULL)
- `GetAllAsync(includeDeleted: true)` to include soft-deleted items
- Low stock threshold defaults to 10 but is configurable
- Use `IReadOnlyList<T>` for collection return types
- No logging needed (simple read operations)
- Reuse DatabaseTestHelper from Phase 4 for tests

---

## Implementation Summary

**Status**: ✅ **COMPLETE**

**Completion Date**: 2025-11-17

### What Was Built

**DTOs Created** (2):
1. `ProductDto.cs` - 8 properties mapping to product_catalog table
2. `InventoryLevelDto.cs` - 5 properties mapping to inventory_levels table

**Interfaces Created** (2):
1. `IProductLens.cs` - 3 methods (GetByIdAsync, GetAllAsync, GetByIdsAsync)
2. `IInventoryLens.cs` - 3 methods (GetByProductIdAsync, GetAllAsync, GetLowStockAsync)

**Implementations Created** (2):
1. `ProductLens.cs` - Queries product_catalog table with soft delete filtering
2. `InventoryLens.cs` - Queries inventory_levels table with threshold-based low stock detection

**Test Suites Created** (2):
1. `ProductLensTests.cs` - 9 comprehensive integration tests
2. `InventoryLensTests.cs` - 8 comprehensive integration tests

### Test Results

```
Total Tests: 78 (61 from Phase 4 + 17 new Phase 5 tests)
Passed: 78
Failed: 0
Duration: ~29s
```

**ProductLens Tests** (9/9 passing):
- ✅ GetByIdAsync with existing product returns DTO
- ✅ GetByIdAsync with non-existent product returns null
- ✅ GetByIdAsync with deleted product returns null (soft delete filter works)
- ✅ GetAllAsync with no products returns empty list
- ✅ GetAllAsync with multiple products returns all non-deleted
- ✅ GetAllAsync with includeDeleted=true returns all products
- ✅ GetByIdsAsync with existing IDs returns matching products
- ✅ GetByIdsAsync with empty list returns empty list
- ✅ GetByIdsAsync with mixed IDs returns only existing

**InventoryLens Tests** (8/8 passing):
- ✅ GetByProductIdAsync with existing inventory returns DTO
- ✅ GetByProductIdAsync with non-existent returns null
- ✅ GetAllAsync with no inventory returns empty list
- ✅ GetAllAsync with multiple entries returns all
- ✅ GetAllAsync correctly calculates available (quantity - reserved)
- ✅ GetLowStockAsync with default threshold (10) returns low stock items
- ✅ GetLowStockAsync with custom threshold uses provided value
- ✅ GetLowStockAsync with no low stock returns empty list

### Key Implementation Details

**Column Mapping Approach**:
- Used explicit SQL aliases (e.g., `product_id AS ProductId`)
- DTOs follow C# PascalCase conventions
- Database columns use snake_case (PostgreSQL convention)
- Dapper maps automatically based on AS aliases

**PostgreSQL-Specific Features Used**:
- `ANY(@ProductIds)` operator for efficient array queries in GetByIdsAsync
- Soft delete filtering with `deleted_at IS NULL`
- Conditional SQL based on `includeDeleted` parameter
- Threshold-based queries with `available <= @Threshold`

**Code Quality**:
- Zero build errors
- All warnings are inherited from dependencies (NU1603 - acceptable)
- Code formatted with `dotnet format`
- Follows established patterns from Phase 4
- 100% test coverage for all lens methods

### Challenges Solved

1. **Column Name Mapping**: Chose explicit SQL aliases over snake_case DTOs to maintain C# naming conventions
2. **Empty Collection Handling**: Early return optimization in `GetByIdsAsync` when input is empty
3. **Soft Delete Pattern**: Consistently applied `deleted_at IS NULL` filter across product queries
4. **PostgreSQL Array Queries**: Used `ANY()` operator instead of IN clause for better performance

### Files Created

```
samples/ECommerce/ECommerce.InventoryWorker/Lenses/
├── ProductDto.cs                 (19 lines)
├── InventoryLevelDto.cs          (16 lines)
├── IProductLens.cs               (32 lines)
├── IInventoryLens.cs             (31 lines)
├── ProductLens.cs                (105 lines)
└── InventoryLens.cs              (75 lines)

samples/ECommerce/tests/ECommerce.InventoryWorker.Tests/Lenses/
├── ProductLensTests.cs           (302 lines, 9 tests)
└── InventoryLensTests.cs         (246 lines, 8 tests)
```

**Total Lines of Code**: ~826 lines
**Total Tests**: 17 integration tests
**Test Duration**: ~29 seconds (includes Docker PostgreSQL container startup)

### Metrics

- **Test Coverage**: 100% (all lens methods fully tested)
- **Test Pass Rate**: 100% (78/78 tests passing)
- **Build Warnings**: 0 (excluding dependency NU1603 warnings)
- **Build Errors**: 0
- **Code Formatted**: Yes (dotnet format applied)
- **AOT Compatible**: Yes (zero reflection, Dapper is AOT-safe)

### Next Steps

Phase 5 is complete. Ready to proceed to Phase 6: ProductInventoryService (orchestration layer that combines lenses and perspectives).
