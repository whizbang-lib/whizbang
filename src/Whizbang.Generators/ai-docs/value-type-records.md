# Value Type Records

**CRITICAL: Sealed records for incremental generator caching**

This document explains the MOST IMPORTANT pattern for incremental source generator performance: using `sealed record` instead of classes for cached data.

**Failure to follow this pattern = generator NEVER caches, 50-200ms overhead on EVERY build.**

---

## Table of Contents

1. [The Critical Pattern](#the-critical-pattern)
2. [Why This Matters](#why-this-matters)
3. [Requirements](#requirements)
4. [Common Value Types](#common-value-types)
5. [Working with Collections](#working-with-collections)
6. [Performance Impact](#performance-impact)

---

## The Critical Pattern

### ✅ CORRECT: Sealed Record

```csharp
/// <summary>
/// Value type containing information about a discovered receptor.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="ClassName">Fully qualified class name</param>
/// <param name="MessageType">Fully qualified message type</param>
/// <param name="ResponseType">Fully qualified response type</param>
internal sealed record ReceptorInfo(
    string ClassName,
    string MessageType,
    string ResponseType
);
```

---

### ❌ WRONG: Class

```csharp
// ❌ WRONG: Class uses reference equality - breaks caching
internal class ReceptorInfo {
    public string ClassName { get; set; }
    public string MessageType { get; set; }
    public string ResponseType { get; set; }
}
```

---

## Why This Matters

### How Incremental Caching Works

**Incremental generators compare cached data using equality**:

```csharp
// Simplified view of what happens internally
var previousData = cache.Get<ReceptorInfo>();
var currentData = ExtractReceptorInfo(context);

if (previousData == currentData) {
    // ✅ Data unchanged - skip re-generation
    return previousData;
}

// ❌ Data changed - regenerate
GenerateCode(currentData);
```

---

### Record vs. Class Equality

**Record (value equality)**:
```csharp
var info1 = new ReceptorInfo("OrderReceptor", "CreateOrder", "OrderCreated");
var info2 = new ReceptorInfo("OrderReceptor", "CreateOrder", "OrderCreated");

info1 == info2  // ✅ TRUE - compares field values
```

**Class (reference equality)**:
```csharp
var info1 = new ReceptorInfo {
    ClassName = "OrderReceptor",
    MessageType = "CreateOrder",
    ResponseType = "OrderCreated"
};
var info2 = new ReceptorInfo {
    ClassName = "OrderReceptor",
    MessageType = "CreateOrder",
    ResponseType = "OrderCreated"
};

info1 == info2  // ❌ FALSE - different object instances
```

---

### Impact on Caching

**With record (value equality)**:
```
Build 1: Extract ReceptorInfo("OrderReceptor", "CreateOrder", "OrderCreated")
         → Cache: ReceptorInfo("OrderReceptor", "CreateOrder", "OrderCreated")

Build 2: Extract ReceptorInfo("OrderReceptor", "CreateOrder", "OrderCreated")
         → Compare: new == cached? ✅ TRUE (values match)
         → Skip re-generation (0ms)
```

**With class (reference equality)**:
```
Build 1: Extract new ReceptorInfo { ... }
         → Cache: ReceptorInfo@0x1234

Build 2: Extract new ReceptorInfo { ... }  // Different instance!
         → Compare: ReceptorInfo@0x5678 == ReceptorInfo@0x1234? ❌ FALSE
         → ALWAYS regenerates (50-200ms overhead)
```

---

## Requirements

### Requirement 1: Must Be `record`

```csharp
// ✅ CORRECT
internal sealed record ReceptorInfo(...)

// ❌ WRONG
internal sealed class ReceptorInfo { ... }
internal struct ReceptorInfo { ... }  // Don't use struct either
```

**Why `record`?**
- Uses value equality by default
- Structural comparison (compares all fields)
- Immutable by design
- Concise syntax

---

### Requirement 2: Must Be `sealed`

```csharp
// ✅ CORRECT
internal sealed record ReceptorInfo(...)

// ❌ WRONG
internal record ReceptorInfo(...)  // Not sealed
```

**Why `sealed`?**
- Performance optimization
- Prevents inheritance (clearer semantics)
- Compiler can optimize better
- Matches incremental generator best practices

---

### Requirement 3: Use Primary Constructor

```csharp
// ✅ CORRECT: Primary constructor
internal sealed record ReceptorInfo(
    string ClassName,
    string MessageType,
    string ResponseType
);

// ⚠️ LESS OPTIMAL: Traditional properties
internal sealed record ReceptorInfo {
    public string ClassName { get; init; }
    public string MessageType { get; init; }
    public string ResponseType { get; init; }
}
```

**Why primary constructor?**
- More concise
- Immutable by default
- Clear intent
- Less code to maintain

---

### Requirement 4: Include XML Documentation

```csharp
/// <summary>
/// Value type containing information about a discovered receptor.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="ClassName">Fully qualified class name</param>
/// <param name="MessageType">Fully qualified message type name</param>
/// <param name="ResponseType">Fully qualified response type name</param>
internal sealed record ReceptorInfo(
    string ClassName,
    string MessageType,
    string ResponseType
);
```

**Always include**:
- `<summary>` - Purpose of the record
- Mention "value equality" and "incremental generator performance"
- `<param>` for each parameter - What it represents

---

### Requirement 5: Use Fully Qualified Names

```csharp
// ✅ CORRECT: Fully qualified names
internal sealed record ReceptorInfo(
    string ClassName,              // "global::MyApp.Receptors.OrderReceptor"
    string MessageType,            // "global::MyApp.Commands.CreateOrder"
    string ResponseType            // "global::MyApp.Events.OrderCreated"
);

// ❌ WRONG: Simple names (ambiguous)
internal sealed record ReceptorInfo(
    string ClassName,              // "OrderReceptor" - Which namespace?
    string MessageType,            // "CreateOrder" - Ambiguous!
    string ResponseType            // "OrderCreated" - Conflicts?
);
```

**Why fully qualified?**
- Avoids namespace ambiguity
- Clear which type is referenced
- No conflicts with same-named types
- Generated code can use without `using` statements

**Get fully qualified name**:
```csharp
var fullyQualifiedName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
// Result: "global::MyApp.Commands.CreateOrder"
```

---

## Common Value Types

### Simple Info Record

```csharp
/// <summary>
/// Information about a discovered receptor.
/// </summary>
internal sealed record ReceptorInfo(
    string ClassName,
    string MessageType,
    string ResponseType
);
```

---

### With Location Info

```csharp
/// <summary>
/// Information about a discovered message type with source location.
/// </summary>
internal sealed record MessageTypeInfo(
    string TypeName,
    bool IsCommand,
    bool IsEvent,
    string FilePath,
    int LineNumber
);
```

---

### With Optional Fields

```csharp
/// <summary>
/// Information about a dispatcher invocation.
/// </summary>
internal sealed record DispatcherLocationInfo(
    string DispatcherType,
    string MessageType,
    string? ResponseType,  // Nullable for Send vs. Publish
    string FilePath,
    int LineNumber
);
```

---

### With Collections

```csharp
/// <summary>
/// Information about a perspective with tracked event types.
/// </summary>
internal sealed record PerspectiveLocationInfo(
    string ClassName,
    string[] EventTypes,  // Arrays work with value equality in records
    string FilePath,
    int LineNumber
);
```

**Arrays in records**:
- `string[]` uses structural equality
- Items compared element-by-element
- Works correctly with incremental caching

---

## Working with Collections

### Arrays Are Supported

```csharp
internal sealed record Info(
    string Name,
    string[] Items  // ✅ Supported, uses value equality
);

var info1 = new Info("Test", new[] { "A", "B" });
var info2 = new Info("Test", new[] { "A", "B" });

info1 == info2  // ✅ TRUE - array contents compared
```

---

### Lists Are NOT Supported

```csharp
internal sealed record Info(
    string Name,
    List<string> Items  // ❌ WRONG: List uses reference equality
);

var info1 = new Info("Test", new List<string> { "A", "B" });
var info2 = new Info("Test", new List<string> { "A", "B" });

info1 == info2  // ❌ FALSE - List compares by reference
```

**Use arrays instead**:
```csharp
// ✅ CORRECT
internal sealed record Info(
    string Name,
    string[] Items  // Array, not List
);

// Convert from IEnumerable
var items = someEnumerable.ToArray();  // Not ToList()
```

---

### ImmutableArray Is Supported

```csharp
using System.Collections.Immutable;

internal sealed record Info(
    string Name,
    ImmutableArray<string> Items  // ✅ Supported, uses value equality
);
```

**ImmutableArray vs. Array**:
- Both support value equality
- ImmutableArray is more explicit about immutability
- Array is simpler for source generators
- **Prefer array** for simplicity unless ImmutableArray is needed elsewhere

---

## Performance Impact

### Record vs. Class - Real Numbers

**Scenario**: Generator that discovers 10 receptors

**With record (value equality)**:
- First build: 100ms
- Incremental build (no changes): **0ms** ✅
- Incremental build (1 receptor changed): 10ms (only changed receptor)

**With class (reference equality)**:
- First build: 100ms
- Incremental build (no changes): **100ms** ❌
- Incremental build (1 receptor changed): 100ms (all receptors)

---

### Performance Over Time

**10 incremental builds (no changes)**:

| Approach | Total Time |
|----------|-----------|
| Record | 0ms |
| Class | 1000ms |

**100 incremental builds (no changes)**:

| Approach | Total Time |
|----------|-----------|
| Record | 0ms |
| Class | 10,000ms (10 seconds!) |

**Result**: Classes waste SECONDS of developer time over a typical work session.

---

## Checklist

Before creating info records for generators:

- [ ] Type is `sealed record` (not class, not struct)
- [ ] Uses primary constructor syntax
- [ ] Includes XML documentation (`<summary>`, `<param>`)
- [ ] Mentions "value equality" in documentation
- [ ] Uses fully qualified type names (avoid ambiguity)
- [ ] Collections use arrays (not List, not IEnumerable)
- [ ] All fields are immutable (no mutable properties)

---

## See Also

- [performance-principles.md](performance-principles.md) - Overall performance patterns
- [architecture.md](architecture.md) - Why value equality matters for caching
- [common-pitfalls.md](common-pitfalls.md) - Pitfall #1: Using classes instead of records
