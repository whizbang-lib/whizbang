# EF Core Storage Implementation

**Status**: ✅ Complete (v0.1.0 - January 2025)

This document describes the **EF Core-specific implementation** of Whizbang's perspective storage abstractions using Entity Framework Core 10 and PostgreSQL JSONB.

> **📖 See Also**: `docs/perspective-storage.md` for ORM-agnostic storage concepts, interfaces, and the 3-column JSONB pattern.

---

## Overview

The EF Core implementation provides concrete implementations of `IPerspectiveStore<TModel>` and `ILensQuery<TModel>` using Entity Framework Core 10 with PostgreSQL JSONB storage.

### Key Components

1. **EFCorePostgresPerspectiveStore<TModel>** - Concrete write implementation
2. **EFCorePostgresLensQuery<TModel>** - Concrete read implementation
3. **Source Generator** - Auto-generates EF Core configuration
4. **ComplexProperty().ToJson()** - EF Core 10 JSONB mapping

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
1. Discovers all classes implementing `IPerspectiveFor<TModel, TEvent>`
2. Infers model type from first type parameter
3. Generates table name using snake_case convention ("OrderReadModel" → "order_read_model")
4. Creates `ConfigureWhizbangPerspectives()` extension method
5. Configures `.ComplexProperty().ToJson()` for PostgreSQL JSONB columns

**Generated Code Example**:
```csharp
// WhizbangModelBuilderExtensions.g.cs
namespace Whizbang.Data.EFCore.Postgres;

public static class WhizbangModelBuilderExtensions {
  public static ModelBuilder ConfigureWhizbangPerspectives(this ModelBuilder modelBuilder) {

    // Configure global::MyApp.OrderReadModel
    modelBuilder.Entity<PerspectiveRow<global::MyApp.OrderReadModel>>(entity => {
      entity.ToTable("order_read_model");
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
  public async Task OrderPerspective_Apply_SavesModelAsync() {
    // Arrange
    var options = CreateInMemoryOptions();
    await using var context = new TestDbContext(options);
    var store = new EFCorePostgresPerspectiveStore<OrderReadModel>(context, "order");
    var perspective = new OrderPerspective(store);

    // Act
    var result = perspective.Apply(new OrderReadModel(), new OrderCreatedEvent("order-123", 99.99m));
    await store.UpsertAsync("order-123", result);

    // Assert
    var saved = await context.Set<PerspectiveRow<OrderReadModel>>()
      .FirstOrDefaultAsync(r => r.Id == "order-123");

    Assert.That(saved).IsNotNull();
    Assert.That(saved!.Data.Amount).IsEqualTo(99.99m);
  }

  // Test DbContext uses .OwnsOne() for InMemory compatibility
  private class TestDbContext : DbContext {
    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<OrderReadModel>>(entity => {
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
public record OrderReadModel {
  public string? OrderId { get; init; }
  public decimal Amount { get; init; }
  public string? Status { get; init; }
}
```

### 2. Implement the Perspective

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

### 3. Implement the Lens

```csharp
public class OrderLens {
  private readonly ILensQuery<OrderReadModel> _query;

  public OrderLens(ILensQuery<OrderReadModel> query) {
    _query = query;
  }

  public async Task<OrderReadModel?> GetOrderAsync(string orderId) {
    return await _query.GetByIdAsync(orderId);
  }

  public async Task<List<OrderReadModel>> GetRecentOrdersAsync(int count = 10) {
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
services.AddScoped<IPerspectiveStore<OrderReadModel>>(sp => {
  var context = sp.GetRequiredService<AppDbContext>();
  return new EFCorePostgresPerspectiveStore<OrderReadModel>(context, "order_read_model");
});

services.AddScoped<ILensQuery<OrderReadModel>>(sp => {
  var context = sp.GetRequiredService<AppDbContext>();
  return new EFCorePostgresLensQuery<OrderReadModel>(context, "order_read_model");
});

// Register perspective and lens
services.AddScoped<OrderPerspective>();
services.AddScoped<OrderLens>();
```

### 6. Generated Table Schema

