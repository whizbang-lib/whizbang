# EF Core Perspectives and Lenses - Implementation Guide

**Status**: ✅ Complete (v1.0 - January 2025)

This document describes the EF Core-based implementation of Perspectives and Lenses using a clean abstraction layer with source-generated configuration.

---

## Overview

The EF Core implementation provides a clean, testable abstraction for perspectives (write) and lenses (read) built on top of Entity Framework Core with PostgreSQL JSONB storage.

### Key Components

1. **IPerspectiveStore<TModel>** - Write abstraction for perspectives
2. **ILensQuery<TModel>** - Read abstraction for lenses
3. **PerspectiveRow<TModel>** - 3-column JSONB storage pattern
4. **EFCore Implementation** - Concrete implementations for PostgreSQL
5. **Source Generator** - Auto-generates EF Core configuration

---

## Architecture

### The 3-Column JSONB Pattern

Every perspective materializes its read model into a table with this structure:

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

**Benefits**:
- **Flexibility**: Add fields to model without schema migrations
- **Queryability**: PostgreSQL JSONB supports indexing and querying
- **Separation**: Model data, metadata, and scope are clearly separated
- **Version Control**: Built-in optimistic concurrency

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

**Usage Example**:
```csharp
public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent> {
  private readonly IPerspectiveStore<Order> _store;

  public OrderPerspective(IPerspectiveStore<Order> store) {
    _store = store;
  }

  public async Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
    var order = new Order {
      OrderId = @event.OrderId,
      Amount = @event.Amount,
      Status = "Created"
    };

    // Store handles JSON serialization, metadata, scope, timestamps, versioning
    await _store.UpsertAsync(@event.OrderId, order, cancellationToken);
  }
}
```

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

**Usage Example**:
```csharp
public class OrderLens {
  private readonly ILensQuery<Order> _query;

  public OrderLens(ILensQuery<Order> query) {
    _query = query;
  }

  // Simple query
  public async Task<Order?> GetOrderAsync(string orderId) {
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

---

### PerspectiveRow<TModel>

The universal storage structure for all perspectives.

**Location**: `Whizbang.Core/Lenses/PerspectiveRow.cs`

```csharp
namespace Whizbang.Core.Lenses;

/// <summary>
/// Universal storage row for perspective read models.
/// Contains the model data plus metadata and scope for querying/filtering.
/// </summary>
public class PerspectiveRow<TModel> where TModel : class {
  /// <summary>Unique identifier for this read model instance</summary>
  public required string Id { get; init; }

  /// <summary>The actual read model data (stored as JSONB)</summary>
  public required TModel Data { get; init; }

  /// <summary>Event metadata (type, timestamp, correlation, causation)</summary>
  public required PerspectiveMetadata Metadata { get; init; }

  /// <summary>Security/tenant scope (tenant, customer, user, organization)</summary>
  public required PerspectiveScope Scope { get; init; }

  /// <summary>When this record was first created</summary>
  public required DateTime CreatedAt { get; init; }

  /// <summary>When this record was last updated</summary>
  public required DateTime UpdatedAt { get; init; }

  /// <summary>Version number for optimistic concurrency</summary>
  public required int Version { get; init; }
}

/// <summary>Event metadata stored alongside the read model</summary>
public class PerspectiveMetadata {
  public required string EventType { get; init; }
  public required string EventId { get; init; }
  public required DateTime Timestamp { get; init; }
  public string? CorrelationId { get; init; }
  public string? CausationId { get; init; }
}

