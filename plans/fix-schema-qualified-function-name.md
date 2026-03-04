# Fix: Schema-Qualified Function Name - Wrong Schema Used

## Problem

`EFCoreWorkCoordinator.ProcessWorkBatchAsync` calls the wrong function:

```
42601: syntax error at or near "."
POSITION: 26
```

Or calls the function in wrong schema (e.g., `process_work_batch` instead of `"user".process_work_batch`).

## Root Cause

The schema detection at lines 135-138 uses `FindEntityType(typeof(OutboxRecord))`:

```csharp
var schema = _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema() ?? DEFAULT_SCHEMA;
var functionName = string.IsNullOrEmpty(schema) || schema == DEFAULT_SCHEMA
  ? "process_work_batch"
  : $"{schema}.process_work_batch";
```

**Problem:** `OutboxRecord` is NOT registered as an EF Core entity - it's created via raw SQL in the schema builder. So `FindEntityType()` returns `null`, and the code falls back to `DEFAULT_SCHEMA` ("public").

**But:** The PostgreSQL functions are created in the user-specified schema (e.g., `"user".process_work_batch`), not `public.process_work_batch`.

**Result:** Code tries to call `process_work_batch` (unqualified) but the function exists as `"user".process_work_batch`.

## Example

JDNext's UserService has:
```csharp
[WhizbangDbContext(Schema = "user")]
public partial class UserDbContext : DbContext { }
```

Schema initialization creates: `"user".process_work_batch`

But `EFCoreWorkCoordinator` calls: `SELECT * FROM process_work_batch(...)` (wrong!)

Should call: `SELECT * FROM "user".process_work_batch(...)`

## Solution

**Don't rely on `FindEntityType(typeof(OutboxRecord))`** - it will always return null.

Instead, get the schema from one of:
1. A method on the DbContext that returns its configured schema
2. The `[WhizbangDbContext(Schema = "...")]` attribute via source generator
3. A registered `ISchemaProvider` service

### Suggested Fix

Option 1: Add schema property to generated DbContext partial:

```csharp
// Generated in UserDbContext.Generated.g.cs
public partial class UserDbContext {
  public static string WhizbangSchema => "user";
}
```

Then in `EFCoreWorkCoordinator`:

```csharp
// Get schema from DbContext's generated property
var schema = _dbContext.GetType().GetProperty("WhizbangSchema")?.GetValue(null) as string ?? DEFAULT_SCHEMA;
```

Option 2: Register schema via DI during `AddWhizbang()`:

```csharp
// In EFCoreModelRegistration.g.cs (already runs during startup)
services.AddSingleton(new WhizbangSchemaOptions { Schema = "user" });
```

Then inject `WhizbangSchemaOptions` into `EFCoreWorkCoordinator`.

## Files to Modify

- `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs` - use correct schema source
- `src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs` - generate schema accessor

## Testing

1. Test with non-public schema (e.g., `[WhizbangDbContext(Schema = "user")]`)
2. Verify `process_work_batch` is called with correct schema qualification
3. Test with JDNext services

## Context

- Reported from JDNext project using Whizbang 0.5.1-alpha.26
- All JDNext services use custom schemas: `user`, `bff`, `chat`, `job`, etc.
- WorkCoordinatorPublisherWorker triggers this during startup
- Need fix in 0.5.1-alpha.27