```sql
CREATE TABLE "order_read_model" (
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

## EF Core-Specific Benefits

### ComplexProperty().ToJson() Advantages

**EF Core 10 Feature**:
- Maps complex types to JSONB columns directly
- No manual JSON serialization in application code
- Change tracking on complex type properties
- Migration generation for JSONB columns

**Example Configuration**:
```csharp
entity.ComplexProperty(e => e.Data).ToJson("model_data");
```

This generates PostgreSQL JSONB column with automatic serialization/deserialization.

### OwnsOne() vs ToJson()

**For Testing (InMemory)**:
```csharp
entity.OwnsOne(e => e.Data, data => { data.WithOwner(); });
```

**For Production (PostgreSQL)**:
```csharp
entity.ComplexProperty(e => e.Data).ToJson("model_data");
```

**Why Different?**:
- InMemory provider doesn't support JSON columns
- `.OwnsOne()` creates normalized tables in InMemory
- `.ToJson()` creates JSONB columns in PostgreSQL
- Source generator uses `.ToJson()` for production

### Migration Generation

EF Core migrations automatically handle JSONB columns:

```bash
dotnet ef migrations add AddOrderPerspective
```

Generates:
```csharp
migrationBuilder.CreateTable(
    name: "order_read_model",
    columns: table => new {
        id = table.Column<string>(nullable: false),
        model_data = table.Column<string>(type: "jsonb", nullable: false),
        metadata = table.Column<string>(type: "jsonb", nullable: false),
        scope = table.Column<string>(type: "jsonb", nullable: false),
        created_at = table.Column<DateTime>(nullable: false),
        updated_at = table.Column<DateTime>(nullable: false),
        version = table.Column<int>(nullable: false)
    },
    constraints: table => {
        table.PrimaryKey("PK_order_read_model", x => x.id);
    });
```

---

## Migration from Dapper Pattern

If migrating from direct Dapper usage:

**Before** (Dapper - manual SQL):
```csharp
public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
  private readonly IDbConnectionFactory _connectionFactory;

  public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) {
    return currentData with {
      OrderId = @event.OrderId,
      Amount = @event.Amount,
      Status = "Created"
    };
  }

  // Separate persistence layer
  public async Task SaveAsync(string id, OrderReadModel model, CancellationToken ct = default) {
    using var conn = await _connectionFactory.CreateConnectionAsync(ct);

    await conn.ExecuteAsync(@"
      INSERT INTO order_perspective (id, model_data, metadata, scope, ...)
      VALUES (@Id, @ModelData::jsonb, @Metadata::jsonb, @Scope::jsonb, ...)
      ON CONFLICT (id) DO UPDATE SET ...",
      new { Id = id, ModelData = JsonSerializer.Serialize(model), ... }
    );
  }
}
```

**After** (EF Core - abstraction):
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

// Persistence handled by IPerspectiveStore implementation
```

**Benefits of Migration**:
- 80% less code
- No manual JSON serialization
- No SQL string maintenance
- Automatic metadata/scope handling
- Built-in versioning
- Easier testing
- Source generator handles configuration

---

## Project Status

**Implementation**: ✅ Complete
- EF Core concrete implementations complete
- Source generator working
- 22 tests passing (17 store/query + 5 perspective integration)

**Documentation**: ✅ This document

**Coverage**: ~95% for EF Core implementations

---

## References

**ORM-Agnostic Concepts**:
- `docs/perspective-storage.md` - Storage abstractions, 3-column JSONB pattern, interface definitions

**EF Core Implementation**:
- `Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveStore.cs`
- `Whizbang.Data.EFCore.Postgres/EFCorePostgresLensQuery.cs`
- `Whizbang.Data.EFCore.Postgres.Generators/EFCorePerspectiveConfigurationGenerator.cs`

**Tests**:
- `Whizbang.Data.EFCore.Postgres.Tests/`
- `Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs` (complete example)

**Core Interfaces**:
- `Whizbang.Core/Perspectives/IPerspectiveStore.cs`
- `Whizbang.Core/Lenses/ILensQuery.cs`
- `Whizbang.Core/Lenses/PerspectiveRow.cs`

---

**Last Updated**: 2025-01-20 (v0.1.0)
