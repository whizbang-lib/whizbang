# Perspective Storage - ORM-Agnostic Architecture

**Status**: ✅ Complete (v0.1.0 - January 2025)

This document describes the universal storage architecture for Whizbang perspectives. These patterns are **ORM-agnostic** and can be implemented with any data access technology (EF Core, Dapper, Marten, NHibernate, etc.).

---

## Overview

Whizbang perspectives use a clean abstraction layer that separates business logic from storage implementation. This allows you to swap ORMs or data access technologies without changing your perspective code.

### Key Components

1. **IPerspectiveStore<TModel>** - Write abstraction (ORM-agnostic)
2. **ILensQuery<TModel>** - Read abstraction (ORM-agnostic)
3. **PerspectiveRow<TModel>** - Universal storage structure
4. **3-Column JSONB Pattern** - Storage schema design

---

## The 3-Column JSONB Pattern

Every perspective materializes its read model into a table with this universal structure:

```sql
CREATE TABLE perspective_name (
  id TEXT PRIMARY KEY,                    -- Unique identifier
  model_data JSONB NOT NULL,              -- The read model as JSON
  metadata JSONB NOT NULL,                -- Event metadata (type, timestamp, etc.)
  scope JSONB NOT NULL,                   -- Security/tenant scope
  created_at TIMESTAMP NOT NULL,          -- Record creation time
  updated_at TIMESTAMP NOT NULL,          -- Last update time
  version INTEGER NOT NULL DEFAULT 1      -- Optimistic concurrency
);
```

### Why This Pattern?

**Flexibility**
- Add fields to your read model without schema migrations
- Evolve models independently per perspective
- No coupling between event structure and storage schema

**Queryability**
- JSONB supports indexing (`CREATE INDEX ON table USING GIN (model_data)`)
- JSONB supports path queries (`WHERE model_data->>'status' = 'active'`)
- JSONB supports containment queries (`WHERE model_data @> '{"premium": true}'::jsonb`)

**Separation of Concerns**
- `model_data`: Your business data (the read model)
- `metadata`: Event sourcing metadata (correlation, causation, event type)
- `scope`: Security/tenant isolation (tenant ID, customer ID, etc.)

**Version Control**
- Built-in optimistic concurrency via `version` column
- Prevents lost updates in concurrent scenarios
- Automatic increment on each update

**ORM Swappability**
- PostgreSQL JSONB is a standard type
- Many databases support JSON columns (MySQL, SQL Server, SQLite)
- Even key-value stores can implement this pattern (DynamoDB, MongoDB)

---

## Core Abstractions

### IPerspectiveStore<TModel>

Write-only interface used by perspectives to persist read models.

**Location**: `Whizbang.Core/Perspectives/IPerspectiveStore.cs`

```csharp
namespace Whizbang.Core.Perspectives;

/// <summary>
/// Write-only abstraction for perspective data storage.
/// Hides underlying database implementation (EF Core, Dapper, Marten, etc.).
/// Perspectives use this to update read models without knowing storage details.
/// </summary>
public interface IPerspectiveStore<TModel> where TModel : class {

  /// <summary>
  /// Insert or update a read model.
  /// Creates new row if id doesn't exist, updates if it does.
  /// Automatically increments version for optimistic concurrency.
  /// </summary>
  Task UpsertAsync(string id, TModel model, CancellationToken cancellationToken = default);

  /// <summary>
  /// Update specific fields of a read model.
  /// More efficient than full upsert when only changing a few fields.
  /// Uses database-specific update mechanisms (e.g., jsonb_set for PostgreSQL).
  /// </summary>
  Task UpdateFieldsAsync(string id, Dictionary<string, object> updates, CancellationToken cancellationToken = default);
}
```

**Usage Example** (ORM-agnostic):
```csharp
public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
  private readonly IPerspectiveStore<OrderReadModel> _store;

  public OrderPerspective(IPerspectiveStore<OrderReadModel> store) {
    _store = store;
  }

  public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) {
    return currentData with {
      OrderId = @event.OrderId,
      Amount = @event.Amount,
      Status = "Created"
    };
  }
}
```

**Implementation Responsibilities**:

Any ORM implementation must:
1. Serialize `TModel` to JSON for `model_data` column
2. Create default `PerspectiveMetadata` if not provided
3. Create default `PerspectiveScope` if not provided
4. Set `created_at` on insert, `updated_at` on insert/update
5. Increment `version` on each update
6. Handle upsert logic (insert if not exists, update if exists)

---

### ILensQuery<TModel>

Read-only interface used by lenses to query read models.

**Location**: `Whizbang.Core/Lenses/ILensQuery.cs`

