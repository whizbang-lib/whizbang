# Performance Principles

**Critical performance patterns for Roslyn incremental source generators**

This document explains the performance-critical patterns that MUST be followed when writing source generators. Ignoring these principles can result in 50-100x slower compilation.

---

## Table of Contents

1. [Overview](#overview)
2. [Syntactic Filtering First](#syntactic-filtering-first)
3. [Value Type Records for Caching](#value-type-records-for-caching)
4. [Early Null Returns](#early-null-returns)
5. [Static Methods Where Possible](#static-methods-where-possible)
6. [Keeping Output Cached Across Edits](#keeping-output-cached-across-edits)
7. [Performance Impact Summary](#performance-impact-summary)

---

## Overview

**The Four Critical Performance Principles**:

1. **Syntactic Filtering First** - Filter out 95%+ of nodes before semantic analysis
2. **Value Type Records** - Use `sealed record` for all cached data
3. **Early Null Returns** - Exit transform methods as soon as you know node doesn't match
4. **Static Methods** - Use `static` for predicates and transforms where possible

**Failure to follow these principles results in**:
- 50-100x slower compilation
- Poor incremental build performance
- Always-invalidated caches
- Unnecessary allocations

---

## Syntactic Filtering First

### The Golden Rule

> **Filter out 95%+ of nodes before semantic analysis**

**Semantic analysis is EXPENSIVE**. Syntactic checks are CHEAP.

### ✅ CORRECT: Cheap Syntactic Check First

```csharp
var candidates = context.SyntaxProvider.CreateSyntaxProvider(
    // Predicate: SYNTACTIC check only
    predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },

    // Transform: Semantic analysis only on filtered nodes
    transform: static (ctx, ct) => PerformSemanticAnalysis(ctx, ct)
);
```

**What makes this fast?**
- `node is ClassDeclarationSyntax` - Simple type check (microseconds)
- `BaseList.Types.Count: > 0` - Property access (microseconds)
- Filters out ~99% of syntax nodes (comments, methods, properties, etc.)
- Only classes with base types reach semantic analysis

---

### ❌ WRONG: No Filtering or Semantic Analysis in Predicate

```csharp
// ❌ WRONG: No filtering - analyzes EVERYTHING
var candidates = context.SyntaxProvider.CreateSyntaxProvider(
    predicate: static (node, _) => true,  // Analyzes ALL nodes!
    transform: static (ctx, ct) => PerformSemanticAnalysis(ctx, ct)
);

// ❌ WRONG: Semantic analysis in predicate
var candidates = context.SyntaxProvider.CreateSyntaxProvider(
    predicate: (node, _) => {
        var symbol = semanticModel.GetSymbolInfo(node);  // VERY EXPENSIVE!
        return symbol.Symbol is IMethodSymbol;
    },
    transform: static (ctx, ct) => ExtractInfo(ctx, ct)
);
```

**Why is this wrong?**
- First example: Semantic analysis runs on EVERY syntax node (methods, properties, comments, whitespace, etc.)
- Second example: Semantic analysis in predicate means it runs on nodes that will be rejected
- Both cause 50-100x slower compilation

---

### Performance Impact

**Good predicate (syntactic filtering)**:
- 10,000 types → ~50-100ms
- Filters to ~100 viable candidates → Fast semantic analysis

**Bad predicate (no filtering or semantic in predicate)**:
- 10,000 types → ~5,000-10,000ms
- Semantic analysis on all 10,000 nodes → 100x slower!

**Real-world example**:
```
Good: Compilation in 200ms
Bad:  Compilation in 15,000ms (75x slower!)
```

---

### Good Syntactic Predicates

```csharp
// Classes with base types (receptors, interfaces, etc.)
predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 }

// Records with base types (messages, value objects)
predicate: static (node, _) => node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 }

// Classes with attributes
predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 }

// Method invocations (dispatcher calls, etc.)
predicate: static (node, _) => node is InvocationExpressionSyntax

// Properties with attributes
predicate: static (node, _) => node is PropertyDeclarationSyntax { AttributeLists.Count: > 0 }
```

**All of these**:
- Use simple type checks
- Access cheap properties
- Filter out 95%+ of nodes
- Take microseconds to execute

---

## Value Type Records for Caching

### The Critical Pattern

**Incremental generator caching depends on VALUE EQUALITY**.

### ✅ CORRECT: Sealed Record with Value Equality

```csharp
internal sealed record ReceptorInfo(
    string ClassName,
    string MessageType,
    string ResponseType
);
```

**Why this works**:
- `record` types use **structural equality** (compare field values)
- Incremental generator compares cached data using equality
- If values match, generator skips re-generation
- Result: 0ms on incremental builds when nothing changed

---

### ❌ WRONG: Class with Reference Equality

```csharp
internal class ReceptorInfo {
    public string ClassName { get; set; }
    public string MessageType { get; set; }
    public string ResponseType { get; set; }
}
```

**Why this is wrong**:
- Classes use **reference equality** (compare object identity)
- Each compilation creates new instances
- New instances ≠ old instances (even if fields match)
- Cache ALWAYS invalidates
- Generator ALWAYS re-runs

---

### Performance Impact

**Record (value equality)**:
- First build: 100ms
- Incremental build (no changes): **0ms** ✅ (cached)
- Incremental build (changes): 100ms

**Class (reference equality)**:
- First build: 100ms
- Incremental build (no changes): **100ms** ❌ (never cached)
- Incremental build (changes): 100ms

**Result**: Classes add 50-200ms overhead to EVERY build, even when nothing changed.

---

### How Caching Works

```csharp
// Incremental generator caching relies on equality checks
var cached = previousCompilation.Get<ReceptorInfo>();
var current = currentCompilation.Get<ReceptorInfo>();

// With record (value equality):
if (cached == current) {
    // ✅ Values match - skip re-generation (0ms)
    return cached;
}

// With class (reference equality):
if (cached == current) {
    // ❌ Always false (different object instances)
    // Generator always re-runs (50-200ms overhead)
}
```

---

## Early Null Returns

### The Principle

> **Return `null` as soon as you know the node doesn't match**

Don't perform expensive operations on nodes that will ultimately be rejected.

### ✅ CORRECT: Early Null Returns

```csharp
private static ReceptorInfo? ExtractReceptorInfo(
    GeneratorSyntaxContext context,
    CancellationToken ct) {

  var classSymbol = context.SemanticModel.GetDeclaredSymbol(context.Node, ct);
  if (classSymbol is null) {
    return null;  // ✅ Early exit - no point continuing
  }

  var receptorInterface = classSymbol.AllInterfaces
      .FirstOrDefault(i => i.Name == "IReceptor");
  if (receptorInterface is null) {
    return null;  // ✅ Early exit - not a receptor
  }

  var typeArgs = receptorInterface.TypeArguments;
  if (typeArgs.Length != 2) {
    return null;  // ✅ Early exit - wrong type arguments
  }

  // Only reach here if we have a valid receptor
  return new ReceptorInfo(
      classSymbol.ToDisplayString(),
      typeArgs[0].ToDisplayString(),
      typeArgs[1].ToDisplayString()
  );
}
```

**Benefits**:
- Exits immediately when node doesn't match
- Avoids expensive operations on invalid nodes
- Clear, readable flow
- Performance: Only valid nodes pay full cost

---

### ❌ WRONG: No Early Returns

```csharp
private static ReceptorInfo? ExtractReceptorInfo(
    GeneratorSyntaxContext context,
    CancellationToken ct) {

  var classSymbol = context.SemanticModel.GetDeclaredSymbol(context.Node, ct);
  var receptorInterface = classSymbol?.AllInterfaces.FirstOrDefault(i => i.Name == "IReceptor");
  var typeArgs = receptorInterface?.TypeArguments;

  // Performs expensive operations even if classSymbol is null
  if (classSymbol is null || receptorInterface is null || typeArgs?.Length != 2) {
    return null;
  }

  return new ReceptorInfo(
      classSymbol.ToDisplayString(),
      typeArgs[0].ToDisplayString(),
      typeArgs[1].ToDisplayString()
  );
}
```

**Why this is worse**:
- Performs expensive operations even when they'll be discarded
- Less readable (complex conditional)
- Null-conditional operators (`?.`) hide the flow

---

## Static Methods Where Possible

### Why Static?

**Compiler can optimize better**:
- No closure allocations
- No `this` pointer passing
- Clear that method has no side effects
- JIT can inline more aggressively

### ✅ CORRECT: Static Methods

```csharp
var candidates = context.SyntaxProvider.CreateSyntaxProvider(
    // Static predicate
    predicate: static (node, _) => node is ClassDeclarationSyntax,

    // Static transform
    transform: static (ctx, ct) => ExtractInfo(ctx, ct)
);

// Static helper
private static ReceptorInfo? ExtractInfo(
    GeneratorSyntaxContext ctx,
    CancellationToken ct) {
  // No access to instance state
}
```

**Benefits**:
- No allocations for closures
- Compiler can optimize better
- Clear that methods are pure
- Faster execution

---

### ⚠️ ACCEPTABLE: Non-Static When Necessary

```csharp
// ⚠️ ACCEPTABLE: Non-static if you need generator state
public class MyGenerator : IIncrementalGenerator {
  private const string TARGET_ATTRIBUTE = "MyAttribute";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var candidates = context.SyntaxProvider.CreateSyntaxProvider(
        // Can't be static - needs TARGET_ATTRIBUTE constant
        predicate: (node, _) => HasAttribute(node, TARGET_ATTRIBUTE),
        transform: static (ctx, ct) => ExtractInfo(ctx, ct)
    );
  }

  private bool HasAttribute(SyntaxNode node, string attributeName) {
    // Needs instance access to constants or fields
  }
}
```

**When non-static is acceptable**:
- Need access to generator constants
- Need access to shared configuration
- Performance impact is minimal

**Rule**: Use `static` unless you have a clear reason not to.

---

## Keeping Output Cached Across Edits

### The Rule

> **An edit that changes nothing a generator reads must leave every output step Cached or Unchanged.**

A generator whose output regenerates on every keystroke costs the consumer in the IDE, on every incremental
build, and in every project that depends on the generated code. `tests/Whizbang.Generators.Tests/BuildCost/GeneratorCachingTests.cs`
runs every generator in the package over a consumer-shaped corpus, applies an unrelated edit (an ordinary
method body, a comment, a receptor body), and fails if any tracked output step is `New` or `Modified`.

Three things defeat caching. Each was found in this package and fixed (#1609).

### 1. Never combine `CompilationProvider` directly

Every edit produces a new `Compilation`, so anything combined with it re-runs and, because a `Compilation`
compares by reference, reaches the output as `Modified`.

```csharp
// ❌ WRONG: the output regenerates on every edit
var data = receptors.Collect().Combine(context.CompilationProvider);

// ✅ CORRECT: project the one fact you need to a value first
var assemblyName = context.CompilationProvider.Select(static (c, _) => c.AssemblyName);
var data = receptors.Collect().Combine(assemblyName);
```

When the output genuinely needs compilation-wide facts (the concrete subtypes of a polymorphic message, the
nested types of a message's properties, referenced-assembly versions), read them in a `Select` step that
returns a **value-equatable model**, and generate from the model:

```csharp
var facts = receptors.Collect()
    .Combine(context.CompilationProvider)
    .Select(static (data, _) => ReceptorCompilationFacts.From(data.Right, data.Left));
context.RegisterSourceOutput(receptors.Collect().Combine(facts), ...);
```

The `Select` still runs on every edit, but its result compares equal while the facts do, so the output step is
`Cached`. Never pass a `Compilation`, `ISymbol` or `SemanticModel` into `RegisterSourceOutput`.
`ReceptorDiscoveryGenerator` (`ReceptorCompilationFacts`), `MessageJsonContextGenerator` (`JsonContextAnalysis`)
and `MessageRegistryGenerator` (referenced Whizbang packages) are the worked examples.

### 2. Models hold no arrays, `ImmutableArray`, `List`, symbols or syntax

A `record` compares member by member, but an array, `ImmutableArray<T>` or `List<T>` member compares **by
reference**, so a model holding one is unequal to its predecessor on every run. A syntax provider's semantic
transform runs again against each new compilation, so a transform that returns such a model regenerates the
output even when the edited file is unrelated.

```csharp
// ❌ WRONG: never equal across runs
internal sealed record ReceptorInfo(string ClassName, string[] LifecycleStages);

// ✅ CORRECT: EquatableArray<T> compares by its elements
internal sealed record ReceptorInfo(string ClassName, EquatableArray<string> LifecycleStages);
```

`EquatableArray<T>` (`Whizbang.Generators.Shared.Utilities`) converts implicitly from `T[]`, is built by a
collection expression (`[.. items]`), and reads like an array (`Length`, indexer, `foreach`, LINQ), so code that
fills or reads a model rarely changes. It is a class, so a member that may be absent stays nullable
(`EquatableArray<string>? MemberDefaults`). The same applies to a transform's own return type: return
`EquatableArray<T>?`, not `T[]?` or `ImmutableArray<T>?`, when the result is `Collect()`ed.

A `Location` is acceptable only on the rare model that reports a diagnostic: it compares by syntax tree and
span, so it stays equal while its file is unchanged.

### 3. Prefer `ForAttributeWithMetadataName` when an attribute is the trigger

`ForAttributeWithMetadataName` finds its candidates from a per-tree index of attribute names that the
compiler keeps incrementally, so an edit re-scans only the edited tree, where a `CreateSyntaxProvider`
predicate is evaluated against the nodes of every changed tree by hand. Both still run their semantic transform
against the new compilation on every edit, so rule 2 applies to both: the transform's result must compare equal.
When the trigger is an attribute, use `ForAttributeWithMetadataName` (as the saga pipelines do). When it is an
interface or base type, `CreateSyntaxProvider` with a syntactic predicate is the only option.

### How to Diagnose

Run the driver with `new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true)`,
apply an edit, run again, and read `GetRunResult().Results[i].TrackedOutputSteps`. For a step that is `New` or
`Modified`, walk `step.Inputs` back to the first input that is not `Cached`/`Unchanged`; its output value's type
names the model that lacks value equality. `WithTrackingName("...")` on a step makes it appear in
`TrackedSteps` by name.

---

## Performance Impact Summary

### Good Practices vs. Bad Practices

| Practice | Good | Bad | Impact |
|----------|------|-----|--------|
| **Predicate Filtering** | Syntactic only | Semantic analysis | **100x slower** |
| **Cached Data** | Sealed record | Class | **Never caches** |
| **Transform Flow** | Early null returns | Complex conditionals | **Slower** |
| **Method Type** | Static | Non-static | **Allocations** |

---

### Real-World Performance

**Following ALL principles** (good):
```
First build: 200ms
Incremental build (no changes): 0ms ✅
Incremental build (receptor changed): 50ms
```

**Violating ALL principles** (bad):
```
First build: 15,000ms (75x slower!)
Incremental build (no changes): 5,000ms ❌ (never cached)
Incremental build (receptor changed): 10,000ms
```

**Violating ONE principle** (syntactic filtering):
```
First build: 5,000ms (25x slower)
Incremental build (no changes): 0ms (cached)
Incremental build (receptor changed): 5,000ms
```

---

## Checklist

Before claiming generator work complete:

- [ ] Predicate uses syntactic filtering only (no semantic analysis)
- [ ] Predicate filters out 95%+ of nodes
- [ ] Info types are `sealed record` (not classes)
- [ ] Models and transform results hold `EquatableArray<T>`, never `T[]`, `ImmutableArray<T>`, `List<T>`, symbols or syntax
- [ ] `CompilationProvider` is projected with `Select` to a value before any `Combine`; no `Compilation` reaches `RegisterSourceOutput`
- [ ] Attribute-triggered pipelines use `ForAttributeWithMetadataName`
- [ ] `GeneratorCachingTests` passes (an unrelated edit regenerates nothing)
- [ ] Transform has early null returns
- [ ] Semantic operations pass `CancellationToken`
- [ ] Methods are `static` where possible
- [ ] Tested incremental build performance (should be ~0ms when nothing changes)

---

## See Also

- [value-type-records.md](value-type-records.md) - Detailed info on sealed records for caching
- [common-pitfalls.md](common-pitfalls.md) - Common performance mistakes
- [architecture.md](architecture.md) - Why separate generators improve performance
- [quick-reference.md](quick-reference.md) - Performance checklist
