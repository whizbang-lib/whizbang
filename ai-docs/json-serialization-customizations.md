# Whizbang JSON Serialization Customizations

> **Purpose**: Documents all custom JSON converters, type handling, and edge cases managed by Whizbang's serialization system. Essential reference for debugging serialization issues and understanding database-specific handling.

## Overview

Whizbang uses **AOT-compatible JSON serialization** via source-generated `JsonTypeInfo` factories. When data flows through `MessageJsonContext` (especially for polymorphic models stored as JSONB), custom handling is required for edge cases that System.Text.Json doesn't handle by default.

**When these customizations apply:**
- Polymorphic models using `Property().HasColumnType("jsonb")` instead of `ComplexProperty().ToJson()`
- Message/event serialization through `JsonContextRegistry`
- Any type resolved via the generated `MessageJsonContext`

---

## Custom Converters

### LenientDateTimeOffsetConverter

**Location**: `src/Whizbang.Core/Serialization/LenientDateTimeOffsetConverter.cs`

**Tests**: `tests/Whizbang.Core.Tests/Serialization/LenientDateTimeOffsetConverterTests.cs`

**Purpose**: Handles DateTimeOffset values that don't conform to strict ISO 8601 format, particularly from PostgreSQL JSONB storage.

| Input Format | Output | Notes |
|--------------|--------|-------|
| `"2024-01-15T10:30:00+05:00"` | Preserves offset | Standard ISO 8601 with offset |
| `"2024-01-15T10:30:00Z"` | UTC (offset = 0) | Zulu time |
| `"2024-01-15T10:30:00"` | UTC (offset = 0) | **No timezone - assumes UTC** |
| `"2024-01-15"` | Midnight UTC | Date-only format |
| `"-infinity"` | `DateTimeOffset.MinValue` | **PostgreSQL special value** |
| `"infinity"` | `DateTimeOffset.MaxValue` | **PostgreSQL special value** |
| `""` | `default(DateTimeOffset)` | Empty string |

**Database-specific notes:**
- **PostgreSQL**: Stores `timestamptz` without explicit offset in JSONB; uses `-infinity`/`infinity` for unbounded ranges
- **SQL Server**: TBD - may have different edge cases
- **MySQL**: TBD - may have different edge cases

### LenientNullableDateTimeOffsetConverter

**Location**: `src/Whizbang.Core/Serialization/LenientDateTimeOffsetConverter.cs` (same file)

**Tests**: `tests/Whizbang.Core.Tests/Serialization/LenientDateTimeOffsetConverterTests.cs`

**Purpose**: Nullable wrapper for `LenientDateTimeOffsetConverter`.

| Input | Output |
|-------|--------|
| `null` | `null` |
| Any valid value | Delegates to `LenientDateTimeOffsetConverter` |

---

## Generator-Managed Type Handling

### Nullable Enum Types

**Generator**: `src/Whizbang.Generators/MessageJsonContextGenerator.cs`

**Snippets**: `src/Whizbang.Generators/Templates/Snippets/JsonContextSnippets.cs`
- `LAZY_FIELD_NULLABLE_ENUM`
- `GET_TYPE_INFO_NULLABLE_ENUM`
- `NULLABLE_ENUM_TYPE_FACTORY`

**Tests**:
- `tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_EnumProperty_GeneratesNullableEnumFactoryAsync`
- `tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_NullableEnumProperty_GeneratesBothFactoriesAsync`

**Behavior**: When an enum type is discovered, the generator automatically creates `JsonTypeInfo` for BOTH:
- `EnumType` (non-nullable)
- `EnumType?` (nullable)

