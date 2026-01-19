# Architecture - Multiple Independent Generators Pattern

**Core architectural pattern for Whizbang source generators**

This document explains why Whizbang uses multiple independent generators instead of a single monolithic generator, and the benefits this architecture provides.

---

## Table of Contents

1. [Overview](#overview)
2. [Multiple Independent Generators Pattern](#multiple-independent-generators-pattern)
3. [IIncrementalGenerator Interface](#iincrementalgenerator-interface)
4. [Why Separate Generators?](#why-separate-generators)
5. [Existing Generators](#existing-generators)

---

## Overview

Whizbang uses **Roslyn incremental source generators** to achieve:
- **Zero reflection** - All type discovery at compile-time
- **AOT compatibility** - Native AOT from day one
- **Type safety** - Compile-time validation
- **Performance** - Optimal caching with incremental generation

The architecture is based on **multiple independent generators** rather than a single monolithic generator. This design choice is fundamental to achieving optimal incremental build performance.

---

## Multiple Independent Generators Pattern

### Conceptual Structure

```
Compilation
├── ReceptorDiscoveryGenerator
│   ├── Cache: ReceptorInfo records
│   └── Output: Dispatcher.g.cs, DispatcherRegistrations.g.cs, Diagnostics.g.cs
├── MessageRegistryGenerator
│   ├── Cache: MessageTypeInfo, DispatcherLocationInfo, etc.
│   └── Output: MessageRegistry.g.cs
└── DiagnosticsGenerator
    ├── Cache: None (post-initialization)
    └── Output: WhizbangDiagnostics.g.cs
```

**Key Principle**: Each generator is **completely independent** with **no cross-dependencies**.

---

## IIncrementalGenerator Interface

### The Required Interface

**ALL generators MUST implement `IIncrementalGenerator`** (not `ISourceGenerator`):

```csharp
[Generator]
public class MyGenerator : IIncrementalGenerator {
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Register pipelines here
  }
}
```

### Why IIncrementalGenerator?

**Value-based caching**:
- Only re-runs when inputs actually change
- Uses structural equality to detect changes
- Requires value-type records (see [value-type-records.md](value-type-records.md))

**Parallel execution**:
- Multiple generators run concurrently
- No blocking or sequential dependencies
- Better utilization of multi-core CPUs

**Minimal overhead**:
- ~0ms on incremental builds when nothing changes
- Only changed pipelines re-execute
- Cached results reused automatically

### ❌ Do NOT Use ISourceGenerator

```csharp
// ❌ WRONG - Legacy interface
[Generator]
public class MyGenerator : ISourceGenerator {
  public void Execute(GeneratorExecutionContext context) {
    // Runs on EVERY build, no caching
  }
}
```

**Why not?**
- No incremental caching
- Runs on every compilation
- 50-200ms overhead on every build
- Poor performance with large codebases

---

## Why Separate Generators?

### Benefit 1: Cache Isolation

**Problem with monolithic generator**:
```
User changes a receptor
  ↓
Entire generator re-runs
  ↓
Message registry recalculated (unnecessary!)
```

**Solution with independent generators**:
```
User changes a receptor
  ↓
ReceptorDiscoveryGenerator re-runs (cache invalidated)
  ↓
MessageRegistryGenerator skipped (cache still valid)
```

**Performance Impact**:
- Monolithic: 100-200ms on every receptor change
- Independent: 20-50ms (only affected generator runs)

---

### Benefit 2: Simpler Predicates

**Each generator has a focused predicate**:

```csharp
// ReceptorDiscoveryGenerator - Only looks for classes with base types
predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 }

// MessageRegistryGenerator - Only looks for records and invocations
predicate: static (node, _) => node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 }
```

**Benefits**:
- Faster filtering (95%+ nodes eliminated early)
- Easier to understand and maintain
- No complex conditional logic

---

### Benefit 3: Single Responsibility

**Each generator has ONE job**:

| Generator | Responsibility |
|-----------|----------------|
| ReceptorDiscoveryGenerator | Discover receptors, generate dispatcher routing |
| MessageRegistryGenerator | Discover messages, dispatchers, receptors for VSCode |
| DiagnosticsGenerator | Generate centralized diagnostics infrastructure |

**Benefits**:
- Easy to reason about
- Focused test coverage
- Simple debugging
- Clear ownership

---

### Benefit 4: Extensibility

**Adding new generators is trivial**:

```csharp
// Add new generator - no changes to existing generators!
[Generator]
public class AggregateIdGenerator : IIncrementalGenerator {
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover aggregates, generate ID types
  }
}
```

**Benefits**:
- No risk of breaking existing generators
- Incremental feature development
- Independent testing
- Parallel development by team members

---

## Existing Generators

### 1. ReceptorDiscoveryGenerator

**Purpose**: Discovers `IReceptor<TMessage, TResponse>` implementations and generates dispatcher routing.

**Inputs**:
- Classes implementing `IReceptor<TMessage, TResponse>`

**Outputs**:
- `Dispatcher.g.cs` - Generated dispatcher with routing logic
- `DispatcherRegistrations.g.cs` - Service registration extensions
- `Diagnostics.g.cs` - Diagnostic reporting

**Cache**: `ReceptorInfo` records (sealed record with value equality)

---

### 2. MessageRegistryGenerator

**Purpose**: Discovers messages, dispatchers, receptors, perspectives for VSCode extension integration.

**Inputs**:
- Record types implementing message contracts
- `IReceptor<TMessage, TResponse>` implementations
- Invocations of dispatcher methods
- Perspective classes

**Outputs**:
- `MessageRegistry.g.cs` - JSON registry for VSCode extension
- Written to `.whizbang/message-registry.json`

**Cache**: `MessageTypeInfo`, `DispatcherLocationInfo`, `ReceptorLocationInfo`, `PerspectiveLocationInfo` records

---

### 3. DiagnosticsGenerator

**Purpose**: Generates centralized diagnostics infrastructure.

**Inputs**:
- None (post-initialization generator)

**Outputs**:
- `WhizbangDiagnostics.g.cs` - Centralized diagnostic infrastructure

**Cache**: None (runs once at start of compilation)

---

## Architecture Principles

### Principle 1: Independence

**NO cross-dependencies between generators**:
- Each generator has isolated cache
- Changing one generator doesn't affect others
- Failures in one generator don't cascade

### Principle 2: Value-Based Caching

**All cached data uses sealed records**:
- Structural equality for comparison
- Incremental generator caching depends on this
- See [value-type-records.md](value-type-records.md)

### Principle 3: Syntactic Filtering First

**Predicates use cheap syntactic checks**:
- Filter out 95%+ of nodes before semantic analysis
- Only perform semantic analysis on viable candidates
- See [performance-principles.md](performance-principles.md)

### Principle 4: Early Null Returns

**Transform methods exit early**:
- Return `null` as soon as you know the node doesn't match
- Avoid expensive semantic operations on invalid nodes
- See [performance-principles.md](performance-principles.md)

---

## Design Decisions

### Why Not a Single Generator?

**Single generator would require**:
- Complex predicate logic (slower filtering)
- Shared cache (invalidates on any change)
- Multiple outputs from single pipeline (harder to maintain)
- Single point of failure

**Multiple generators provide**:
- Simple, focused predicates (faster)
- Isolated caches (better incremental performance)
- Single responsibility (easier to maintain)
- Independent failure domains

### Why Not Per-Feature Generators?

**Too many generators would cause**:
- Overhead from multiple compilations
- Duplicate analysis of same syntax trees
- Complex coordination between generators

**Current design balances**:
- Few enough generators for performance
- Enough separation for cache isolation
- Clear responsibility boundaries

---

## See Also

- [performance-principles.md](performance-principles.md) - Syntactic filtering, caching strategies
- [value-type-records.md](value-type-records.md) - Critical for incremental caching
- [generator-patterns.md](generator-patterns.md) - Implementation patterns
- [common-pitfalls.md](common-pitfalls.md) - Avoid architecture mistakes