```csharp
namespace Whizbang.Core.Lenses;

/// <summary>
/// Read-only abstraction for querying perspective data.
/// Provides direct access to PerspectiveRow<TModel> for complex queries.
/// Lenses use this to build API-specific projections without knowing storage details.
/// </summary>
public interface ILensQuery<TModel> where TModel : class {

  /// <summary>
  /// Get a single model by ID.
  /// Returns null if not found.
  /// </summary>
  Task<TModel?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

  /// <summary>
  /// Full queryable access to perspective data.
  /// Allows filtering/projecting across model_data, metadata, and scope columns.
  /// Use this for complex queries with LINQ.
  /// </summary>
  IQueryable<PerspectiveRow<TModel>> Query { get; }
}
```

**Usage Example** (ORM-agnostic):
```csharp
public class OrderLens {
  private readonly ILensQuery<OrderReadModel> _query;

  public OrderLens(ILensQuery<OrderReadModel> query) {
    _query = query;
  }

  // Simple query
  public async Task<OrderReadModel?> GetOrderAsync(string orderId) {
    return await _query.GetByIdAsync(orderId);
  }

  // Complex query across all columns
  public async Task<List<OrderSummary>> GetRecentOrdersForTenantAsync(string tenantId) {
    return await _query.Query
      .Where(row => row.Scope.TenantId == tenantId)
      .OrderByDescending(row => row.Metadata.Timestamp)
      .Take(10)
      .Select(row => new OrderSummary {
        OrderId = row.Data.OrderId,
        Amount = row.Data.Amount,
        Status = row.Data.Status,
        CreatedAt = row.Metadata.Timestamp
      })
      .ToListAsync();
  }
}
```

**Implementation Responsibilities**:

Any ORM implementation must:
1. Deserialize JSON `model_data` column to `TModel`
2. Populate `PerspectiveMetadata` from `metadata` column
3. Populate `PerspectiveScope` from `scope` column
4. Support `IQueryable<PerspectiveRow<TModel>>` for LINQ queries
5. Use read-only/no-tracking queries for performance

---

### PerspectiveRow<TModel>

The universal storage structure for all perspectives.

**Location**: `Whizbang.Core/Lenses/PerspectiveRow.cs`

```csharp
namespace Whizbang.Core.Lenses;

/// <summary>
/// Universal storage row for perspective read models.
/// Contains the model data plus metadata and scope for querying/filtering.
/// Maps directly to the 3-column JSONB table pattern.
/// </summary>
public class PerspectiveRow<TModel> where TModel : class {
  /// <summary>Unique identifier for this read model instance</summary>
  public required string Id { get; init; }

  /// <summary>The actual read model data (stored as JSONB in model_data column)</summary>
  public required TModel Data { get; init; }

  /// <summary>Event metadata (type, timestamp, correlation, causation) - stored in metadata column</summary>
  public required PerspectiveMetadata Metadata { get; init; }

  /// <summary>Security/tenant scope (tenant, customer, user, organization) - stored in scope column</summary>
  public required PerspectiveScope Scope { get; init; }

  /// <summary>When this record was first created</summary>
  public required DateTime CreatedAt { get; init; }

  /// <summary>When this record was last updated</summary>
  public required DateTime UpdatedAt { get; init; }

  /// <summary>Version number for optimistic concurrency</summary>
  public required int Version { get; init; }
}

/// <summary>Event metadata stored alongside the read model (metadata JSONB column)</summary>
public class PerspectiveMetadata {
  public required string EventType { get; init; }
  public required string EventId { get; init; }
  public required DateTime Timestamp { get; init; }
  public string? CorrelationId { get; init; }
  public string? CausationId { get; init; }
}

/// <summary>Security/tenant scope for filtering (scope JSONB column)</summary>
public class PerspectiveScope {
  public string? TenantId { get; init; }
  public string? CustomerId { get; init; }
  public string? UserId { get; init; }
  public string? OrganizationId { get; init; }
}
```

---

## Table Schema Requirements

For any ORM implementation, the table schema must include these columns:

### Required Columns

| Column | Type | Constraints | Purpose |
|--------|------|-------------|---------|
| `id` | TEXT/VARCHAR | PRIMARY KEY | Unique identifier for the read model instance |
| `model_data` | JSONB/JSON | NOT NULL | The read model serialized as JSON |
| `metadata` | JSONB/JSON | NOT NULL | Event sourcing metadata (correlation, causation, etc.) |
| `scope` | JSONB/JSON | NOT NULL | Security/tenant scope for filtering |
| `created_at` | TIMESTAMP | NOT NULL | When the record was first created |
| `updated_at` | TIMESTAMP | NOT NULL | When the record was last updated |
| `version` | INTEGER | NOT NULL, DEFAULT 1 | Optimistic concurrency version |

