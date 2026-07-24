# Phase 1: AOT-Compatible Perspective Schema Integration - API Design

**Date**: 2025-01-17
**Status**: Design Phase
**AOT Requirement**: Zero reflection allowed

---

## Problem Statement

The `PerspectiveSchemaGenerator` generates SQL DDL at compile-time in `Whizbang.Generated.PerspectiveSchemas.Sql`, but this SQL is never executed. Applications currently need manual `schema.sql` files for perspective tables.

## Design Goals

1. **AOT Compatible**: No reflection, no runtime type discovery
2. **Explicit**: Consumer explicitly provides generated SQL
3. **Optional**: Perspectives remain optional - only execute SQL if provided
4. **Backward Compatible**: Existing code without perspectives continues to work
5. **Testable**: All branches must be testable for 100% branch coverage

---

## API Changes

### 1. PostgresSchemaInitializer Constructor

**Current**:
```csharp
public PostgresSchemaInitializer(string connectionString) {
  ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
  _connectionString = connectionString;
}
```

**New**:
```csharp
public PostgresSchemaInitializer(
  string connectionString,
  string? perspectiveSchemaSql = null) {
  ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
  _connectionString = connectionString;
  _perspectiveSchemaSql = perspectiveSchemaSql;
}
```

**Rationale**:
- Optional `perspectiveSchemaSql` parameter (nullable, default `null`)
- Stored as private field for use in `InitializeSchema` methods
- AOT-safe: No reflection, just string parameter

---

### 2. PostgresSchemaInitializer.InitializeSchemaAsync

**Current**:
```csharp
public async Task InitializeSchemaAsync(CancellationToken cancellationToken = default) {
  var schemaSql = await LoadEmbeddedSchemaAsync();

  await using var connection = new NpgsqlConnection(_connectionString);
  await connection.OpenAsync(cancellationToken);

  await using var command = connection.CreateCommand();
  command.CommandText = schemaSql;
  command.CommandTimeout = 30;

  await command.ExecuteNonQueryAsync(cancellationToken);
}
```

**New**:
```csharp
public async Task InitializeSchemaAsync(CancellationToken cancellationToken = default) {
  var schemaSql = await LoadEmbeddedSchemaAsync();

  await using var connection = new NpgsqlConnection(_connectionString);
  await connection.OpenAsync(cancellationToken);

  // Execute infrastructure schema (event store, inbox, outbox, etc.)
  await using var command = connection.CreateCommand();
  command.CommandText = schemaSql;
  command.CommandTimeout = 30;
  await command.ExecuteNonQueryAsync(cancellationToken);

  // Execute perspective schema if provided
  if (!string.IsNullOrWhiteSpace(_perspectiveSchemaSql)) {
    await using var perspectiveCommand = connection.CreateCommand();
    perspectiveCommand.CommandText = _perspectiveSchemaSql;
    perspectiveCommand.CommandTimeout = 30;
    await perspectiveCommand.ExecuteNonQueryAsync(cancellationToken);
  }
}
```

**Branch Coverage Requirements**:
- ✅ Path 1: `_perspectiveSchemaSql` is `null` → skip perspective SQL
- ✅ Path 2: `_perspectiveSchemaSql` is empty string → skip perspective SQL
- ✅ Path 3: `_perspectiveSchemaSql` is whitespace → skip perspective SQL
- ✅ Path 4: `_perspectiveSchemaSql` has valid SQL → execute perspective SQL

---

### 3. PostgresSchemaInitializer.InitializeSchema (Sync)

**Current**:
```csharp
public void InitializeSchema() {
  var schemaSql = LoadEmbeddedSchema();

  using var connection = new NpgsqlConnection(_connectionString);
  connection.Open();

  using var command = connection.CreateCommand();
  command.CommandText = schemaSql;
  command.CommandTimeout = 30;

  command.ExecuteNonQuery();
}
```

**New**:
```csharp
public void InitializeSchema() {
  var schemaSql = LoadEmbeddedSchema();

  using var connection = new NpgsqlConnection(_connectionString);
  connection.Open();

  // Execute infrastructure schema
  using var command = connection.CreateCommand();
  command.CommandText = schemaSql;
  command.CommandTimeout = 30;
  command.ExecuteNonQuery();

  // Execute perspective schema if provided
  if (!string.IsNullOrWhiteSpace(_perspectiveSchemaSql)) {
    using var perspectiveCommand = connection.CreateCommand();
    perspectiveCommand.CommandText = _perspectiveSchemaSql;
    perspectiveCommand.CommandTimeout = 30;
    perspectiveCommand.ExecuteNonQuery();
  }
}
```

**Branch Coverage Requirements**: Same as async version

---

### 4. ServiceCollectionExtensions.AddWhizbangPostgres

**Current**:
```csharp
public static IServiceCollection AddWhizbangPostgres(
  this IServiceCollection services,
  string connectionString,
  JsonSerializerOptions jsonOptions,
  bool initializeSchema = false) {

  if (initializeSchema) {
    var initializer = new PostgresSchemaInitializer(connectionString);
    initializer.InitializeSchema();
  }

  // ... rest of registrations
}
```

