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
6. [Performance Impact Summary](#performance-impact-summary)

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
