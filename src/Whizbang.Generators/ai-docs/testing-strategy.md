# Testing Strategy

**Three-level testing approach for source generators**

This document explains how to test source generators at three levels: unit tests, integration tests, and snapshot tests.

---

## Table of Contents

1. [Overview](#overview)
2. [Generator Testing Levels](#generator-testing-levels)
3. [Unit Test Pattern](#unit-test-pattern)
4. [Integration Test Pattern](#integration-test-pattern)
5. [Snapshot Test Pattern](#snapshot-test-pattern)
6. [Best Practices](#best-practices)

---

## Overview

### Why Three Levels?

**Each level tests different aspects**:

| Level | Tests | Speed | Purpose |
|-------|-------|-------|---------|
| **Unit** | Generator logic in isolation | Fast | Verify extraction and filtering |
| **Integration** | Generated code compiles and works | Medium | Verify end-to-end functionality |
| **Snapshot** | Generated code matches expected output | Medium | Catch regressions |

**All three levels are necessary** for comprehensive generator testing.

---

## Generator Testing Levels

### Level 1: Unit Tests

**What they test**:
- Info extraction logic
- Predicate filtering
- Transform methods
- Edge cases and error handling

**What they DON'T test**:
- Actual code generation
- Template rendering
- Compilation of generated code

**Use when**:
- Testing extraction logic in isolation
- Verifying edge cases (null handling, invalid syntax)
- Fast feedback during development

---

### Level 2: Integration Tests

**What they test**:
- Generated code compiles without errors
- Generated code has expected types and members
- Generated code integrates with library

**What they DON'T test**:
- Exact generated code format
- Comments and formatting

**Use when**:
- Verifying end-to-end functionality
- Testing that generated code works correctly
- Ensuring no compilation errors

---

### Level 3: Snapshot Tests

**What they test**:
- Generated code exactly matches expected output
- No regressions in code generation
- Formatting and structure

**What they DON'T test**:
- Whether code compiles (integration tests cover this)
- Functional correctness (integration tests cover this)

**Use when**:
- Catching unexpected changes in generated code
- Documenting expected output
- Regression testing

---

## Unit Test Pattern

### Example: Testing Extraction Logic

```csharp
using TUnit.Core;
using TUnit.Assertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Whizbang.Generators.Tests;

public class ReceptorDiscoveryGeneratorTests {

  [Test]
  public async Task ExtractReceptorInfo_ValidReceptor_ReturnsInfoAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      namespace MyApp.Receptors;

      public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
        public Task<OrderCreated> HandleAsync(CreateOrder message) {
          return Task.FromResult(new OrderCreated());
        }
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
    var info = ReceptorDiscoveryGenerator.ExtractReceptorInfo(
        new GeneratorSyntaxContext(classDecl, model, null, default),
        default
    );

    // Assert
    await Assert.That(info).IsNotNull();
    await Assert.That(info!.ClassName).Contains("OrderReceptor");
    await Assert.That(info.MessageType).Contains("CreateOrder");
    await Assert.That(info.ResponseType).Contains("OrderCreated");
  }

  [Test]
  public async Task ExtractReceptorInfo_NotAReceptor_ReturnsNullAsync() {
    // Arrange
    var source = @"
      namespace MyApp;

      public class NotAReceptor {
        // Just a regular class
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
    var info = ReceptorDiscoveryGenerator.ExtractReceptorInfo(
        new GeneratorSyntaxContext(classDecl, model, null, default),
        default
    );

    // Assert
    await Assert.That(info).IsNull();
  }

  private static Compilation CreateCompilation(string source) {
    var syntaxTree = CSharpSyntaxTree.ParseText(source);

    var references = new[] {
      MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
      MetadataReference.CreateFromFile(typeof(IReceptor<,>).Assembly.Location)
    };

    return CSharpCompilation.Create(
        "TestAssembly",
        new[] { syntaxTree },
        references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );
  }
}
```

---

### Unit Test Characteristics

**Fast**:
- No code generation
- No compilation of generated code
- Tests extraction logic only

**Focused**:
- One method or scenario per test
- Clear arrange/act/assert structure
- Easy to debug

**Comprehensive**:
- Test happy path
- Test edge cases (null, invalid syntax)
- Test error conditions

---

## Integration Test Pattern

### Example: Testing Generated Code Compiles

```csharp
[Test]
public async Task Generator_WithValidReceptor_GeneratesCompilableCodeAsync() {
  // Arrange - source with receptor
  var source = @"
    using Whizbang.Core;

    namespace MyApp.Receptors;

    public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
      public Task<OrderCreated> HandleAsync(CreateOrder message) {
        return Task.FromResult(new OrderCreated());
      }
    }

    public record CreateOrder(string CustomerId);
    public record OrderCreated(string OrderId);
  ";

  var compilation = CreateCompilation(source);

  // Act - run generator
  var driver = CSharpGeneratorDriver.Create(new ReceptorDiscoveryGenerator());
  driver.RunGeneratorsAndUpdateCompilation(
      compilation,
      out var outputCompilation,
      out var diagnostics
  );

  // Assert - no generator diagnostics
  await Assert.That(diagnostics).IsEmpty();

  // Assert - generated code compiles without errors
  var compilationDiagnostics = outputCompilation.GetDiagnostics();
  var errors = compilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
  await Assert.That(errors).IsEmpty();

  // Assert - can find generated dispatcher type
  var dispatcherType = outputCompilation.GetTypeByMetadataName("Whizbang.Core.Generated.GeneratedDispatcher");
  await Assert.That(dispatcherType).IsNotNull();

  // Assert - dispatcher has expected method
  var method = dispatcherType!.GetMembers("_getReceptorInvoker").FirstOrDefault();
  await Assert.That(method).IsNotNull();
}
```

---

### Integration Test Characteristics

**End-to-End**:
- Runs actual generator
- Compiles generated code
- Verifies types and members exist

**Realistic**:
- Uses real Roslyn compilation
- Tests actual integration
- Catches compilation errors

**Medium Speed**:
- Slower than unit tests (compilation)
- Faster than runtime tests
- Acceptable for CI/CD

---

### Testing Generated Code Functionality

```csharp
[Test]
public async Task GeneratedDispatcher_WithMessage_RoutesToReceptorAsync() {
  // Arrange - create compilation with receptor and generated code
  var source = CreateTestSource();
  var compilation = CreateCompilation(source);

  var driver = CSharpGeneratorDriver.Create(new ReceptorDiscoveryGenerator());
  driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

  // Act - compile to assembly
  var assembly = await CompileToAssembly(outputCompilation);

  // Assert - can instantiate generated dispatcher
  var dispatcherType = assembly.GetType("Whizbang.Core.Generated.GeneratedDispatcher");
  await Assert.That(dispatcherType).IsNotNull();

  var serviceProvider = CreateMockServiceProvider();
  var dispatcher = Activator.CreateInstance(dispatcherType!, serviceProvider);
  await Assert.That(dispatcher).IsNotNull();

  // Assert - dispatcher can route messages
  var message = new CreateOrder("customer-123");
  var result = await InvokeDispatcher(dispatcher!, message);
  await Assert.That(result).IsNotNull();
}

private static async Task<Assembly> CompileToAssembly(Compilation compilation) {
  using var ms = new MemoryStream();
  var emitResult = compilation.Emit(ms);

  if (!emitResult.Success) {
    throw new InvalidOperationException("Compilation failed");
  }

  ms.Seek(0, SeekOrigin.Begin);
  return Assembly.Load(ms.ToArray());
}
```

---

## Snapshot Test Pattern

### Example: Verifying Generated Code

```csharp
[Test]
public async Task Generator_WithValidReceptor_MatchesSnapshotAsync() {
  // Arrange
  var source = @"
    using Whizbang.Core;

    public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
      public Task<OrderCreated> HandleAsync(CreateOrder message) {
        return Task.FromResult(new OrderCreated());
      }
    }

    public record CreateOrder(string CustomerId);
    public record OrderCreated(string OrderId);
  ";

  var compilation = CreateCompilation(source);

  // Act - run generator
  var driver = CSharpGeneratorDriver.Create(new ReceptorDiscoveryGenerator());
  var result = driver.RunGenerators(compilation);

  // Assert - get generated source
  var generatedSource = result.Results.Single()
      .GeneratedSources
      .Single(s => s.HintName == "Dispatcher.g.cs")
      .SourceText
      .ToString();

  // Assert - matches expected snapshot
  var expectedSource = await File.ReadAllTextAsync("Snapshots/Dispatcher.g.cs");
  await Assert.That(generatedSource).IsEqualTo(expectedSource);
}
```

---

### Snapshot Test Characteristics

**Catches Regressions**:
- Any change in generated code fails test
- Forces deliberate updates
- Documents expected output

**Precise**:
- Exact comparison (formatting, comments, etc.)
- No ambiguity
- Clear diff when test fails

**Maintainable**:
- Snapshots stored in source control
- Easy to review changes
- Update snapshot = update expectation

---

### Snapshot Management

**Storing snapshots**:
```
tests/
├── Whizbang.Generators.Tests/
│   ├── Snapshots/
│   │   ├── Dispatcher.g.cs                # Expected output
│   │   ├── DispatcherRegistrations.g.cs
│   │   └── MessageRegistry.g.cs
│   └── ReceptorDiscoveryGeneratorTests.cs
```

**Updating snapshots**:
1. Run test
2. Test fails with diff
3. Review diff carefully
4. If correct, copy actual output to snapshot file
5. Commit updated snapshot

**DO NOT blindly update snapshots without review!**

---

## Best Practices

### Test Organization

```
tests/
├── Whizbang.Generators.Tests/
│   ├── Unit/
│   │   ├── ReceptorInfoExtractionTests.cs
│   │   ├── MessageTypeExtractionTests.cs
│   │   └── TemplateUtilitiesTests.cs
│   ├── Integration/
│   │   ├── ReceptorDiscoveryGeneratorTests.cs
│   │   ├── MessageRegistryGeneratorTests.cs
│   │   └── DiagnosticsGeneratorTests.cs
│   └── Snapshots/
│       ├── Dispatcher.g.cs
│       ├── DispatcherRegistrations.g.cs
│       └── MessageRegistry.g.cs
```

---

### Test Coverage Goals

**Unit Tests**:
- Cover all extraction methods
- Test all edge cases
- Test error conditions
- 100% code coverage of extraction logic

**Integration Tests**:
- At least one happy path per generator
- At least one error case per generator
- Test all output files compile

**Snapshot Tests**:
- One snapshot per output file
- Update when generator changes deliberately
- Never ignore snapshot failures

---

### Naming Conventions

```csharp
// Unit tests
[Test]
public async Task ExtractReceptorInfo_ValidReceptor_ReturnsInfoAsync() { }

[Test]
public async Task ExtractReceptorInfo_NotAReceptor_ReturnsNullAsync() { }

// Integration tests
[Test]
public async Task Generator_WithValidReceptor_GeneratesCompilableCodeAsync() { }

[Test]
public async Task Generator_NoReceptors_WarnsAndSkipsGenerationAsync() { }

// Snapshot tests
[Test]
public async Task Generator_WithValidReceptor_MatchesSnapshotAsync() { }
```

**Pattern**: `MethodOrComponent_Scenario_ExpectedOutcomeAsync`

---

### Shared Test Utilities

```csharp
// TestHelpers.cs
public static class TestHelpers {
  public static Compilation CreateCompilation(string source) {
    var syntaxTree = CSharpSyntaxTree.ParseText(source);

    var references = new[] {
      MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
      MetadataReference.CreateFromFile(typeof(IReceptor<,>).Assembly.Location)
    };

    return CSharpCompilation.Create(
        "TestAssembly",
        new[] { syntaxTree },
        references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );
  }

  public static GeneratorDriver RunGenerator(
      IIncrementalGenerator generator,
      Compilation compilation) {

    var driver = CSharpGeneratorDriver.Create(generator);
    driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
    return driver;
  }
}
```

---

## Checklist

Before claiming generator tests complete:

- [ ] Unit tests cover all extraction methods
- [ ] Unit tests test edge cases (null, invalid syntax)
- [ ] Integration tests verify generated code compiles
- [ ] Integration tests verify expected types exist
- [ ] Snapshot tests for all generated files
- [ ] Snapshots stored in source control
- [ ] Tests follow naming conventions
- [ ] Shared test utilities for common operations
- [ ] All tests passing

---

## See Also

- [generator-patterns.md](generator-patterns.md) - What to test in each pattern
- [quick-reference.md](quick-reference.md) - Complete generator example with tests
- [common-pitfalls.md](common-pitfalls.md) - Common testing mistakes
