# Generator Patterns

**Three core implementation patterns for incremental source generators**

This document describes the three fundamental patterns used when implementing incremental source generators, with examples from Whizbang generators.

---

## Table of Contents

1. [Overview](#overview)
2. [Pattern 1: Single Discovery Pipeline](#pattern-1-single-discovery-pipeline)
3. [Pattern 2: Multiple Parallel Pipelines](#pattern-2-multiple-parallel-pipelines)
4. [Pattern 3: Post-Initialization Output](#pattern-3-post-initialization-output)
5. [Choosing the Right Pattern](#choosing-the-right-pattern)

---

## Overview

Incremental source generators follow one of three fundamental patterns:

1. **Single Discovery Pipeline** - Discovering one type of construct
2. **Multiple Parallel Pipelines** - Discovering multiple independent constructs
3. **Post-Initialization Output** - Generating static infrastructure (no discovery)

**Choose the pattern that matches your use case.**

---

## Pattern 1: Single Discovery Pipeline

### When to Use

- Discovering **one type** of construct (receptors, attributes, interfaces)
- All discovered items processed together
- Single output or multiple outputs from same data

### Example: ReceptorDiscoveryGenerator

**Use Case**: Discover `IReceptor<TMessage, TResponse>` implementations and generate dispatcher routing.

```csharp
[Generator]
public class ReceptorDiscoveryGenerator : IIncrementalGenerator {
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Single pipeline for discovering receptors
    var receptors = context.SyntaxProvider.CreateSyntaxProvider(
        // Predicate: Filter to classes with base types
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },

        // Transform: Extract receptor info
        transform: static (ctx, ct) => ExtractReceptorInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Collect all receptors and generate outputs
    context.RegisterSourceOutput(
        receptors.Collect(),
        static (ctx, receptors) => GenerateDispatcherCode(ctx, receptors!)
    );
  }

  private static ReceptorInfo? ExtractReceptorInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {
    // Extract info, return null if not a receptor
    // See performance-principles.md for early null return pattern
  }

  private static void GenerateDispatcherCode(
      SourceProductionContext ctx,
      ImmutableArray<ReceptorInfo> receptors) {
    // Generate multiple files from same data:
    // - Dispatcher.g.cs
    // - DispatcherRegistrations.g.cs
    // - Diagnostics.g.cs
  }
}
```

---

### Characteristics

**Pipeline Structure**:
- One `CreateSyntaxProvider` call
- One `RegisterSourceOutput` call
- Collect all results with `.Collect()`
- Generate multiple files from collected data

**Benefits**:
- Simple, straightforward
- Easy to understand
- Good for focused discovery

**When to use**:
- Discovering one type of construct
- All items processed together
- Multiple outputs from same source data

---

### Complete Example

```csharp
// Value type record for caching
internal sealed record AttributedClassInfo(
    string ClassName,
    string Namespace
);

[Generator]
public class MyAttributeGenerator : IIncrementalGenerator {
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover classes with [MyAttribute]
    var attributedClasses = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) =>
            node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
        transform: static (ctx, ct) => ExtractAttributedClass(ctx, ct)
    ).Where(static info => info is not null);

    // Generate code from all attributed classes
    context.RegisterSourceOutput(
        attributedClasses.Collect(),
        static (ctx, classes) => GenerateRegistration(ctx, classes!)
    );
  }
}
```

---

## Pattern 2: Multiple Parallel Pipelines

### When to Use

- Discovering **multiple types** of constructs
- Each type has different predicate requirements
- All types combined into single output

### Example: MessageRegistryGenerator

**Use Case**: Discover messages, dispatchers, receptors, perspectives for VSCode extension.

```csharp
[Generator]
public class MessageRegistryGenerator : IIncrementalGenerator {
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Pipeline 1: Discover message types (records with base types)
    var messageTypes = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) =>
            node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractMessageType(ctx, ct)
    ).Where(static info => info is not null);

    // Pipeline 2: Discover dispatcher invocations
    var dispatchers = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is InvocationExpressionSyntax,
        transform: static (ctx, ct) => ExtractDispatcher(ctx, ct)
    ).Where(static info => info is not null);

    // Pipeline 3: Discover receptor implementations
    var receptors = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) =>
            node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractReceptor(ctx, ct)
    ).Where(static info => info is not null);

    // Pipeline 4: Discover perspective implementations
    var perspectives = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) =>
            node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
        transform: static (ctx, ct) => ExtractPerspective(ctx, ct)
    ).Where(static info => info is not null);

    // Combine all pipelines at the end
    var allData = messageTypes.Collect()
        .Combine(dispatchers.Collect())
        .Combine(receptors.Collect())
        .Combine(perspectives.Collect());

    // Single output from combined data
    context.RegisterSourceOutput(
        allData,
        static (ctx, data) => GenerateRegistry(ctx, data)
    );
  }
}
```

---

### Characteristics

**Pipeline Structure**:
- Multiple independent `CreateSyntaxProvider` calls
- Each pipeline has optimized predicate for its target
- Combine results with `.Combine()`
- Single `RegisterSourceOutput` with combined data

**Benefits**:
- Each pipeline has focused, optimized predicate
- Parallel discovery (concurrent execution)
- Independent caching per pipeline
- Changing one type doesn't invalidate others

**When to use**:
- Discovering multiple types of constructs
- Each type needs different predicate
- All combined into single output

---

### Combining Pipeline Results

```csharp
// Combine two pipelines
var combined = pipeline1.Collect().Combine(pipeline2.Collect());

// Combine three pipelines
var combined = pipeline1.Collect()
    .Combine(pipeline2.Collect())
    .Combine(pipeline3.Collect());

// Combine four pipelines (nested tuples)
var combined = pipeline1.Collect()
    .Combine(pipeline2.Collect())
    .Combine(pipeline3.Collect())
    .Combine(pipeline4.Collect());

// Access combined data in RegisterSourceOutput
context.RegisterSourceOutput(combined, static (ctx, data) => {
    // Two pipelines: data.Left, data.Right
    var items1 = data.Left;
    var items2 = data.Right;

    // Three pipelines: data.Left.Left, data.Left.Right, data.Right
    var items1 = data.Left.Left;
    var items2 = data.Left.Right;
    var items3 = data.Right;

    // Four pipelines: data.Left.Left.Left, data.Left.Left.Right,
    //                  data.Left.Right, data.Right
    var items1 = data.Left.Left.Left;
    var items2 = data.Left.Left.Right;
    var items3 = data.Left.Right;
    var items4 = data.Right;
});
```

**Note**: `.Combine()` creates nested tuples. Access via `.Left` and `.Right`.

---

## Pattern 3: Post-Initialization Output

### When to Use

- Generating **static infrastructure** (no discovery needed)
- Code is the same for every compilation
- No syntax tree analysis required

### Example: DiagnosticsGenerator

**Use Case**: Generate centralized diagnostics infrastructure.

```csharp
[Generator]
public class DiagnosticsGenerator : IIncrementalGenerator {
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Runs once per compilation, no discovery
    context.RegisterPostInitializationOutput(ctx => {
      var source = GenerateWhizbangDiagnostics();
      ctx.AddSource("WhizbangDiagnostics.g.cs", source);
    });
  }

  private static string GenerateWhizbangDiagnostics() {
    // Generate static infrastructure code
    // Same output every time
    return TemplateUtilities.GetEmbeddedTemplate(
        typeof(DiagnosticsGenerator).Assembly,
        "WhizbangDiagnosticsTemplate.cs"
    );
  }
}
```

---

### Characteristics

**Pipeline Structure**:
- No syntax providers
- `RegisterPostInitializationOutput` instead of `RegisterSourceOutput`
- Runs once at start of compilation
- No caching (always runs, but fast)

**Benefits**:
- Simple implementation
- No syntax tree traversal
- Fast execution
- Use for static boilerplate

**When to use**:
- Generating static infrastructure
- No discovery needed
- Same output every compilation

---

### Common Use Cases

**Static infrastructure**:
- Diagnostic descriptors
- Extension method containers
- Shared constants
- Base classes or interfaces

**NOT for**:
- Discovering user code
- Varying output based on code
- Complex analysis

---

## Choosing the Right Pattern

### Decision Tree

```
Is output the same every time?
├─ Yes → Pattern 3: Post-Initialization Output
└─ No → Discovering user code
    │
    ├─ One type of construct?
    │  └─ Yes → Pattern 1: Single Discovery Pipeline
    │
    └─ Multiple types of constructs?
       └─ Yes → Pattern 2: Multiple Parallel Pipelines
```

---

### Pattern Comparison

| Pattern | Use When | Example | Pipelines | Outputs |
|---------|----------|---------|-----------|---------|
| **Single Discovery** | One construct type | ReceptorDiscoveryGenerator | 1 | Multiple from same data |
| **Multiple Parallel** | Multiple construct types | MessageRegistryGenerator | Many | Combined data |
| **Post-Initialization** | Static infrastructure | DiagnosticsGenerator | 0 | Static code |

---

### Performance Considerations

**Single Discovery Pipeline**:
- ✅ Simple predicate (fast)
- ✅ Single cache (optimal)
- ⚠️ Limited to one construct type

**Multiple Parallel Pipelines**:
- ✅ Optimized predicates per type (fast)
- ✅ Independent caches (optimal)
- ✅ Parallel execution
- ⚠️ More complex (nested tuples)

**Post-Initialization Output**:
- ✅ No syntax traversal (fastest)
- ✅ Simple implementation
- ❌ No caching (always runs)
- ⚠️ Limited to static output

---

## Implementation Checklist

### Single Discovery Pipeline

- [ ] One `CreateSyntaxProvider` call
- [ ] Syntactic predicate filtering 95%+ nodes
- [ ] Transform with early null returns
- [ ] `.Where(static info => info is not null)`
- [ ] `.Collect()` before `RegisterSourceOutput`
- [ ] Generate all outputs from collected data

### Multiple Parallel Pipelines

- [ ] Multiple `CreateSyntaxProvider` calls (one per type)
- [ ] Each predicate optimized for its target type
- [ ] Each transform with early null returns
- [ ] `.Where(static info => info is not null)` on each pipeline
- [ ] `.Collect()` on each pipeline
- [ ] `.Combine()` pipelines together
- [ ] Single `RegisterSourceOutput` with combined data

### Post-Initialization Output

- [ ] Use `RegisterPostInitializationOutput`
- [ ] Generate static infrastructure code
- [ ] Use templates for code generation
- [ ] Keep implementation simple (no discovery)

---

## See Also

- [architecture.md](architecture.md) - Why multiple independent generators
- [performance-principles.md](performance-principles.md) - Syntactic filtering, caching
- [template-system.md](template-system.md) - Using templates for code generation
- [quick-reference.md](quick-reference.md) - Complete generator example