**New**:
```csharp
public static IServiceCollection AddWhizbangPostgres(
  this IServiceCollection services,
  string connectionString,
  JsonSerializerOptions jsonOptions,
  bool initializeSchema = false,
  string? perspectiveSchemaSql = null) {

  if (initializeSchema) {
    var initializer = new PostgresSchemaInitializer(
      connectionString,
      perspectiveSchemaSql);
    initializer.InitializeSchema();
  }

  // ... rest of registrations (unchanged)
}
```

**Branch Coverage Requirements**:
- ✅ Path 1: `initializeSchema = false` → no initialization
- ✅ Path 2: `initializeSchema = true, perspectiveSchemaSql = null` → infra only
- ✅ Path 3: `initializeSchema = true, perspectiveSchemaSql = "..."` → infra + perspectives

---

## Usage Example (Consumer Code)

```csharp
// In Program.cs or Startup
services.AddWhizbangPostgres(
  connectionString: builder.Configuration.GetConnectionString("Postgres")!,
  jsonOptions: WhizbangJsonContext.Default.Options,
  initializeSchema: true,
  perspectiveSchemaSql: Whizbang.Generated.PerspectiveSchemas.Sql  // ← Explicit!
);
```

**Key Points**:
- Consumer explicitly passes `PerspectiveSchemas.Sql`
- Generated class is fully qualified: `Whizbang.Generated.PerspectiveSchemas`
- No reflection anywhere
- If consumer doesn't have perspectives, they simply omit the parameter

---

## Test Coverage Requirements

### PostgresSchemaInitializer Tests (8 total)

1. **Constructor Tests** (2):
   - ✅ Valid connection string, no perspective SQL
   - ✅ Valid connection string, with perspective SQL

2. **InitializeSchemaAsync Tests** (4):
   - ✅ Executes infrastructure SQL only (no perspective SQL)
   - ✅ Executes infrastructure + perspective SQL (valid SQL provided)
   - ✅ Skips perspective SQL when null
   - ✅ Skips perspective SQL when empty/whitespace

3. **InitializeSchema Tests** (2):
   - ✅ Executes infrastructure SQL only (sync version)
   - ✅ Executes infrastructure + perspective SQL (sync version)

### ServiceCollectionExtensions Tests (3 total)

1. **AddWhizbangPostgres Tests** (3):
   - ✅ `initializeSchema = false` → no schema initialization
   - ✅ `initializeSchema = true, perspectiveSchemaSql = null` → infra only
   - ✅ `initializeSchema = true, perspectiveSchemaSql = "..."` → infra + perspectives

**Total New Tests**: 11 tests
**Expected Coverage**: 100% line, 100% branch

---

## Files Modified

1. `src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs`
   - Add `_perspectiveSchemaSql` field
   - Update constructor signature
   - Update `InitializeSchemaAsync` method
   - Update `InitializeSchema` method

2. `src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs`
   - Update `AddWhizbangPostgres` signature
   - Pass `perspectiveSchemaSql` to initializer

3. `tests/Whizbang.Data.Postgres.Tests/PostgresSchemaInitializerTests.cs`
   - **NEW FILE**: 8 tests for PostgresSchemaInitializer

4. `tests/Whizbang.Data.Postgres.Tests/ServiceCollectionExtensionsTests.cs`
   - Add 3 tests for new parameter behavior

---

## AOT Compatibility Verification

**Forbidden Patterns** (NONE USED):
```csharp
❌ Type.GetType("Whizbang.Generated.PerspectiveSchemas")
❌ Assembly.GetTypes()
❌ typeof(PerspectiveSchemas).GetProperty("Sql")
❌ Activator.CreateInstance()
```

**Allowed Patterns** (ALL USED):
```csharp
✅ string parameter passing
✅ Compile-time constants
✅ Direct field access
✅ Static property access (consumer-side)
```

---

## Rollout Plan

1. ✅ Phase 1.1: Baseline tests (1550 passing) - COMPLETED
2. ✅ Phase 1.2: API Design documentation - IN PROGRESS
3. 🔴 Phase 1.3: Write failing tests (RED)
4. 🔴 Phase 1.4: Measure baseline coverage
5. 🔴 Phase 1.5: Implement feature (GREEN)
6. 🔴 Phase 1.6: Measure post-implementation coverage (100%)
7. 🔴 Phase 1.7: Verify no regressions
8. 🔴 Phase 1.8: Verify AOT compatibility
9. 🔴 Phase 1.9: Refactor and format
10. 🔴 Phase 1.10: Integration test in ECommerce sample

---

## Acceptance Criteria

- [x] API design reviewed and documented
- [ ] Zero reflection used anywhere
- [ ] 11 new tests written (all failing initially)
- [ ] All tests passing after implementation
- [ ] 100% line coverage on modified code
- [ ] 100% branch coverage on modified code
- [ ] No regressions in existing 1550 tests
- [ ] Integration test in ECommerce sample works
- [ ] Code formatted with `dotnet format`
