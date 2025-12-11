# CLAUDE.md - Whizbang Source Generators

**Purpose**: Comprehensive guide to Whizbang source generator architecture, patterns, and best practices for AI assistants.

> **📁 New: Focused Documentation Available**
>
> This file provides a complete overview. For deep dives on specific topics, see the **`ai-docs/`** directory:
>
> - **[README.md](ai-docs/README.md)** - Navigation hub with scenario-based guidance
> - **[quick-reference.md](ai-docs/quick-reference.md)** - Complete working generator example
> - **[architecture.md](ai-docs/architecture.md)** - Multiple Independent Generators Pattern
> - **[performance-principles.md](ai-docs/performance-principles.md)** - CRITICAL: Syntactic filtering, 100x performance difference
> - **[value-type-records.md](ai-docs/value-type-records.md)** - Why sealed records matter (caching!)
> - **[generator-patterns.md](ai-docs/generator-patterns.md)** - Three core patterns
> - **[template-system.md](ai-docs/template-system.md)** - Real C# templates with IDE support
> - **[diagnostic-system.md](ai-docs/diagnostic-system.md)** - DiagnosticDescriptors pattern
> - **[project-structure.md](ai-docs/project-structure.md)** - .csproj configuration
> - **[testing-strategy.md](ai-docs/testing-strategy.md)** - Unit, integration, snapshot tests
> - **[common-pitfalls.md](ai-docs/common-pitfalls.md)** - Seven major mistakes to avoid
>
> Choose focused files for specific topics, or read this file for comprehensive overview.

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Performance Principles](#performance-principles)
4. [Generator Patterns](#generator-patterns)
5. [Template System](#template-system)
6. [Diagnostic System](#diagnostic-system)
7. [Value Type Records](#value-type-records)
8. [Project Structure](#project-structure)
9. [Testing Strategy](#testing-strategy)
10. [Common Pitfalls](#common-pitfalls)

---

## Overview

Whizbang uses **Roslyn incremental source generators** to achieve:
- **Zero reflection** - All type discovery at compile-time
- **AOT compatibility** - Native AOT from day one
- **Type safety** - Compile-time validation
- **Performance** - Optimal caching with incremental generation

### Existing Generators

1. **ReceptorDiscoveryGenerator** - Discovers `IReceptor<TMessage, TResponse>` implementations, generates dispatcher routing
2. **MessageRegistryGenerator** - Discovers messages, dispatchers, receptors, perspectives for VSCode tooling
3. **DiagnosticsGenerator** - Generates centralized diagnostics infrastructure

All generators are **independent** with **no cross-dependencies** for optimal incremental caching.

---

## Architecture

### Multiple Independent Generators Pattern

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

**Why separate generators?**
- **Cache isolation**: Changing a receptor doesn't invalidate message registry cache
- **Performance**: Simpler predicates = faster filtering
- **Maintainability**: Single responsibility per generator
- **Extensibility**: Easy to add new generators

### IIncrementalGenerator Interface

**ALL generators MUST implement `IIncrementalGenerator`** (not `ISourceGenerator`):

```csharp
[Generator]
public class MyGenerator : IIncrementalGenerator {
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Register pipelines here
  }
}
```

**Why incremental?**
- **Value-based caching**: Only re-runs when inputs actually change
- **Parallel execution**: Multiple generators run concurrently
- **Minimal overhead**: ~0ms on incremental builds when nothing changes

---

## Performance Principles

### 1. Syntactic Filtering First (CRITICAL)

```csharp
// ✅ CORRECT: Cheap syntactic check first
var candidates = context.SyntaxProvider.CreateSyntaxProvider(
    predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
    transform: static (ctx, ct) => PerformSemanticAnalysis(ctx, ct)
);

// ❌ WRONG: No filtering, semantic analysis on EVERY node
var candidates = context.SyntaxProvider.CreateSyntaxProvider(
    predicate: static (node, _) => true,  // Analyzes everything!
    transform: static (ctx, ct) => PerformSemanticAnalysis(ctx, ct)
);
```

**Performance impact:**
- Good predicate: ~50-100ms on 10,000 types
- Bad predicate: ~5,000-10,000ms on 10,000 types (50-100x slower!)

**Rule**: Predicate should filter out 95%+ of nodes before semantic analysis.

### 2. Value Type Records for Caching

```csharp
// ✅ CORRECT: Sealed record with value equality
internal sealed record ReceptorInfo(
    string ClassName,
    string MessageType,
    string ResponseType
);

// ❌ WRONG: Class with reference equality
internal class ReceptorInfo {
    public string ClassName { get; set; }
    public string MessageType { get; set; }
    public string ResponseType { get; set; }
}
```

**Why?**
- Records use **structural equality** (compare field values)
- Incremental generator caching depends on value equality
- Classes use **reference equality** - cache always invalidates

**Performance impact:**
- Record: 0ms on incremental build (cached)
- Class: 50-200ms on every build (never cached)

### 3. Early Null Returns

```csharp
private static ReceptorInfo? ExtractReceptorInfo(
    GeneratorSyntaxContext context,
    CancellationToken ct) {

  var classSymbol = context.SemanticModel.GetDeclaredSymbol(context.Node, ct);
  if (classSymbol is null) {
    return null;  // ✅ Early exit - avoid expensive operations
  }

  var receptorInterface = classSymbol.AllInterfaces.FirstOrDefault(i => ...);
  if (receptorInterface is null) {
    return null;  // ✅ Early exit - no point continuing
  }

  // Only reach here if we have a valid receptor
  return new ReceptorInfo(...);
}
```

**Rule**: Return `null` as soon as you know the node doesn't match. Don't perform expensive operations unnecessarily.

### 4. Static Methods Where Possible

```csharp
// ✅ CORRECT: Static methods allow better optimization
predicate: static (node, _) => node is ClassDeclarationSyntax,
transform: static (ctx, ct) => ExtractInfo(ctx, ct)

// ⚠️ ACCEPTABLE: Non-static if you need generator state
predicate: (node, _) => _someField == "value"  // Needs instance access
```

**Why static?**
- Compiler can optimize better
- No closure allocations
- Clear that method has no side effects

---

## Generator Patterns

### Pattern 1: Single Discovery Pipeline

**Use when**: Discovering one type of construct

**Example**: `ReceptorDiscoveryGenerator`

```csharp
public void Initialize(IncrementalGeneratorInitializationContext context) {
  // Single pipeline for discovering receptors
  var receptors = context.SyntaxProvider.CreateSyntaxProvider(
      predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
      transform: static (ctx, ct) => ExtractReceptorInfo(ctx, ct)
  ).Where(static info => info is not null);

  // Generate outputs from collected receptors
  context.RegisterSourceOutput(
      receptors.Collect(),
      static (ctx, receptors) => GenerateDispatcherCode(ctx, receptors!)
  );
}
```

**Characteristics**:
- One `CreateSyntaxProvider` call
- One `RegisterSourceOutput` call
- Collect all results, generate multiple files

### Pattern 2: Multiple Parallel Pipelines

**Use when**: Discovering multiple independent constructs

**Example**: `MessageRegistryGenerator`

```csharp
public void Initialize(IncrementalGeneratorInitializationContext context) {
  // Pipeline 1: Discover message types
  var messageTypes = context.SyntaxProvider.CreateSyntaxProvider(
      predicate: static (node, _) => node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 },
      transform: static (ctx, ct) => ExtractMessageType(ctx, ct)
  ).Where(static info => info is not null);

  // Pipeline 2: Discover dispatchers
  var dispatchers = context.SyntaxProvider.CreateSyntaxProvider(
      predicate: static (node, _) => node is InvocationExpressionSyntax,
      transform: static (ctx, ct) => ExtractDispatcher(ctx, ct)
  ).Where(static info => info is not null);

  // Pipeline 3: Discover receptors
  var receptors = context.SyntaxProvider.CreateSyntaxProvider(
      predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
      transform: static (ctx, ct) => ExtractReceptor(ctx, ct)
  ).Where(static info => info is not null);

  // Combine all pipelines at the end
  var allData = messageTypes.Collect()
      .Combine(dispatchers.Collect())
      .Combine(receptors.Collect());

  // Single output from combined data
  context.RegisterSourceOutput(
      allData,
      static (ctx, data) => GenerateRegistry(ctx, data)
  );
}
```

**Characteristics**:
- Multiple independent `CreateSyntaxProvider` calls
- Combine results with `.Combine()`
- Single `RegisterSourceOutput` with combined data

**Why?**
- Each pipeline has optimized predicate for its target
- Parallel discovery (concurrent execution)
- Independent caching per pipeline

### Pattern 3: Post-Initialization Output

**Use when**: Generating static infrastructure (no discovery needed)

**Example**: `DiagnosticsGenerator`

```csharp
public void Initialize(IncrementalGeneratorInitializationContext context) {
  // Runs once per compilation, no discovery
  context.RegisterPostInitializationOutput(ctx => {
    ctx.AddSource("WhizbangDiagnostics.g.cs", GenerateInfrastructure());
  });
}
```

**Characteristics**:
- No syntax providers
- Runs once at start of compilation
- Use for static infrastructure

---

## Template System

### Template File Structure

Templates are **real C# files** with:
- Full IDE support (IntelliSense, refactoring, etc.)
- Placeholder types in `Templates/Placeholders/`
- Excluded from compilation but included as embedded resources

```csharp
// DispatcherTemplate.cs
using Whizbang.Core;
using Whizbang.Core.Generated.Placeholders;  // Placeholder types for IDE

namespace Whizbang.Core.Generated;

#region HEADER
// This region gets replaced with generated header + timestamp
#endregion

public class GeneratedDispatcher : Dispatcher {
  public GeneratedDispatcher(IServiceProvider services) : base(services) { }

  protected override ReceptorInvoker<TResult>? _getReceptorInvoker<TResult>(
      object message,
      Type messageType) {

    #region SEND_ROUTING
    // This region gets replaced with generated routing code
    #endregion

    return null;
  }
}
```

### Region-Based Replacement

```csharp
var template = TemplateUtilities.GetEmbeddedTemplate(
    typeof(MyGenerator).Assembly,
    "MyTemplate.cs"
);

var generatedCode = new StringBuilder();
generatedCode.AppendLine("if (messageType == typeof(CreateOrder)) {");
generatedCode.AppendLine("  return InvokeOrderReceptor;");
generatedCode.AppendLine("}");

// Replace region, preserving indentation
var result = TemplateUtilities.ReplaceRegion(
    template,
    "SEND_ROUTING",  // Region name
    generatedCode.ToString()
);

context.AddSource("MyCode.g.cs", result);
```

**Key methods**:
- `GetEmbeddedTemplate(assembly, "Template.cs")` - Loads template from embedded resource
- `ReplaceRegion(template, "REGION_NAME", code)` - Replaces `#region` with generated code, preserves indentation
- `IndentCode(code, "  ")` - Adds indentation to each line
- `ReplaceHeaderRegion(assembly, template)` - Replaces HEADER region with timestamp

### Snippet System

**Use snippets for reusable code blocks**:

```csharp
// Templates/Snippets/DispatcherSnippets.cs
namespace Whizbang.Generators.Templates.Snippets;

internal static class DispatcherSnippets {

  #region SEND_ROUTING_SNIPPET
  if (messageType == typeof(__MESSAGE_TYPE__)) {
    var receptor = _serviceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
    return async msg => await receptor.HandleAsync((__MESSAGE_TYPE__)msg);
  }
  #endregion

  #region GENERATED_FILE_HEADER
  // <auto-generated/>
  // Generated by Whizbang source generator at __TIMESTAMP__
  // DO NOT EDIT - Changes will be overwritten
  #nullable enable
  #endregion
}
```

**Extract and use**:

```csharp
var snippet = TemplateUtilities.ExtractSnippet(
    typeof(MyGenerator).Assembly,
    "DispatcherSnippets.cs",
    "SEND_ROUTING_SNIPPET"
);

// Replace placeholders
var code = snippet
    .Replace("__MESSAGE_TYPE__", "MyApp.Commands.CreateOrder")
    .Replace("__RESPONSE_TYPE__", "MyApp.Events.OrderCreated")
    .Replace("__RECEPTOR_INTERFACE__", "Whizbang.Core.IReceptor");
```

**Benefits**:
- IDE support for snippet code
- Reusable across generators
- Easy to update and maintain
- Compile-time syntax validation (via placeholders)

---

## Diagnostic System

### Diagnostic Descriptor Pattern

```csharp
// DiagnosticDescriptors.cs
internal static class DiagnosticDescriptors {
  private const string CATEGORY = "Whizbang.SourceGeneration";

  /// <summary>
  /// WHIZ001: Info - Receptor discovered during source generation.
  /// </summary>
  public static readonly DiagnosticDescriptor ReceptorDiscovered = new(
      id: "WHIZ001",
      title: "Receptor Discovered",
      messageFormat: "Found receptor '{0}' handling {1} → {2}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A receptor implementation was discovered and will be registered."
  );

  /// <summary>
  /// WHIZ002: Warning - No receptors found in the compilation.
  /// </summary>
  public static readonly DiagnosticDescriptor NoReceptorsFound = new(
      id: "WHIZ002",
      title: "No Receptors Found",
      messageFormat: "No IReceptor implementations were found in the compilation",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "The source generator did not find any classes implementing IReceptor<TMessage, TResponse>."
  );
}
```

**Diagnostic ID Allocation**:
- WHIZ001-003: Existing diagnostics (receptors)
- WHIZ004-006: Aggregate ID system
- WHIZ007-009: Reserved for future
- WHIZ010+: Additional features

### Reporting Diagnostics

```csharp
// Report success
context.ReportDiagnostic(Diagnostic.Create(
    DiagnosticDescriptors.ReceptorDiscovered,
    Location.None,  // Or node.GetLocation() for specific location
    "OrderReceptor",  // Arg 0
    "CreateOrder",    // Arg 1
    "OrderCreated"    // Arg 2
));

// Report error at specific location
var location = propertyDeclaration.GetLocation();
context.ReportDiagnostic(Diagnostic.Create(
    DiagnosticDescriptors.InvalidProperty,
    location,  // Shows squiggle in IDE at this location
    propertyName
));
```

**Severity Levels**:
- `Info` - Informational message (shows in build output)
- `Warning` - Warning (shows in IDE, doesn't break build)
- `Error` - Error (shows in IDE, breaks build)

---

## Value Type Records

### The Critical Pattern

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

**Requirements**:
1. ✅ **Must be `record`** - Uses value equality
2. ✅ **Must be `sealed`** - Performance optimization
3. ✅ **Use primary constructor** - Concise syntax
4. ✅ **Include XML documentation** - Explain purpose and parameters
5. ✅ **Use fully qualified names** - Avoid ambiguity (e.g., "global::MyApp.Commands.CreateOrder")

**Why these requirements?**

```csharp
// Incremental generator caching relies on equality checks
var cached = previousCompilation.Get<ReceptorInfo>();
var current = currentCompilation.Get<ReceptorInfo>();

// With record (value equality):
cached == current  // ✅ true if fields match - generator skips re-generation

// With class (reference equality):
cached == current  // ❌ false even if fields match - generator always re-runs
```

### Common Value Types

```csharp
// Simple info record
internal sealed record ReceptorInfo(
    string ClassName,
    string MessageType,
    string ResponseType
);

// With location info
internal sealed record MessageTypeInfo(
    string TypeName,
    bool IsCommand,
    bool IsEvent,
    string FilePath,
    int LineNumber
);

// With array (use string[] for value equality)
internal sealed record PerspectiveLocationInfo(
    string ClassName,
    string[] EventTypes,  // Arrays work with value equality in records
    string FilePath,
    int LineNumber
);
```

---

## Project Structure

### File Organization

```
Whizbang.Generators/
├── CLAUDE.md                           # This file
├── Whizbang.Generators.csproj          # Project configuration
├── ReceptorDiscoveryGenerator.cs       # Generator implementations
├── MessageRegistryGenerator.cs
├── DiagnosticsGenerator.cs
├── ReceptorInfo.cs                     # Value type records
├── DiagnosticDescriptors.cs            # Diagnostic definitions
├── TemplateUtilities.cs                # Shared template utilities
└── Templates/                          # Template files (embedded resources)
    ├── DispatcherTemplate.cs
    ├── DispatcherRegistrationsTemplate.cs
    ├── WhizbangDiagnosticsTemplate.cs
    ├── Placeholders/
    │   └── PlaceholderTypes.cs         # Placeholder types for IDE support
    └── Snippets/
        └── DispatcherSnippets.cs       # Reusable code snippets
```

### Project Configuration

**Key settings in `.csproj`**:

```xml
<PropertyGroup>
  <!-- Target netstandard2.0 for broad compatibility -->
  <TargetFramework>netstandard2.0</TargetFramework>

  <!-- Mark as Roslyn component -->
  <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
  <IsRoslynComponent>true</IsRoslynComponent>

  <!-- Enable viewing generated files (for debugging) -->
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(MSBuildProjectDirectory)/.whizbang-generated</CompilerGeneratedFilesOutputPath>

  <!-- Enable performance reporting -->
  <ReportAnalyzer>true</ReportAnalyzer>
</PropertyGroup>

<ItemGroup>
  <!-- Required packages -->
  <PackageReference Include="Microsoft.CodeAnalysis.CSharp" PrivateAssets="all" />
  <PackageReference Include="Microsoft.CodeAnalysis.Analyzers" PrivateAssets="all" />
  <PackageReference Include="PolySharp" PrivateAssets="all" />
</ItemGroup>

<ItemGroup>
  <!-- Templates: exclude from compilation, embed as resources -->
  <Compile Remove="Templates\**\*.cs" />
  <None Include="Templates\**\*.cs" />
  <EmbeddedResource Include="Templates\**\*.cs" />
</ItemGroup>
```

**Important**:
- Templates are **excluded from compilation** (`<Compile Remove>`)
- Templates are **included for IDE** (`<None Include>`)
- Templates are **embedded in assembly** (`<EmbeddedResource Include>`)

This allows templates to be real C# with full IDE support while not causing compilation errors.

---

## Testing Strategy

### Generator Testing Levels

1. **Unit Tests** - Test generator logic in isolation
2. **Integration Tests** - Test generated code compiles and works
3. **Snapshot Tests** - Verify generated code matches expectations

### Unit Test Pattern

```csharp
// Tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs
[Test]
public async Task Generator_WithValidReceptor_GeneratesDispatcherAsync() {
  // Arrange
  var source = @"
    using Whizbang.Core;

    public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
      public Task<OrderCreated> HandleAsync(CreateOrder message) {
        return Task.FromResult(new OrderCreated());
      }
    }
  ";

  // Act
  var result = await RunGenerator(source);

  // Assert
  await Assert.That(result.Diagnostics).IsEmpty();
  await Assert.That(result.GeneratedSources).HasCount().EqualTo(3);

  var dispatcher = result.GeneratedSources.Single(s => s.HintName == "Dispatcher.g.cs");
  await Assert.That(dispatcher.SourceText.ToString()).Contains("CreateOrder");
}
```

### Integration Test Pattern

```csharp
[Test]
public async Task GeneratedDispatcher_WithMessage_RoutesToReceptorAsync() {
  // Arrange - source with receptor
  var source = CreateTestSource();
  var compilation = CreateCompilation(source);

  // Act - run generator
  var driver = CSharpGeneratorDriver.Create(new ReceptorDiscoveryGenerator());
  driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

  // Assert - generated code compiles
  var diagnostics = outputCompilation.GetDiagnostics();
  await Assert.That(diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

  // Assert - can instantiate generated dispatcher
  var assembly = await CompileToAssembly(outputCompilation);
  var dispatcherType = assembly.GetType("Whizbang.Core.Generated.GeneratedDispatcher");
  await Assert.That(dispatcherType).IsNotNull();
}
```

---

## Common Pitfalls

### ❌ Pitfall 1: Using Classes Instead of Records

```csharp
// ❌ WRONG: Class breaks incremental caching
internal class ReceptorInfo {
    public string ClassName { get; set; }
}

// ✅ CORRECT: Sealed record enables caching
internal sealed record ReceptorInfo(string ClassName);
```

**Result**: Generator always re-runs, even when nothing changed (50-200ms overhead per build).

### ❌ Pitfall 2: Expensive Predicate

```csharp
// ❌ WRONG: Semantic analysis in predicate
predicate: (node, _) => {
    var symbol = semanticModel.GetSymbolInfo(node);  // VERY EXPENSIVE!
    return symbol.Symbol is IMethodSymbol;
},

// ✅ CORRECT: Syntactic check only
predicate: static (node, _) => node is MethodDeclarationSyntax,
```

**Result**: 100x slower compilation.

### ❌ Pitfall 3: Non-Static Methods

```csharp
// ⚠️ LESS OPTIMAL: Non-static lambda captures 'this'
predicate: (node, _) => IsValidNode(node),
transform: (ctx, ct) => ExtractInfo(ctx, ct)

// ✅ BETTER: Static methods, no closure
predicate: static (node, _) => IsValidNode(node),
transform: static (ctx, ct) => ExtractInfo(ctx, ct)
```

**Result**: Allocations and missed optimizations.

### ❌ Pitfall 4: Forgetting to Filter Nulls

```csharp
// ❌ WRONG: Nulls passed to RegisterSourceOutput
var items = context.SyntaxProvider.CreateSyntaxProvider(
    predicate: static (node, _) => node is ClassDeclarationSyntax,
    transform: static (ctx, ct) => ExtractInfo(ctx, ct)  // May return null
).Collect();

// ✅ CORRECT: Filter nulls before collecting
var items = context.SyntaxProvider.CreateSyntaxProvider(
    predicate: static (node, _) => node is ClassDeclarationSyntax,
    transform: static (ctx, ct) => ExtractInfo(ctx, ct)
).Where(static info => info is not null);  // Filter nulls
```

**Result**: Null reference exceptions in generation.

### ❌ Pitfall 5: Incorrect Resource Namespace

```csharp
// ❌ WRONG: Resource name doesn't match actual namespace
var template = TemplateUtilities.GetEmbeddedTemplate(
    assembly,
    "MyTemplate.cs",
    "Whizbang.Templates"  // Wrong namespace!
);

// ✅ CORRECT: Match actual namespace
var template = TemplateUtilities.GetEmbeddedTemplate(
    assembly,
    "MyTemplate.cs",
    "Whizbang.Generators.Templates"  // Correct!
);
```

**Result**: "Template not found" error at runtime.

### ❌ Pitfall 6: Modifying Immutable Collections

```csharp
// ❌ WRONG: Can't modify ImmutableArray
void ProcessItems(ImmutableArray<ReceptorInfo> items) {
    items.Add(newItem);  // Compilation error!
}

// ✅ CORRECT: Use builder or create new array
void ProcessItems(ImmutableArray<ReceptorInfo> items) {
    var builder = items.ToBuilder();
    builder.Add(newItem);
    return builder.ToImmutable();
}
```

### ❌ Pitfall 7: Forgetting CancellationToken

```csharp
// ❌ WRONG: Ignores cancellation
private static Info? Extract(GeneratorSyntaxContext ctx, CancellationToken ct) {
    var symbol = ctx.SemanticModel.GetDeclaredSymbol(ctx.Node);  // Doesn't pass ct!
}

// ✅ CORRECT: Pass cancellation token
private static Info? Extract(GeneratorSyntaxContext ctx, CancellationToken ct) {
    var symbol = ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct);  // Cancellable!
}
```

**Result**: Generator can't be cancelled, delays IDE responsiveness.

---

## Quick Reference

### Creating a New Generator

1. Create value-type record: `internal sealed record MyInfo(...)`
2. Create generator class: `[Generator] public class MyGenerator : IIncrementalGenerator`
3. Implement `Initialize` with syntax provider(s)
4. Add predicates with syntactic filtering
5. Add transform methods with semantic analysis + early nulls
6. Add diagnostics to `DiagnosticDescriptors.cs`
7. Create templates in `Templates/` if needed
8. Register source output with `context.RegisterSourceOutput`

### Performance Checklist

- ✅ Use `IIncrementalGenerator` (not `ISourceGenerator`)
- ✅ Sealed record value types for all cached data
- ✅ Static predicates and transforms
- ✅ Syntactic filtering before semantic analysis
- ✅ Early null returns in transform
- ✅ Filter nulls with `.Where(static info => info is not null)`
- ✅ Pass `CancellationToken` to all semantic operations

### Template Checklist

- ✅ Real C# files in `Templates/` directory
- ✅ Placeholder types in `Templates/Placeholders/`
- ✅ Excluded from compilation: `<Compile Remove="Templates\**\*.cs" />`
- ✅ Embedded as resources: `<EmbeddedResource Include="Templates\**\*.cs" />`
- ✅ Use `#region` markers for replacement points
- ✅ Load with `TemplateUtilities.GetEmbeddedTemplate`
- ✅ Replace with `TemplateUtilities.ReplaceRegion`

---

## Example: Complete Generator Implementation

```csharp
// MyFeatureInfo.cs
internal sealed record MyFeatureInfo(
    string TypeName,
    string PropertyName
);

// MyFeatureGenerator.cs
[Generator]
public class MyFeatureGenerator : IIncrementalGenerator {
  private const string MY_ATTRIBUTE = "Whizbang.Core.MyFeatureAttribute";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover types with [MyFeature] attribute
    var features = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
        transform: static (ctx, ct) => ExtractFeatureInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Generate code from features
    context.RegisterSourceOutput(
        features.Collect(),
        static (ctx, features) => GenerateFeatureCode(ctx, features!)
    );
  }

  private static MyFeatureInfo? ExtractFeatureInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var classDecl = (ClassDeclarationSyntax)context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct);

    if (symbol is null) return null;

    var hasAttribute = symbol.GetAttributes()
        .Any(a => a.AttributeClass?.ToDisplayString() == MY_ATTRIBUTE);

    if (!hasAttribute) return null;

    // Extract required info
    return new MyFeatureInfo(
        TypeName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        PropertyName: "SomeProperty"
    );
  }

  private static void GenerateFeatureCode(
      SourceProductionContext context,
      ImmutableArray<MyFeatureInfo> features) {

    if (features.IsEmpty) return;

    // Report discoveries
    foreach (var feature in features) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.FeatureDiscovered,
          Location.None,
          feature.TypeName
      ));
    }

    // Load template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(MyFeatureGenerator).Assembly,
        "MyFeatureTemplate.cs"
    );

    // Generate code
    var sb = new StringBuilder();
    foreach (var feature in features) {
      sb.AppendLine($"// Feature: {feature.TypeName}");
    }

    // Replace region
    var result = TemplateUtilities.ReplaceRegion(template, "FEATURES", sb.ToString());
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(MyFeatureGenerator).Assembly, result);

    // Add source
    context.AddSource("MyFeature.g.cs", result);
  }
}
```

---

## Resources

- [Roslyn Source Generators Cookbook](https://github.com/dotnet/roslyn/blob/main/docs/features/source-generators.cookbook.md)
- [Incremental Generators](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md)
- Whizbang examples: `ReceptorDiscoveryGenerator.cs`, `MessageRegistryGenerator.cs`

---

**Last Updated**: 2025-01-04 (v0.1.0)