This ensures `System.Nullable`1[EnumType]` is always available without needing to track which enums are used as nullable.

### Nested Type Discovery

**Generator**: `src/Whizbang.Generators/MessageJsonContextGenerator.cs` (`_tryGetPublicTypeSymbol` method)

**Tests**: `tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithSiblingNestedTypes_DiscoversBothTypesAsync`

**Issue**: Roslyn's `GetTypeByMetadataName()` expects CLR format (`Namespace.Container+NestedClass`) but property types come from `ToDisplayString()` which uses C# format (`Namespace.Container.NestedClass`).

**Solution**: The generator progressively converts `.` to `+` from right to left until the type is found:
```
Namespace.Container.Nested
→ Namespace.Container+Nested  ✓ Found!
```

### Perspective Model Discovery

**Generator**: `src/Whizbang.Generators/MessageJsonContextGenerator.cs` (`_isPerspectiveModelType` method)

**Tests**: `tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_NestedPerspectiveModel_IsDiscoveredAsync`

**Purpose**: Discovers types used as `TModel` in `IPerspectiveFor<TModel, ...>` implementations.

**Discovery scenarios**:
| Pattern | Example | Discovery |
|---------|---------|-----------|
| Nested model in containing perspective | `ChatSession : IPerspectiveFor<ChatSession.ChatSessionModel>` | Checks containing type |
| Sibling model | `OrderProjection : IPerspectiveFor<OrderModel>` | Checks namespace siblings |
| Top-level model | Separate files | Checks namespace members |

**Key fix**: For nested types like `ChatSession.ChatSessionModel`, the generator now also checks the **containing type** (`ChatSession`) for `IPerspectiveFor` implementations, not just sibling types.

### HotChocolate GraphQLName Discovery

**Generator**: `src/Whizbang.Generators/MessageJsonContextGenerator.cs`

**Tests**: Located in `tests/Whizbang.Transports.HotChocolate.Tests/`

**Purpose**: Types with `[GraphQLName]` attribute are discovered for JSON serialization (needed for HotChocolate GraphQL responses).

**Note**: GraphQL-specific tests are in the HotChocolate tests project where the dependency is properly available.

### Array Type Discovery (T[])

**Generator**: `src/Whizbang.Generators/MessageJsonContextGenerator.cs` (`_discoverArrayTypes` method)

**Tests**: `tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs`
- `Generator_MessageWithArrayProperty_DiscoversArrayTypeAsync`
- `Generator_MessageWithNullableElementArray_GeneratesArrayFactoryAsync`
- `Generator_MessageWithCustomTypeArray_GeneratesArrayFactoryAsync`
- `Generator_MessageWithGuidArray_GeneratesArrayFactoryAsync`
- `Generator_MessageWithGenericTypeArray_GeneratesValidIdentifierAsync`

**Purpose**: Automatically discovers and generates `JsonTypeInfo<T[]>` for array types used in message properties.

**Behavior**: When a property has an array type (`T[]`), the generator:
1. Extracts the element type name
2. Normalizes C# keyword aliases (`int` → `System.Int32`)
3. Sanitizes special characters (`<`, `>`, `,`, spaces) to create valid C# identifiers
4. Creates `JsonTypeInfo<T[]>` using `JsonMetadataServices.CreateArrayInfo`

**Supported array types**:
| Property Type | Generated Factory |
|--------------|-------------------|
| `string[]` | `CreateArray_System_String` |
| `int[]` | `CreateArray_System_Int32` |
| `Guid[]` | `CreateArray_System_Guid` |
| `int?[]` | `CreateArray_System_Int32__Nullable` |
| `CustomType[]` | `CreateArray_Namespace_CustomType` |
| `Dictionary<string, string>[]` | `CreateArray_System_Collections_Generic_Dictionary_string__string_` |

### Core Interface Types (IMessage, IEvent, ICommand)

**Location**: `src/Whizbang.Core/Generated/InfrastructureJsonContext.cs`

**Purpose**: Provides `JsonTypeInfo` for core Whizbang message interfaces and their array/list forms.

**Registered types**:
| Type | Purpose |
|------|---------|
| `IMessage`, `IMessage[]`, `List<IMessage>` | Base message interface |
| `IEvent`, `IEvent[]`, `List<IEvent>` | Event marker interface |
| `ICommand`, `ICommand[]`, `List<ICommand>` | Command marker interface |

**Why needed**: When a property has type `IEvent[]` (e.g., batch processing), the generator needs to resolve `JsonTypeInfo<IEvent>` for the array element type. These interface types are registered in `InfrastructureJsonContext` for use across the resolver chain.

### Primitive Type Handling in GetOrCreateTypeInfo

**Location**: `src/Whizbang.Generators/Templates/Snippets/JsonContextSnippets.cs` (`HELPER_GET_OR_CREATE_TYPE_INFO` region)

**Handled types** (with AOT-compatible `JsonMetadataServices` converters):
- `string`, `int`, `long`, `bool`, `DateTime`, `Guid`, `decimal`, `double`, `float`
- `byte`, `sbyte`, `short`, `ushort`, `uint`, `ulong`, `char`
- `DateTimeOffset` → Uses `LenientDateTimeOffsetConverter`

**Circular reference detection**: Uses `[ThreadStatic] HashSet<Type>` to detect and break circular type dependencies.

---

## Database Platform Considerations

### PostgreSQL

| Feature | Handling |
|---------|----------|
| `timestamptz` in JSONB | May lack timezone offset → `LenientDateTimeOffsetConverter` assumes UTC |
| `-infinity` timestamp | Maps to `DateTimeOffset.MinValue` |
| `infinity` timestamp | Maps to `DateTimeOffset.MaxValue` |
| JSONB storage | Polymorphic models use `Property().HasColumnType("jsonb")` |

### SQL Server (Future)

| Feature | Expected Handling |
|---------|-------------------|
| `datetime2` | TBD - may need specific handling |
| JSON columns | TBD - `OPENJSON` behavior |

### MySQL (Future)

| Feature | Expected Handling |
|---------|-------------------|
| `DATETIME` | TBD - may need specific handling |
| JSON columns | TBD |

---

## Troubleshooting Guide

### "JsonTypeInfo metadata for type 'X' was not provided"

**Cause**: The type wasn't discovered by the generator or doesn't have a factory.

**Check**:
1. Is it a nested type? → Verify `_tryGetPublicTypeSymbol` handles the CLR name format
2. Is it a nullable enum? → Generator should create both versions automatically
3. Is it a custom type? → Needs `[WhizbangSerializable]` or be reachable from a message property

### "Unable to parse DateTimeOffset from value: X"

**Cause**: `LenientDateTimeOffsetConverter` doesn't handle this format.

**Check**:
1. What's the actual value? Add handling to `LenientDateTimeOffsetConverter`
2. Which database? May need database-specific handling
3. Add a test case to `LenientDateTimeOffsetConverterTests.cs`

### "Circular type reference detected"

**Cause**: Type A has property of type B, type B has property of type A.

**Solution**: Use `[JsonIgnore]` on one property to break the cycle, or use a custom `JsonConverter`.

---

## Adding New Custom Handling

1. **Create converter** in `src/Whizbang.Core/Serialization/`
2. **Add tests** in `tests/Whizbang.Core.Tests/Serialization/`
3. **Update generator** if needed (snippets in `JsonContextSnippets.cs`)
4. **Update this document** with the new handling
5. **Link tests** using `<tests>` tags in code

---

## Related Files

| File | Purpose |
|------|---------|
| `src/Whizbang.Core/Serialization/LenientDateTimeOffsetConverter.cs` | DateTimeOffset edge case handling |
| `src/Whizbang.Core/Serialization/JsonContextRegistry.cs` | Central registry for JSON contexts |
| `src/Whizbang.Core/Generated/InfrastructureJsonContext.cs` | Core Whizbang types including interfaces |
| `src/Whizbang.Generators/MessageJsonContextGenerator.cs` | Generates JsonTypeInfo factories |
| `src/Whizbang.Generators/Templates/Snippets/JsonContextSnippets.cs` | Code templates for generation |
| `src/Whizbang.Generators/ArrayTypeInfo.cs` | Value type for discovered array types |
| `src/Whizbang.Generators/ListTypeInfo.cs` | Value type for discovered List types |

---

**Last Updated**: 2025-02-25