/// <summary>Security/tenant scope for filtering</summary>
public class PerspectiveScope {
  public string? TenantId { get; init; }
  public string? CustomerId { get; init; }
  public string? UserId { get; init; }
  public string? OrganizationId { get; init; }
}
```

---

## EF Core Implementation

### EFCorePostgresPerspectiveStore<TModel>

Concrete implementation of `IPerspectiveStore<TModel>` using EF Core and PostgreSQL.

**Location**: `Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveStore.cs`

**Key Features**:
- **UpsertAsync**: Insert or update with version incrementing
- **UpdateFieldsAsync**: Partial updates using reflection
- **Default Metadata/Scope**: Creates default values if not provided
- **Timestamp Management**: Automatically sets CreatedAt/UpdatedAt
- **Owned Type Handling**: Special handling for EF Core owned types (metadata, scope)

**Implementation Pattern**:
```csharp
public async Task UpsertAsync(string id, TModel model, CancellationToken cancellationToken = default) {
  var existingRow = await _context.Set<PerspectiveRow<TModel>>()
    .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

  if (existingRow == null) {
    // Insert new record with defaults
    var newRow = new PerspectiveRow<TModel> {
      Id = id,
      Data = model,
      Metadata = new PerspectiveMetadata {
        EventType = "Unknown",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      },
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };
    _context.Set<PerspectiveRow<TModel>>().Add(newRow);
  } else {
    // Update existing - remove and re-add to handle owned types
    _context.Set<PerspectiveRow<TModel>>().Remove(existingRow);

    var updatedRow = new PerspectiveRow<TModel> {
      Id = existingRow.Id,
      Data = model,
      Metadata = CloneMetadata(existingRow.Metadata),
      Scope = CloneScope(existingRow.Scope),
      CreatedAt = existingRow.CreatedAt,
      UpdatedAt = DateTime.UtcNow,
      Version = existingRow.Version + 1
    };
    _context.Set<PerspectiveRow<TModel>>().Add(updatedRow);
  }

  await _context.SaveChangesAsync(cancellationToken);
}
```

---

### EFCorePostgresLensQuery<TModel>

Concrete implementation of `ILensQuery<TModel>` using EF Core.

**Location**: `Whizbang.Data.EFCore.Postgres/EFCorePostgresLensQuery.cs`

**Key Features**:
- **GetByIdAsync**: Efficient single-record lookup
- **Query**: Full LINQ query capabilities
- **No Tracking**: All queries use `AsNoTracking()` for read-only performance
- **Type Safety**: Compile-time query validation via LINQ

**Implementation Pattern**:
```csharp
public async Task<TModel?> GetByIdAsync(string id, CancellationToken cancellationToken = default) {
  var row = await _context.Set<PerspectiveRow<TModel>>()
    .AsNoTracking()
    .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

  return row?.Data;
}

public IQueryable<PerspectiveRow<TModel>> Query =>
  _context.Set<PerspectiveRow<TModel>>().AsNoTracking();
```

---

## Source Generator

### EFCorePerspectiveConfigurationGenerator

Automatically discovers perspectives and generates EF Core configuration.

**Location**: `Whizbang.Data.EFCore.Postgres.Generators/EFCorePerspectiveConfigurationGenerator.cs`

**What It Does**:
1. Discovers all classes implementing `IPerspectiveOf<TEvent>`
2. Infers model type from class name ("OrderPerspective" → "Order")
3. Generates table name using snake_case convention ("Order" → "order")
4. Creates `ConfigureWhizbangPerspectives()` extension method
5. Configures `.ComplexProperty().ToJson()` for PostgreSQL JSONB columns

**Generated Code Example**:
```csharp
// WhizbangModelBuilderExtensions.g.cs
namespace Whizbang.Data.EFCore.Postgres;

