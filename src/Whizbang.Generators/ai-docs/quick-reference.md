# Quick Reference

**Checklists and complete generator example**

This document provides quick reference checklists and a complete working generator implementation.

---

## Table of Contents

1. [Creating a New Generator](#creating-a-new-generator)
2. [Performance Checklist](#performance-checklist)
3. [Template Checklist](#template-checklist)
4. [Complete Generator Example](#complete-generator-example)

---

## Creating a New Generator

### Step-by-Step Process

**1. Create value-type record**:
```csharp
/// <summary>
/// Information about discovered feature.
/// This record uses value equality for incremental generator caching.
/// </summary>
internal sealed record MyFeatureInfo(
    string TypeName,
    string PropertyName
);
```

**2. Create generator class**:
```csharp
[Generator]
public class MyFeatureGenerator : IIncrementalGenerator {
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Register pipelines here
  }
}
```

**3. Add syntactic predicate**:
```csharp
var features = context.SyntaxProvider.CreateSyntaxProvider(
    // Syntactic filtering only!
    predicate: static (node, _) =>
        node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
    transform: static (ctx, ct) => ExtractFeatureInfo(ctx, ct)
);
```

**4. Add transform method**:
```csharp
private static MyFeatureInfo? ExtractFeatureInfo(
    GeneratorSyntaxContext context,
    CancellationToken ct) {

  var classDecl = (ClassDeclarationSyntax)context.Node;
  var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct);

  if (symbol is null) return null;  // Early null return

  var hasAttribute = symbol.GetAttributes()
      .Any(a => a.AttributeClass?.ToDisplayString() == MY_ATTRIBUTE);

  if (!hasAttribute) return null;  // Early null return

  return new MyFeatureInfo(
      TypeName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
      PropertyName: "SomeProperty"
  );
}
```

**5. Filter nulls**:
```csharp
var features = context.SyntaxProvider.CreateSyntaxProvider(
    predicate: static (node, _) => ...,
    transform: static (ctx, ct) => ExtractFeatureInfo(ctx, ct)
).Where(static info => info is not null);  // Filter nulls!
```

**6. Add diagnostics**:
```csharp
// Add to DiagnosticDescriptors.cs
public static readonly DiagnosticDescriptor FeatureDiscovered = new(
    id: "WHIZ010",  // Next available ID
    title: "Feature Discovered",
    messageFormat: "Found feature '{0}'",
    category: "Whizbang.SourceGeneration",
    defaultSeverity: DiagnosticSeverity.Info,
    isEnabledByDefault: true,
    description: "A feature was discovered during source generation."
);
```

**7. Create template (if needed)**:
```csharp
// Templates/MyFeatureTemplate.cs
namespace Whizbang.Core.Generated;

#region HEADER
// Auto-generated header
#endregion

public class MyFeatureRegistry {
  #region FEATURES
  // Generated features
  #endregion
}
```

**8. Register source output**:
```csharp
context.RegisterSourceOutput(
    features.Collect(),
    static (ctx, features) => GenerateFeatureCode(ctx, features!)
);
```

**9. Generate code**:
```csharp
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

  // Replace regions
  var result = TemplateUtilities.ReplaceRegion(template, "FEATURES", sb.ToString());
  result = TemplateUtilities.ReplaceHeaderRegion(typeof(MyFeatureGenerator).Assembly, result);

  // Add source
  context.AddSource("MyFeature.g.cs", result);
}
```

---

## Performance Checklist

**Before claiming generator work complete**:

### Incremental Generator

- [ ] Uses `IIncrementalGenerator` (not `ISourceGenerator`)
- [ ] Implements `Initialize(IncrementalGeneratorInitializationContext)`

### Value Type Records

- [ ] Info types are `sealed record` (not classes)
- [ ] Uses primary constructor syntax
- [ ] Includes XML documentation
- [ ] Mentions "value equality" in documentation
- [ ] Uses fully qualified type names

### Syntactic Filtering

- [ ] Predicate uses syntactic filtering only (no semantic analysis)
- [ ] Predicate filters out 95%+ of nodes
- [ ] Pattern matching used where appropriate
- [ ] Property checks used (`BaseList.Types.Count: > 0`)

### Transform Methods

- [ ] Transform has early null returns
- [ ] Semantic operations pass `CancellationToken`
- [ ] Exits immediately when node doesn't match
- [ ] Avoids expensive operations on invalid nodes

### Static Methods

- [ ] Predicates are `static` where possible
- [ ] Transforms are `static` where possible
- [ ] Helper methods are `static` where possible

### Null Filtering

- [ ] `.Where(static info => info is not null)` after transform
- [ ] Nulls filtered before `.Collect()` or `.Combine()`
- [ ] Source output uses null-forgiving operator (`!`) safely

### Performance Testing

- [ ] Tested incremental build performance
- [ ] ~0ms when nothing changes (cached)
- [ ] Reasonable time when changes occur

---

## Template Checklist

**If using templates**:

### Template Files

- [ ] Templates in `Templates/` directory
- [ ] Real C# files with full IDE support
- [ ] Placeholder types in `Templates/Placeholders/`
- [ ] Snippets in `Templates/Snippets/`

### Project Configuration

- [ ] `.csproj` configured: `<Compile Remove="Templates\**\*.cs" />`
- [ ] `.csproj` configured: `<None Include="Templates\**\*.cs" />`
- [ ] `.csproj` configured: `<EmbeddedResource Include="Templates\**\*.cs" />`

### Template Content

- [ ] Templates use `#region` markers for injection points
- [ ] Region names are UPPERCASE (e.g., `HEADER`, `SEND_ROUTING`)
- [ ] Placeholder names clearly marked (`Placeholder*` or `__*__`)
- [ ] All generated files include HEADER region

### Template Loading

- [ ] Template filenames match `GetEmbeddedTemplate` calls (case-sensitive)
- [ ] Template namespaces match actual file namespaces
- [ ] Templates loaded via `TemplateUtilities.GetEmbeddedTemplate`
- [ ] Regions replaced via `TemplateUtilities.ReplaceRegion`

---

## Complete Generator Example

### Value Type Record

```csharp
/// <summary>
/// Information about a class with [MyFeature] attribute.
/// This record uses value equality for incremental generator caching.
/// </summary>
/// <param name="TypeName">Fully qualified type name</param>
/// <param name="PropertyName">Name of the feature property</param>
internal sealed record MyFeatureInfo(
    string TypeName,
    string PropertyName
);
```

---

### Diagnostic Descriptor

```csharp
// DiagnosticDescriptors.cs
/// <summary>
/// WHIZ010: Info - Feature discovered during source generation.
/// </summary>
public static readonly DiagnosticDescriptor FeatureDiscovered = new(
    id: "WHIZ010",
    title: "Feature Discovered",
    messageFormat: "Found feature '{0}' with property '{1}'",
    category: "Whizbang.SourceGeneration",
    defaultSeverity: DiagnosticSeverity.Info,
    isEnabledByDefault: true,
    description: "A class with [MyFeature] attribute was discovered."
);
```

---

### Generator Implementation

```csharp
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

[Generator]
public class MyFeatureGenerator : IIncrementalGenerator {
  private const string MY_FEATURE_ATTRIBUTE = "Whizbang.Core.MyFeatureAttribute";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover classes with [MyFeature] attribute
    var features = context.SyntaxProvider.CreateSyntaxProvider(
        // Predicate: Syntactic filtering only
        predicate: static (node, _) =>
            node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },

        // Transform: Semantic analysis with early null returns
        transform: static (ctx, ct) => ExtractFeatureInfo(ctx, ct)

    // Filter nulls
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

    // Early null return: No symbol
    if (symbol is null) return null;

    // Check for [MyFeature] attribute
    var hasAttribute = symbol.GetAttributes()
        .Any(a => a.AttributeClass?.ToDisplayString() == MY_FEATURE_ATTRIBUTE);

    // Early null return: No attribute
    if (!hasAttribute) return null;

    // Find feature property
    var property = symbol.GetMembers()
        .OfType<IPropertySymbol>()
        .FirstOrDefault(p => p.Name == "FeatureProperty");

    // Early null return: No property
    if (property is null) return null;

    // Extract info
    return new MyFeatureInfo(
        TypeName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        PropertyName: property.Name
    );
  }

  private static void GenerateFeatureCode(
      SourceProductionContext context,
      ImmutableArray<MyFeatureInfo> features) {

    // Early exit if no features
    if (features.IsEmpty) return;

    // Report discoveries
    foreach (var feature in features) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.FeatureDiscovered,
          Location.None,
          feature.TypeName,
          feature.PropertyName
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
      sb.AppendLine($"//   Property: {feature.PropertyName}");
    }

    // Replace regions
    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(
        typeof(MyFeatureGenerator).Assembly,
        result
    );
    result = TemplateUtilities.ReplaceRegion(
        result,
        "FEATURES",
        sb.ToString()
    );

    // Add source
    context.AddSource("MyFeature.g.cs", result);
  }
}
```

---

### Template File

```csharp
// Templates/MyFeatureTemplate.cs
using System;

namespace Whizbang.Core.Generated;

#region HEADER
// <auto-generated/>
// Generated by Whizbang source generator
// DO NOT EDIT - Changes will be overwritten
#nullable enable
#endregion

public static class MyFeatureRegistry {

  #region FEATURES
  // Generated features go here
  #endregion

  public static void Initialize() {
    // Initialization logic
  }
}
```

---

### .csproj Configuration

```xml
<ItemGroup>
  <!-- Templates: exclude from compilation, embed as resources -->
  <Compile Remove="Templates\**\*.cs" />
  <None Include="Templates\**\*.cs" />
  <EmbeddedResource Include="Templates\**\*.cs" />
</ItemGroup>
```

---

### Unit Test

```csharp
[Test]
public async Task ExtractFeatureInfo_ValidFeature_ReturnsInfoAsync() {
  // Arrange
  var source = @"
    using Whizbang.Core;

    [MyFeature]
    public class MyClass {
      public string FeatureProperty { get; set; }
    }
  ";

  var compilation = CreateCompilation(source);
  var tree = compilation.SyntaxTrees.First();
  var model = compilation.GetSemanticModel(tree);
  var classDecl = tree.GetRoot()
      .DescendantNodes()
      .OfType<ClassDeclarationSyntax>()
      .First();

  // Act
  var info = MyFeatureGenerator.ExtractFeatureInfo(
      new GeneratorSyntaxContext(classDecl, model, null, default),
      default
  );

  // Assert
  await Assert.That(info).IsNotNull();
  await Assert.That(info!.TypeName).Contains("MyClass");
  await Assert.That(info.PropertyName).IsEqualTo("FeatureProperty");
}
```

---

## See Also

- [architecture.md](architecture.md) - Overall generator architecture
- [performance-principles.md](performance-principles.md) - Performance patterns
- [generator-patterns.md](generator-patterns.md) - Implementation patterns
- [template-system.md](template-system.md) - Using templates
- [value-type-records.md](value-type-records.md) - Critical caching pattern
- [diagnostic-system.md](diagnostic-system.md) - Reporting diagnostics
- [project-structure.md](project-structure.md) - File organization
- [testing-strategy.md](testing-strategy.md) - Testing generators
- [common-pitfalls.md](common-pitfalls.md) - Mistakes to avoid