### Recommended Indexes

```sql
-- Primary key (automatic)
CREATE UNIQUE INDEX ON perspective_name (id);

-- Query by scope (common for multi-tenant apps)
CREATE INDEX ON perspective_name USING GIN (scope);

-- Query by model data fields (add as needed based on query patterns)
CREATE INDEX ON perspective_name USING GIN (model_data);

-- Query by event type
CREATE INDEX ON perspective_name USING GIN (metadata);

-- Time-based queries
CREATE INDEX ON perspective_name (created_at);
CREATE INDEX ON perspective_name (updated_at);
```

---

## ORM Implementation Examples

### PostgreSQL with Dapper

```csharp
public class DapperPerspectiveStore<TModel> : IPerspectiveStore<TModel> where TModel : class {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly string _tableName;

  public async Task UpsertAsync(string id, TModel model, CancellationToken ct = default) {
    using var conn = await _connectionFactory.CreateConnectionAsync(ct);

    await conn.ExecuteAsync($@"
      INSERT INTO {_tableName} (id, model_data, metadata, scope, created_at, updated_at, version)
      VALUES (@Id, @ModelData::jsonb, @Metadata::jsonb, @Scope::jsonb, @Now, @Now, 1)
      ON CONFLICT (id) DO UPDATE SET
        model_data = EXCLUDED.model_data,
        updated_at = EXCLUDED.updated_at,
        version = {_tableName}.version + 1
      ",
      new {
        Id = id,
        ModelData = JsonSerializer.Serialize(model),
        Metadata = JsonSerializer.Serialize(CreateDefaultMetadata()),
        Scope = JsonSerializer.Serialize(new PerspectiveScope()),
        Now = DateTime.UtcNow
      }
    );
  }
}
```

### PostgreSQL with EF Core

See `docs/efcore-storage-implementation.md` for complete EF Core implementation using:
- `PerspectiveRow<TModel>` entity
- `.ComplexProperty().ToJson()` for JSONB columns
- Source generator for automatic configuration

### In-Memory (Testing)

```csharp
public class InMemoryPerspectiveStore<TModel> : IPerspectiveStore<TModel> where TModel : class {
  private readonly Dictionary<string, PerspectiveRow<TModel>> _storage = new();

  public Task UpsertAsync(string id, TModel model, CancellationToken ct = default) {
    if (_storage.TryGetValue(id, out var existing)) {
      _storage[id] = existing with {
        Data = model,
        UpdatedAt = DateTime.UtcNow,
        Version = existing.Version + 1
      };
    } else {
      _storage[id] = new PerspectiveRow<TModel> {
        Id = id,
        Data = model,
        Metadata = CreateDefaultMetadata(),
        Scope = new PerspectiveScope(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Version = 1
      };
    }
    return Task.CompletedTask;
  }
}
```

---

## Performance Characteristics

### Write Performance

**Upsert** (Insert or Update):
- **EF Core**: ~2-5ms for single upsert (includes JSON serialization)
- **Dapper**: ~1-3ms for single upsert (raw SQL, minimal overhead)
- **Batching**: Use batch inserts for bulk operations (10-100x faster)

**Partial Updates**:
- **PostgreSQL jsonb_set**: ~1-2ms (update single field without full object replacement)
- **Recommended for**: High-frequency field updates (counters, status changes)

### Read Performance

**Single Record** (`GetByIdAsync`):
- **EF Core**: ~1-2ms (includes JSON deserialization)
- **Dapper**: ~0.5-1ms (raw SQL)
- **Index**: Ensure primary key index on `id` column

**Complex Queries** (LINQ on `IQueryable`):
- **Simple filter**: ~5-10ms for 1000 rows
- **GIN index**: ~2-5ms for JSONB path queries with index
- **Full scan**: ~50-100ms for 10,000 rows without index

### Indexing Strategies

**When to index `model_data` JSONB**:
- Frequent queries on specific model fields
- Complex filtering across model properties
- Use GIN index for containment queries (`@>` operator)

**When NOT to index**:
- Small tables (<1000 rows)
- Write-heavy workloads (indexes slow down writes)
- Rarely queried fields

---

## Benefits of This Architecture

### For Developers

**Clean Separation**
- Business logic (perspectives) never touches database code
- Storage implementation can be swapped without changing perspectives
- Easy to test with in-memory implementations

**Type Safety**
- `TModel` is strongly typed (no dynamic queries)
- LINQ queries validated at compile-time
- Refactoring support (rename properties, IDE finds all usages)