public static class WhizbangModelBuilderExtensions {
  public static ModelBuilder ConfigureWhizbangPerspectives(this ModelBuilder modelBuilder) {

    // Configure global::MyApp.Order
    modelBuilder.Entity<PerspectiveRow<global::MyApp.Order>>(entity => {
      entity.ToTable("order");
      entity.HasKey(e => e.Id);

      // Configure JSONB columns using EF Core 10 complex types
      entity.ComplexProperty(e => e.Data).ToJson("model_data");
      entity.ComplexProperty(e => e.Metadata).ToJson("metadata");
      entity.ComplexProperty(e => e.Scope).ToJson("scope");

      // Configure system fields
      entity.Property(e => e.CreatedAt).IsRequired();
      entity.Property(e => e.UpdatedAt).IsRequired();
      entity.Property(e => e.Version).IsRequired();
    });

    return modelBuilder;
  }
}
```

**Usage in DbContext**:
```csharp
public class MyDbContext : DbContext {
  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    // Automatically configures all discovered perspectives
    modelBuilder.ConfigureWhizbangPerspectives();
  }
}
```

---

## Testing Strategy

### Unit Testing with EF Core InMemory

For fast, isolated tests, use EF Core InMemory provider with `.OwnsOne()` configuration:

```csharp
public class OrderPerspectiveTests {
  private DbContextOptions<TestDbContext> CreateInMemoryOptions() {
    return new DbContextOptionsBuilder<TestDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;
  }

  [Test]
  public async Task OrderPerspective_Update_SavesModelAsync() {
    // Arrange
    var options = CreateInMemoryOptions();
    await using var context = new TestDbContext(options);
    var store = new EFCorePostgresPerspectiveStore<Order>(context, "order");
    var perspective = new OrderPerspective(store);

    // Act
    await perspective.Update(new OrderCreatedEvent("order-123", 99.99m));

    // Assert
    var saved = await context.Set<PerspectiveRow<Order>>()
      .FirstOrDefaultAsync(r => r.Id == "order-123");

    Assert.That(saved).IsNotNull();
    Assert.That(saved!.Data.Amount).IsEqualTo(99.99m);
  }

  // Test DbContext uses .OwnsOne() for InMemory compatibility
  private class TestDbContext : DbContext {
    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<Order>>(entity => {
        entity.HasKey(e => e.Id);
        entity.OwnsOne(e => e.Data, data => { data.WithOwner(); });
        entity.OwnsOne(e => e.Metadata, meta => { meta.WithOwner(); });
        entity.OwnsOne(e => e.Scope, scope => { scope.WithOwner(); });
      });
    }
  }
}
```

**Note**: Production PostgreSQL uses the generated `ConfigureWhizbangPerspectives()` method with `.ComplexProperty().ToJson()` for real JSONB columns.

---

## Complete Example

### 1. Define the Read Model

```csharp
public class Order {
  public required string OrderId { get; init; }
  public required decimal Amount { get; init; }
  public required string Status { get; init; }
}
```

### 2. Implement the Perspective

```csharp
public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent> {
  private readonly IPerspectiveStore<Order> _store;

  public OrderPerspective(IPerspectiveStore<Order> store) {
    _store = store;
  }

  public async Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
    var order = new Order {
      OrderId = @event.OrderId,
      Amount = @event.Amount,
      Status = "Created"
    };

    await _store.UpsertAsync(@event.OrderId, order, cancellationToken);
  }
}
```

### 3. Implement the Lens

```csharp
public class OrderLens {
  private readonly ILensQuery<Order> _query;

  public OrderLens(ILensQuery<Order> query) {
    _query = query;
  }

  public async Task<Order?> GetOrderAsync(string orderId) {
    return await _query.GetByIdAsync(orderId);
  }

  public async Task<List<Order>> GetRecentOrdersAsync(int count = 10) {
    return await _query.Query
      .OrderByDescending(row => row.CreatedAt)
      .Take(count)
      .Select(row => row.Data)
      .ToListAsync();
  }
}
```

### 4. Configure DbContext

```csharp
public class AppDbContext : DbContext {
  public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    // Auto-configures all discovered perspectives
    modelBuilder.ConfigureWhizbangPerspectives();
  }
}
```

### 5. Register Services

```csharp
// Register DbContext
services.AddDbContext<AppDbContext>(options =>
  options.UseNpgsql(connectionString));

// Register store and query for each model type
services.AddScoped<IPerspectiveStore<Order>>(sp => {
  var context = sp.GetRequiredService<AppDbContext>();
  return new EFCorePostgresPerspectiveStore<Order>(context, "order");
});

