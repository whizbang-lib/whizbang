# Common Pitfalls

**Seven major mistakes that break generator performance and correctness**

This document lists the most common mistakes when writing source generators and how to avoid them.

---

## Table of Contents

1. [Pitfall 1: Using Classes Instead of Records](#pitfall-1-using-classes-instead-of-records)
2. [Pitfall 2: Expensive Predicate](#pitfall-2-expensive-predicate)
3. [Pitfall 3: Non-Static Methods](#pitfall-3-non-static-methods)
4. [Pitfall 4: Forgetting to Filter Nulls](#pitfall-4-forgetting-to-filter-nulls)
5. [Pitfall 5: Incorrect Resource Namespace](#pitfall-5-incorrect-resource-namespace)
6. [Pitfall 6: Modifying Immutable Collections](#pitfall-6-modifying-immutable-collections)
7. [Pitfall 7: Forgetting CancellationToken](#pitfall-7-forgetting-cancellationtoken)
8. [Pitfall 8: Emitting Constant Content Once Per Discovered Type](#pitfall-8-emitting-constant-content-once-per-discovered-type)

---

## Pitfall 1: Using Classes Instead of Records

### ❌ WRONG: Class with Reference Equality

```csharp
internal class ReceptorInfo {
    public string ClassName { get; set; }
    public string MessageType { get; set; }
    public string ResponseType { get; set; }
}
```

---

### ✅ CORRECT: Sealed Record with Value Equality

```csharp
internal sealed record ReceptorInfo(
    string ClassName,
    string MessageType,
    string ResponseType
);
```

---

### Why This Is a Pitfall

**Problem**:
- Classes use reference equality (compare object identity)
- Incremental generator caching compares using equality
- New compilation = new instances = different references
- Cache ALWAYS invalidates
- Generator ALWAYS re-runs

**Performance Impact**:
- Record: 0ms on incremental builds (cached)
- Class: 50-200ms on EVERY build (never cached)

**How to Avoid**:
- ALWAYS use `sealed record` for info types
- NEVER use classes for cached data
- See [value-type-records.md](value-type-records.md)

---

## Pitfall 2: Expensive Predicate

### ❌ WRONG: Semantic Analysis in Predicate

```csharp
// ❌ WRONG: Performs semantic analysis on EVERY node
predicate: (node, _) => {
    var symbol = semanticModel.GetSymbolInfo(node);  // VERY EXPENSIVE!
    return symbol.Symbol is IMethodSymbol;
},
```

---

### ❌ WRONG: No Filtering at All

```csharp
// ❌ WRONG: No filtering - analyzes EVERYTHING
predicate: static (node, _) => true,  // Processes all nodes!
```

---

### ✅ CORRECT: Syntactic Filtering Only

```csharp
// ✅ CORRECT: Cheap syntactic check filters out 95%+ of nodes
predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
```

---

### Why This Is a Pitfall

**Problem**:
- Semantic analysis is 100x slower than syntactic checks
- Bad predicate runs semantic analysis on EVERY syntax node
- 10,000 types = 5,000-10,000ms instead of 50-100ms

**Performance Impact**:
- Good predicate: ~50-100ms on 10,000 types
- Bad predicate: ~5,000-10,000ms on 10,000 types (100x slower!)

**How to Avoid**:
- ONLY use syntactic checks in predicate
- Filter out 95%+ of nodes before semantic analysis
- Perform semantic analysis in transform method
- See [performance-principles.md](performance-principles.md)

---

## Pitfall 3: Non-Static Methods

### ⚠️ LESS OPTIMAL: Non-Static Lambda

```csharp
// ⚠️ LESS OPTIMAL: Non-static lambda captures 'this'
predicate: (node, _) => IsValidNode(node),
transform: (ctx, ct) => ExtractInfo(ctx, ct)
```

---

### ✅ BETTER: Static Methods

```csharp
// ✅ BETTER: Static methods, no closure
predicate: static (node, _) => IsValidNode(node),
transform: static (ctx, ct) => ExtractInfo(ctx, ct)
```

---

### Why This Is a Pitfall

**Problem**:
- Non-static lambdas capture `this`
- Creates closure allocations
- Prevents compiler optimizations
- Slower execution

**Performance Impact**:
- Minor but measurable
- Adds up over many invocations
- Especially bad in tight loops

**How to Avoid**:
- Use `static` keyword on predicates and transforms
- Use static methods for helpers
- Only use non-static when you need instance access

---

## Pitfall 4: Forgetting to Filter Nulls

### ❌ WRONG: Nulls Passed to RegisterSourceOutput

```csharp
var items = context.SyntaxProvider.CreateSyntaxProvider(
    predicate: static (node, _) => node is ClassDeclarationSyntax,
    transform: static (ctx, ct) => ExtractInfo(ctx, ct)  // May return null!
).Collect();  // ❌ Nulls included in collection!

context.RegisterSourceOutput(items, static (ctx, items) => {
    foreach (var item in items) {
        // ❌ NullReferenceException when item is null!
        var name = item.ClassName;
    }
});
```

---

### ✅ CORRECT: Filter Nulls Before Collecting

```csharp
var items = context.SyntaxProvider.CreateSyntaxProvider(
    predicate: static (node, _) => node is ClassDeclarationSyntax,
    transform: static (ctx, ct) => ExtractInfo(ctx, ct)
).Where(static info => info is not null);  // ✅ Filter nulls!

context.RegisterSourceOutput(items.Collect(), static (ctx, items) => {
    foreach (var item in items) {
        // ✅ Safe - nulls filtered out
        var name = item!.ClassName;
    }
});
```

---

### Why This Is a Pitfall

**Problem**:
- Transform methods return `null` for nodes that don't match
- Without filtering, nulls passed to source output
- Results in `NullReferenceException` during generation

**How to Avoid**:
- ALWAYS add `.Where(static info => info is not null)` after transform
- Filter before `.Collect()` or `.Combine()`
- Use null-forgiving operator (`!`) in source output (safe after filtering)

---

## Pitfall 5: Incorrect Resource Namespace

### ❌ WRONG: Resource Name Doesn't Match Actual Namespace

```csharp
// Template file location:
// Templates/DispatcherTemplate.cs
// Namespace: Whizbang.Generators.Templates

// ❌ WRONG: Incorrect namespace
var template = TemplateUtilities.GetEmbeddedTemplate(
    assembly,
    "DispatcherTemplate.cs",
    "Whizbang.Templates"  // Wrong namespace!
);

// Error: "Template not found"
```

---

### ✅ CORRECT: Match Actual Namespace

```csharp
// Template file location:
// Templates/DispatcherTemplate.cs
// Namespace: Whizbang.Generators.Templates

// ✅ CORRECT: Matches actual namespace
var template = TemplateUtilities.GetEmbeddedTemplate(
    assembly,
    "DispatcherTemplate.cs",
    "Whizbang.Generators.Templates"  // Correct!
);
```

---

### Why This Is a Pitfall

**Problem**:
- Embedded resources named by namespace + filename
- Wrong namespace = resource not found
- Runtime error (not caught at compile-time)
- Confusing error message

**How to Avoid**:
- Check template file namespace carefully
- Use fully qualified namespace
- Test template loading in integration tests

---

## Pitfall 6: Modifying Immutable Collections

### ❌ WRONG: Can't Modify ImmutableArray

```csharp
void ProcessItems(ImmutableArray<ReceptorInfo> items) {
    items.Add(newItem);  // ❌ Compilation error!
    // Error: ImmutableArray<T> does not contain a definition for 'Add'
}
```

---

### ✅ CORRECT: Use Builder or Create New Array

```csharp
// Option 1: Use builder
ImmutableArray<ReceptorInfo> AddItem(
    ImmutableArray<ReceptorInfo> items,
    ReceptorInfo newItem) {

    var builder = items.ToBuilder();
    builder.Add(newItem);
    return builder.ToImmutable();
}

// Option 2: Create new array with concatenation
ImmutableArray<ReceptorInfo> AddItem(
    ImmutableArray<ReceptorInfo> items,
    ReceptorInfo newItem) {

    return items.Add(newItem);  // Returns new array
}
```

---

### Why This Is a Pitfall

**Problem**:
- `ImmutableArray<T>` is immutable (can't be modified)
- Attempt to modify = compilation error
- Easy to forget if coming from mutable collections

**How to Avoid**:
- Remember: ImmutableArray is IMMUTABLE
- Use `.ToBuilder()` for multiple modifications
- Use `.Add()` method (returns new array)
- Consider using regular arrays if you need mutability

---

## Pitfall 7: Forgetting CancellationToken

### ❌ WRONG: Ignores Cancellation

```csharp
private static ReceptorInfo? ExtractInfo(
    GeneratorSyntaxContext ctx,
    CancellationToken ct) {

    // ❌ WRONG: Doesn't pass cancellation token
    var symbol = ctx.SemanticModel.GetDeclaredSymbol(ctx.Node);

    // More expensive operations without cancellation...
}
```

---

### ✅ CORRECT: Pass Cancellation Token

```csharp
private static ReceptorInfo? ExtractInfo(
    GeneratorSyntaxContext ctx,
    CancellationToken ct) {

    // ✅ CORRECT: Passes cancellation token
    var symbol = ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct);

    // Other operations also pass ct...
    var interfaces = symbol?.AllInterfaces;
    // etc.
}
```

---

### Why This Is a Pitfall

**Problem**:
- Without cancellation token, generator can't be canceled
- User closes IDE or stops build = generator keeps running
- Wastes CPU resources
- Delays IDE responsiveness
- Poor user experience

**How to Avoid**:
- ALWAYS pass `CancellationToken` to semantic operations
- Pass `ct` to all Roslyn API methods that accept it
- Check `ct.IsCancellationRequested` in long loops

---

## Pitfall 8: Emitting Constant Content Once Per Discovered Type

### The Problem

A generator that emits one file per discovered type (one per DbContext, one per perspective) and puts content in it
that does not depend on that type multiplies that content by the number of types. Nothing fails: the output compiles,
the tests pass, and the compiler's memory grows with (types x content) until a build runner runs out.

This is what #2388 was. Every DbContext's schema extensions embedded the library's whole migration set, about 9 MB
of SQL that is the same for every context. A test assembly with 13 contexts compiled 120 MB of generated source,
nine times its hand-written code, and its compile peaked at 18.9 GB, past the 16 GB of a hosted CI runner, where the
build hangs instead of failing. The assembly barely grew (identical UTF-8 literals are stored once), so its size gave
no warning.

### How to Avoid

- Emit content that does not depend on the discovered type **once per compilation**, in a class of its own, and have
  each per-type file reference it by its fully qualified name (`WhizbangMigrationScripts`, emitted by
  `EFCoreServiceRegistrationGenerator`, is the pattern).
- When the shared content is logic that each per-type file runs over its own types, emit it once as a generic base
  class and have each per-type file derive from it, supplying only what varies as traits and overrides. The perspective
  runners are the pattern (#2401): each runner repeated about 190 KB of runner logic, and the generator's time went
  into copying that template once per perspective (#1689). `PerspectiveRunnerBaseTemplate.cs` is now emitted once per
  assembly as `PerspectiveRunnerBase<TPerspective, TModel>`, and a runner (`PerspectiveRunnerClassTemplate.cs`) is
  about 4 KB. `PerspectiveRunnerGeneratedSizeTests` pins both halves: one base per assembly, and a runner holding only
  its perspective's parts.
- When you look at what a generator emits, compare the generated source to the hand-written source
  (`-p:EmitCompilerGeneratedFiles=true`, then size `obj/generated/<generator>`). Generated output several times the
  size of the code it serves is the signal.
- CI runs every full-solution build under `.github/scripts/Invoke-BuildWithinMemory.ps1`, which fails a build whose
  memory passes 13 GB, so growth is caught as a failed check rather than a hung runner.
- Measure a single project in isolation too, but do not trust it alone: the compiler server is shared by every
  project in a build and, under server GC, holds what one compile grew to while it runs the next. Removing the copies
  took the EF Core test project from 18.9 GB to 7.9 GB on its own, yet the full build only from 20.6 GB to 19.3 GB
  until the build steps set `DOTNET_GCConserveMemory=7` (12.8 GB).

---

## Quick Pitfall Reference

| Pitfall | Impact | Solution |
|---------|--------|----------|
| **Classes instead of records** | 50-200ms overhead every build | Use `sealed record` |
| **Expensive predicate** | 100x slower compilation | Syntactic filtering only |
| **Non-static methods** | Allocations, missed optimizations | Use `static` keyword |
| **No null filtering** | NullReferenceException | `.Where(static info => info is not null)` |
| **Wrong resource namespace** | "Template not found" error | Match actual namespace |
| **Modifying ImmutableArray** | Compilation error | Use `.ToBuilder()` or `.Add()` |
| **Forgetting CancellationToken** | Can't cancel generator | Pass `ct` to all operations |
| **Constant content per discovered type** | Compile memory grows with types x content (#2388) | Emit it once per compilation |

---

## Checklist

Before claiming generator work complete, verify you avoided all pitfalls:

- [ ] Info types are `sealed record` (not classes)
- [ ] Predicate uses syntactic filtering only (no semantic analysis)
- [ ] Methods are `static` where possible
- [ ] Nulls filtered with `.Where(static info => info is not null)`
- [ ] Template namespaces match actual file namespaces
- [ ] ImmutableArray used correctly (`.ToBuilder()` or `.Add()`)
- [ ] `CancellationToken` passed to all semantic operations
- [ ] Nothing that is the same for every discovered type is emitted once per type

---

## See Also

- [value-type-records.md](value-type-records.md) - Pitfall #1 in detail
- [performance-principles.md](performance-principles.md) - Pitfall #2 in detail
- [template-system.md](template-system.md) - Pitfall #5 in detail
- [quick-reference.md](quick-reference.md) - Complete correct example
