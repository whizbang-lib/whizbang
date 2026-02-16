# Fix: EFCorePerspectiveConfigurationGenerator Table Name Bug

## Status: FIXED in 0.5.1-alpha.16

The bug was fixed by changing line 204 in `EFCorePerspectiveConfigurationGenerator.cs` to use `TypeNameUtilities.GetTableBaseName(modelType)` instead of `modelType.Name`.

## Problem

The `EFCorePerspectiveConfigurationGenerator` was mapping ALL perspective entity types to the same table name `wh_per_model` instead of unique table names.

## Error

```
System.InvalidOperationException: The table 'task.wh_per_model' cannot be used for entity type
'PerspectiveRow<Model>' since it is being used for entity type 'PerspectiveRow<Model>' and potentially
other entity types, but there is no linking relationship.
```

## Root Cause

In the generated `WhizbangModelBuilderExtensions.g.cs`, all perspective entities use hardcoded table name:

```csharp
// ActiveJobTemplate.Model
entity.ToTable("wh_per_model");  // WRONG - should be wh_per_active_job_template_model

// ActiveJobTemplateSection.Model
entity.ToTable("wh_per_model");  // WRONG - should be wh_per_active_job_template_section_model

// DraftJob.Model
entity.ToTable("wh_per_model");  // WRONG - should be wh_per_draft_job_model
```

Meanwhile, the `EFCoreServiceRegistrationGenerator` (which generates DbSet properties) uses **correct** table names:

```csharp
/// DbSet for ActiveJobTemplateModels perspective (table: wh_per_active_job_template_model)
public DbSet<PerspectiveRow<...ActiveJobTemplate.Model>> ActiveJobTemplateModels => ...
```

## Solution

In `EFCorePerspectiveConfigurationGenerator`, the table name derivation logic needs to match `EFCoreServiceRegistrationGenerator`.

### Expected Table Name Format

For a perspective model type like `JDX.MockService.Features.MockDraftJobFieldPopulation.Domain.ActiveJobTemplate.Model`:
- Extract: `ActiveJobTemplate` (parent type name, not `Model`)
- Convert to snake_case: `active_job_template`
- Add prefix: `wh_per_active_job_template_model`

### Files to Fix

1. **Whizbang.Data.EFCore.Postgres.Generators/EFCorePerspectiveConfigurationGenerator.cs**
   - Find where `entity.ToTable("wh_per_model")` is being generated
   - Replace with derived table name based on perspective model type

### Table Name Derivation Logic (from EFCoreServiceRegistrationGenerator)

```csharp
// Get the parent type name (e.g., "ActiveJobTemplate" from "ActiveJobTemplate.Model")
var parentTypeName = perspectiveModel.ContainingType?.Name ?? perspectiveModel.Name;
// Convert to snake_case
var snakeCaseName = ToSnakeCase(parentTypeName); // "active_job_template"
// Build table name
var tableName = $"wh_per_{snakeCaseName}_model"; // "wh_per_active_job_template_model"
```

## Affected Services

Any service with multiple perspective models will fail:
- TaskService (5 perspectives → all mapping to same table)
- Likely other services with multiple perspectives

## Workaround

None - this blocks service startup. Must be fixed in Whizbang generator.