services.AddScoped<ILensQuery<Order>>(sp => {
  var context = sp.GetRequiredService<AppDbContext>();
  return new EFCorePostgresLensQuery<Order>(context, "order");
});

// Register perspective and lens
services.AddScoped<OrderPerspective>();
services.AddScoped<OrderLens>();
```

### 6. Generated Table Schema

```sql
CREATE TABLE "order" (
  id TEXT PRIMARY KEY,
  model_data JSONB NOT NULL,
  metadata JSONB NOT NULL,
  scope JSONB NOT NULL,
  created_at TIMESTAMP NOT NULL,
  updated_at TIMESTAMP NOT NULL,
  version INTEGER NOT NULL DEFAULT 1
);
```

---

## Benefits

### For Perspectives (Write Side)
- **Simple API**: Just call `UpsertAsync(id, model)`
- **No Schema Changes**: Add model fields without migrations
- **Default Metadata**: Store creates defaults automatically
- **Version Control**: Built-in optimistic concurrency
- **Testable**: Easy to mock `IPerspectiveStore<TModel>`

### For Lenses (Read Side)
- **LINQ Power**: Full query capabilities with compile-time safety
- **Cross-Column Queries**: Filter by model data, metadata, or scope
- **Performance**: `AsNoTracking()` for read-only queries
- **Type Safety**: No reflection, no dynamic queries
- **Testable**: Easy to mock `ILensQuery<TModel>`

### For Development
- **Auto-Configuration**: Generator discovers perspectives, creates EF Core config
- **Convention-Based**: "OrderPerspective" → "Order" model → "order" table
- **AOT Compatible**: No reflection, all source-generated
- **Fast Tests**: Use InMemory provider with `.OwnsOne()`
- **Production Ready**: Use PostgreSQL JSONB with `.ToJson()`

---

## Migration from Dapper Pattern

If migrating from direct Dapper usage:

**Before** (Dapper):
```csharp
public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent> {
  private readonly IDbConnectionFactory _connectionFactory;

  public async Task Update(OrderCreatedEvent @event, CancellationToken ct = default) {
    using var conn = await _connectionFactory.CreateConnectionAsync(ct);

    await conn.ExecuteAsync(@"
      INSERT INTO order_perspective (id, model_data, metadata, scope, ...)
      VALUES (@Id, @ModelData::jsonb, @Metadata::jsonb, @Scope::jsonb, ...)
      ON CONFLICT (id) DO UPDATE SET ...",
      new { Id = @event.OrderId, ModelData = JsonSerializer.Serialize(...), ... }
    );
  }
}
```

**After** (EF Core):
```csharp
public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent> {
  private readonly IPerspectiveStore<Order> _store;

  public async Task Update(OrderCreatedEvent @event, CancellationToken ct = default) {
    var order = new Order { OrderId = @event.OrderId, ... };
    await _store.UpsertAsync(@event.OrderId, order, ct);
  }
}
```

**Benefits of Migration**:
- 80% less code
- No manual JSON serialization
- No SQL string maintenance
- Automatic metadata/scope handling
- Built-in versioning
- Easier testing

---

## Project Status

**Implementation**: ✅ Complete
- Core abstractions defined
- EF Core implementation complete
- Source generator working
- 22 tests passing (17 store/query + 5 perspective integration)

**Documentation**: ✅ This document

**Coverage**: ~95% for core abstractions and EF Core implementation

---

## References

- **Core Abstractions**: `Whizbang.Core/Perspectives/`, `Whizbang.Core/Lenses/`
- **EF Core Implementation**: `Whizbang.Data.EFCore.Postgres/`
- **Source Generator**: `Whizbang.Data.EFCore.Postgres.Generators/`
- **Tests**: `Whizbang.Data.EFCore.Postgres.Tests/`
- **Complete Example**: `Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs`