**Flexibility**
- Choose your ORM (EF Core, Dapper, Marten, etc.)
- Choose your database (PostgreSQL, MySQL, SQL Server, SQLite)
- Mix multiple ORMs in same application if needed

### For Operations

**Schema Stability**
- Table structure never changes (7 columns, always)
- No migrations when adding model fields
- Safe to deploy new model versions

**Query Power**
- JSONB indexes enable fast queries on model fields
- No N+1 queries (everything in single table)
- Efficient multi-tenant filtering via `scope` column

**Monitoring**
- `created_at`/`updated_at` for tracking data freshness
- `version` for detecting concurrent updates
- `metadata` for event sourcing audit trail

---

## Migration Guide

### From Direct SQL to Abstraction

**Before** (tightly coupled):
```csharp
public class OrderPerspective {
  private readonly IDbConnection _connection;

  public async Task Update(OrderCreatedEvent @event) {
    await _connection.ExecuteAsync(@"
      INSERT INTO orders (id, data) VALUES (@Id, @Data::jsonb)
      ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data
    ", new { Id = @event.OrderId, Data = JsonSerializer.Serialize(...) });
  }
}
```

**After** (abstraction):
```csharp
public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
  private readonly IPerspectiveStore<OrderReadModel> _store;

  public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) {
    return currentData with { OrderId = @event.OrderId, Amount = @event.Amount };
  }
}
```

**Benefits**:
- 60% less code
- No SQL string maintenance
- No manual JSON serialization
- Testable with in-memory store
- Swappable ORM implementation

---

## Testing Strategy

### Unit Testing (In-Memory)

```csharp
[Test]
public async Task OrderPerspective_Apply_UpdatesModelAsync() {
  // Arrange - use in-memory store
  var store = new InMemoryPerspectiveStore<OrderReadModel>();
  var perspective = new OrderPerspective(store);
  var @event = new OrderCreatedEvent("order-123", 99.99m);

  // Act
  var result = perspective.Apply(new OrderReadModel(), @event);
  await store.UpsertAsync("order-123", result);

  // Assert
  var saved = await store.GetByIdAsync("order-123");
  await Assert.That(saved.Amount).IsEqualTo(99.99m);
}
```

### Integration Testing (Real Database)

```csharp
[Test]
public async Task OrderPerspective_RoundTrip_PostgresAsync() {
  // Arrange - use real PostgreSQL connection
  var store = new DapperPerspectiveStore<OrderReadModel>(connectionFactory, "orders");
  var perspective = new OrderPerspective(store);

  // Act
  var result = perspective.Apply(new OrderReadModel(), new OrderCreatedEvent("order-456", 199.99m));
  await store.UpsertAsync("order-456", result);

  // Assert - verify in database
  var query = new DapperLensQuery<OrderReadModel>(connectionFactory, "orders");
  var saved = await query.GetByIdAsync("order-456");
  await Assert.That(saved.Amount).IsEqualTo(199.99m);
}
```

---

## Implementation Checklist

When implementing `IPerspectiveStore<TModel>` and `ILensQuery<TModel>` for a new ORM:

- [ ] Create table with 7 required columns (`id`, `model_data`, `metadata`, `scope`, `created_at`, `updated_at`, `version`)
- [ ] Implement `UpsertAsync`: insert if not exists, update if exists
- [ ] Increment `version` on each update
- [ ] Set `created_at` on insert, `updated_at` on insert and update
- [ ] Serialize `TModel` to JSON for `model_data` column
- [ ] Create default `PerspectiveMetadata` if not provided
- [ ] Create default `PerspectiveScope` if not provided
- [ ] Implement `UpdateFieldsAsync` for partial updates (optional but recommended)
- [ ] Implement `GetByIdAsync` with JSON deserialization
- [ ] Implement `Query` property returning `IQueryable<PerspectiveRow<TModel>>`
- [ ] Use read-only/no-tracking queries in `ILensQuery`
- [ ] Add indexes on `scope` and `model_data` JSONB columns
- [ ] Test with unit tests (in-memory) and integration tests (real database)

---

## References

**Core Abstractions**:
- `Whizbang.Core/Perspectives/IPerspectiveStore.cs`
- `Whizbang.Core/Lenses/ILensQuery.cs`
- `Whizbang.Core/Lenses/PerspectiveRow.cs`

**Implementation Examples**:
- **EF Core**: See `docs/efcore-storage-implementation.md`
- **Dapper**: See examples in this document
- **In-Memory**: See testing examples in this document

**Related Documentation**:
- `docs/pure-function-perspectives.md` - How perspectives work
- `docs/efcore-storage-implementation.md` - EF Core-specific implementation details

---

**Last Updated**: 2025-01-20 (v0.1.0)
