# Strongly-Typed ID Providers Implementation Plan

**Status**: In Progress (Core interfaces complete, generator work remaining)
**Started**: 2025-12-16
**Last Updated**: 2025-12-16

## Overview

This plan implements `IWhizbangIdProvider<TId>` for strongly-typed ID generation with full AOT compatibility, auto-registration via ModuleInitializer, and comprehensive DI support.

## Completed Work ✅

### 1. Core Interfaces & Infrastructure

**Files Created**:

- ✅ `/Users/philcarbone/src/whizbang/src/Whizbang.Core/IWhizbangIdProviderGeneric.cs`
  - Generic interface `IWhizbangIdProvider<TId> where TId : struct`
  - Single method: `TId NewId()`
  - Full XML documentation with usage examples
  - Added `<docs>core-concepts/whizbang-ids</docs>` tag

- ✅ `/Users/philcarbone/src/whizbang/src/Whizbang.Core/WhizbangIdProviderRegistry.cs`
  - Global registry for provider factories
  - Thread-safe with lock
  - Methods:
    - `RegisterFactory<TId>(Func<IWhizbangIdProvider, IWhizbangIdProvider<TId>>)` - Called by ModuleInitializer
    - `RegisterDICallback(Action<IServiceCollection, IWhizbangIdProvider>)` - Registers DI callbacks
    - `CreateProvider<TId>(IWhizbangIdProvider)` - Creates typed provider from registry
    - `RegisterAllWithDI(IServiceCollection, IWhizbangIdProvider)` - Calls all DI callbacks
    - `GetRegisteredIdTypes()` - Returns all registered types for diagnostics

**Files Enhanced**:

- ✅ `/Users/philcarbone/src/whizbang/src/Whizbang.Core/WhizbangIdServiceCollectionExtensions.cs`
  - Moved to `Microsoft.Extensions.DependencyInjection` namespace (standard pattern)
  - Added `AddWhizbangIdProviders(IWhizbangIdProvider? baseProvider = null)` method
  - Registers base provider as singleton
  - Calls `WhizbangIdProviderRegistry.RegisterAllWithDI()` to register all typed providers
  - Added `<docs>core-concepts/whizbang-ids</docs>` tag
  - Comprehensive XML documentation with 3 usage examples

### 2. Generator Templates

**Files Created**:

- ✅ `/Users/philcarbone/src/whizbang/src/Whizbang.Generators/Templates/WhizbangIdProviderTemplate.cs`
  - Template for generating `{TypeName}Provider` classes
  - Implements `IWhizbangIdProvider<TId>`
  - Wraps base `IWhizbangIdProvider` with null check
  - Single method: `NewId()` returns `TypeName.From(_baseProvider.NewGuid())`
  - Placeholders: `__NAMESPACE__`, `__TYPE_NAME__`, `HEADER` region

- ✅ `/Users/philcarbone/src/whizbang/src/Whizbang.Generators/Templates/WhizbangIdProviderRegistrationTemplate.cs`
  - Template for generating `WhizbangIdProviderRegistration` class (one per assembly)
  - `[ModuleInitializer]` attribute on `Initialize()` method
  - Two regions to replace:
    - `FACTORY_REGISTRATIONS` - `RegisterFactory<OrderId>(baseProvider => new OrderIdProvider(baseProvider))`
    - `DI_REGISTRATIONS` - `services.AddSingleton<IWhizbangIdProvider<OrderId>>(...)`
  - `RegisterAll()` method for DI integration

## Remaining Work 🔧

### Phase 1: Generator Implementation (HIGH PRIORITY)

**File to Modify**: `/Users/philcarbone/src/whizbang/src/Whizbang.Generators/WhizbangIdGenerator.cs`

#### Task 1.1: Add Provider Generation

In the `GenerateWhizbangIds()` method (around line 376), add after JSON converter generation:

```csharp
// Generate provider class
var providerCode = GenerateProvider(id);
context.AddSource($"{hintNamePrefix}{id.TypeName}Provider.g.cs", providerCode);
```

#### Task 1.2: Implement GenerateProvider() Method

Add new method after `GenerateFactory()` (around line 574):

```csharp
/// <summary>
/// Generates a strongly-typed provider for the WhizbangId.
/// </summary>
private static string GenerateProvider(WhizbangIdInfo id) {
  var assembly = typeof(WhizbangIdGenerator).Assembly;
  var template = TemplateUtilities.GetEmbeddedTemplate(assembly, "WhizbangIdProviderTemplate.cs");

  // Replace namespace
  template = template.Replace("__NAMESPACE__", id.Namespace);

  // Replace type name
  template = template.Replace("__TYPE_NAME__", id.TypeName);

  // Replace header region
  template = TemplateUtilities.ReplaceHeaderRegion(assembly, template);

  return template;
}
```

#### Task 1.3: Add Registration Generation

In the `GenerateWhizbangIds()` method, after the foreach loop (around line 393), add:

```csharp
// Generate registration class (one per assembly)
if (deduplicated.Count > 0) {
  GenerateProviderRegistration(context, deduplicated);
}
```

#### Task 1.4: Implement GenerateProviderRegistration() Method

Add new method after `GenerateProvider()`:

```csharp
/// <summary>
/// Generates WhizbangIdProviderRegistration class for the assembly.
/// </summary>
private static void GenerateProviderRegistration(
    SourceProductionContext context,
    List<WhizbangIdInfo> ids) {

  var assembly = typeof(WhizbangIdGenerator).Assembly;
  var template = TemplateUtilities.GetEmbeddedTemplate(
    assembly,
    "WhizbangIdProviderRegistrationTemplate.cs"
  );

  // Determine namespace (use first ID's namespace for the Generated sub-namespace)
  var firstNamespace = ids[0].Namespace;
  template = template.Replace("__NAMESPACE__", firstNamespace);

  // Replace header
  template = TemplateUtilities.ReplaceHeaderRegion(assembly, template);

  // Generate factory registrations
  var factoryRegistrations = new StringBuilder();
  foreach (var id in ids) {
    factoryRegistrations.AppendLine(
      $"    global::Whizbang.Core.WhizbangIdProviderRegistry.RegisterFactory<{id.FullyQualifiedName}>(" +
      $"baseProvider => new {id.TypeName}Provider(baseProvider));"
    );
  }
  template = TemplateUtilities.ReplaceRegion(
    template,
    "FACTORY_REGISTRATIONS",
    factoryRegistrations.ToString()
  );

  // Generate DI registrations
  var diRegistrations = new StringBuilder();
  foreach (var id in ids) {
    diRegistrations.AppendLine(
      $"    services.AddSingleton<global::Whizbang.Core.IWhizbangIdProvider<{id.FullyQualifiedName}>>(" +
      $"sp => new {id.TypeName}Provider(sp.GetRequiredService<global::Whizbang.Core.IWhizbangIdProvider>()));"
    );
  }
  template = TemplateUtilities.ReplaceRegion(
    template,
    "DI_REGISTRATIONS",
    diRegistrations.ToString()
  );

  context.AddSource("WhizbangIdProviderRegistration.g.cs", template);
}
```

#### Task 1.5: Add CreateProvider() to Value Object

In the `GenerateValueObject()` method (around line 486, after Parse method), add:

```csharp
// CreateProvider method
sb.AppendLine();
sb.AppendLine("  /// <summary>");
sb.AppendLine($"  /// Creates a strongly-typed provider for {id.TypeName} instances.");
sb.AppendLine("  /// Useful for direct instantiation without DI.");
sb.AppendLine("  /// </summary>");
sb.AppendLine($"  public static global::Whizbang.Core.IWhizbangIdProvider<{id.TypeName}> CreateProvider(");
sb.AppendLine("      global::Whizbang.Core.IWhizbangIdProvider baseProvider) {");
sb.AppendLine($"    return new {id.TypeName}Provider(baseProvider);");
sb.AppendLine("  }");
```

### Phase 2: Generator Tests (HIGH PRIORITY)

**Files to Create/Modify**:

#### File: `tests/Whizbang.Generators.Tests/WhizbangIdProviderGenerationTests.cs` (NEW)

```csharp
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

public class WhizbangIdProviderGenerationTests : GeneratorTestBase {
  [Test]
  public async Task Generator_WithWhizbangId_GeneratesProviderClassAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = await RunGenerator(source);

    // Assert
    await Assert.That(result.GeneratedSources)
      .Any(s => s.HintName == "TestIdProvider.g.cs");

    var providerSource = result.GeneratedSources
      .First(s => s.HintName == "TestIdProvider.g.cs");
    var code = providerSource.SourceText.ToString();

    await Assert.That(code).Contains("class TestIdProvider");
    await Assert.That(code).Contains("IWhizbangIdProvider<TestId>");
    await Assert.That(code).Contains("public TestId NewId()");
  }

  [Test]
  public async Task Generator_WithMultipleIds_GeneratesAllProvidersAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId1;

      [WhizbangId]
      public readonly partial struct TestId2;
    ";

    // Act
    var result = await RunGenerator(source);

    // Assert
    await Assert.That(result.GeneratedSources)
      .Any(s => s.HintName == "TestId1Provider.g.cs");
    await Assert.That(result.GeneratedSources)
      .Any(s => s.HintName == "TestId2Provider.g.cs");
  }

  [Test]
  public async Task Generator_GeneratesProviderWithNullCheckAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = await RunGenerator(source);

    // Assert
    var providerSource = result.GeneratedSources
      .First(s => s.HintName == "TestIdProvider.g.cs");
    var code = providerSource.SourceText.ToString();

    await Assert.That(code).Contains("ArgumentNullException");
  }
}
```

#### File: `tests/Whizbang.Generators.Tests/WhizbangIdRegistrationGenerationTests.cs` (NEW)

```csharp
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

public class WhizbangIdRegistrationGenerationTests : GeneratorTestBase {
  [Test]
  public async Task Generator_GeneratesRegistrationClassAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = await RunGenerator(source);

    // Assert
    await Assert.That(result.GeneratedSources)
      .Any(s => s.HintName == "WhizbangIdProviderRegistration.g.cs");
  }

  [Test]
  public async Task Generator_RegistrationHasModuleInitializerAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = await RunGenerator(source);

    // Assert
    var registrationSource = result.GeneratedSources
      .First(s => s.HintName == "WhizbangIdProviderRegistration.g.cs");
    var code = registrationSource.SourceText.ToString();

    await Assert.That(code).Contains("[System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(code).Contains("public static void Initialize()");
  }

  [Test]
  public async Task Generator_RegistrationCallsRegisterFactoryAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = await RunGenerator(source);

    // Assert
    var registrationSource = result.GeneratedSources
      .First(s => s.HintName == "WhizbangIdProviderRegistration.g.cs");
    var code = registrationSource.SourceText.ToString();

    await Assert.That(code).Contains("WhizbangIdProviderRegistry.RegisterFactory<TestId>");
    await Assert.That(code).Contains("new TestIdProvider(baseProvider)");
  }

  [Test]
  public async Task Generator_RegistrationHasRegisterAllMethodAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = await RunGenerator(source);

    // Assert
    var registrationSource = result.GeneratedSources
      .First(s => s.HintName == "WhizbangIdProviderRegistration.g.cs");
    var code = registrationSource.SourceText.ToString();

    await Assert.That(code).Contains("public static void RegisterAll(");
    await Assert.That(code).Contains("IServiceCollection services");
  }

  [Test]
  public async Task Generator_RegistrationRegistersAllProvidersAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId1;

      [WhizbangId]
      public readonly partial struct TestId2;
    ";

    // Act
    var result = await RunGenerator(source);

    // Assert
    var registrationSource = result.GeneratedSources
      .First(s => s.HintName == "WhizbangIdProviderRegistration.g.cs");
    var code = registrationSource.SourceText.ToString();

    await Assert.That(code).Contains("AddSingleton<global::Whizbang.Core.IWhizbangIdProvider<");
    // Count should be 2 (one for each ID type)
    var addSingletonCount = code.Split("AddSingleton<global::Whizbang.Core.IWhizbangIdProvider<").Length - 1;
    await Assert.That(addSingletonCount).IsEqualTo(2);
  }
}
```

#### File: `tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs` (ENHANCE)

Add these tests to the existing file:

```csharp
[Test]
public async Task Generator_GeneratesCreateProviderMethodAsync() {
  // Arrange
  var source = @"
    using Whizbang.Core;

    [WhizbangId]
    public readonly partial struct TestId;
  ";

  // Act
  var result = await RunGenerator(source);

  // Assert
  var valueObjectSource = result.GeneratedSources
    .First(s => s.HintName == "TestId.g.cs");
  var code = valueObjectSource.SourceText.ToString();

  await Assert.That(code).Contains("public static global::Whizbang.Core.IWhizbangIdProvider<TestId> CreateProvider(");
  await Assert.That(code).Contains("return new TestIdProvider(baseProvider);");
}

[Test]
public async Task CreateProvider_WithBaseProvider_ReturnsValidProviderAsync() {
  // This is an integration test that actually compiles and runs generated code
  // See existing integration test patterns in the file
}
```

### Phase 3: Core Library Tests (HIGH PRIORITY)

**Files to Create**:

#### File: `tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdProviderRegistryTests.cs` (NEW)

```csharp
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests.ValueObjects;

// Test ID types
[WhizbangId]
public readonly partial struct RegistryTestId1;

[WhizbangId]
public readonly partial struct RegistryTestId2;

public class WhizbangIdProviderRegistryTests {
  [Test]
  public async Task RegisterFactory_WithValidFactory_RegistersSuccessfullyAsync() {
    // Test that factory registration works
  }

  [Test]
  public async Task CreateProvider_WithRegisteredType_ReturnsProviderAsync() {
    // Test that CreateProvider returns valid provider after registration
  }

  [Test]
  public async Task CreateProvider_WithUnregisteredType_ThrowsInvalidOperationExceptionAsync() {
    // Test error handling for unregistered types
  }

  [Test]
  public async Task CreateProvider_WithNullBaseProvider_ThrowsArgumentNullExceptionAsync() {
    // Test null checking
  }

  [Test]
  public async Task GetRegisteredIdTypes_ReturnsAllRegisteredTypesAsync() {
    // Test type enumeration
  }

  [Test]
  public async Task RegisterDICallback_WithValidCallback_RegistersSuccessfullyAsync() {
    // Test DI callback registration
  }

  [Test]
  public async Task RegisterAllWithDI_CallsAllRegisteredCallbacksAsync() {
    // Test that all callbacks are invoked
  }
}
```

#### File: `tests/Whizbang.Core.Tests/ValueObjects/IWhizbangIdProviderGenericTests.cs` (NEW)

```csharp
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests.ValueObjects;

// Test ID types for this file
[WhizbangId]
public readonly partial struct GenericTestId1;

[WhizbangId]
public readonly partial struct GenericTestId2;

public class IWhizbangIdProviderGenericTests {
  [Test]
  public async Task NewId_WithUuid7Provider_ReturnsValidIdAsync() {
    // Test that generated provider creates valid IDs
  }

  [Test]
  public async Task NewId_WithCustomProvider_UsesCustomProviderAsync() {
    // Test custom provider integration
  }

  [Test]
  public async Task NewId_GeneratesUniqueIdsAsync() {
    // Test uniqueness
  }

  [Test]
  public async Task Provider_WithNullBaseProvider_ThrowsArgumentNullExceptionAsync() {
    // Test null handling in constructor
  }
}
```

#### File: `tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdServiceCollectionExtensionsTests.cs` (ENHANCE)

Add to existing file:

```csharp
[Test]
public async Task AddWhizbangIdProviders_RegistersAllProvidersAsync() {
  // Arrange
  var services = new ServiceCollection();

  // Act
  services.AddWhizbangIdProviders();
  var provider = services.BuildServiceProvider();

  // Assert
  var testId1Provider = provider.GetService<IWhizbangIdProvider<SomeTestId>>();
  await Assert.That(testId1Provider).IsNotNull();
}

[Test]
public async Task AddWhizbangIdProviders_WithCustomProvider_UsesCustomProviderAsync() {
  // Test custom base provider
}

[Test]
public async Task AddWhizbangIdProviders_RegistersBaseProviderAsync() {
  // Test that base IWhizbangIdProvider is registered
}

[Test]
public async Task TypedProvider_InjectedInService_CreatesValidIdsAsync() {
  // Integration test with DI
}
```

### Phase 4: Fix EFCore.Postgres.Tests (HIGH PRIORITY)

**File to Create**: `tests/Whizbang.Data.EFCore.Postgres.Tests/TestIds.cs`

```csharp
using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

[WhizbangId]
public readonly partial struct TestOrderId;

[WhizbangId]
public readonly partial struct TestProductId;
```

**Files to Update**:

1. `tests/Whizbang.Data.EFCore.Postgres.Tests/SamplePerspective.cs`
   - Change `string OrderId` → `TestOrderId`
   - Change `record SampleOrderCreatedEvent(string OrderId, ...)` → `record SampleOrderCreatedEvent(TestOrderId, ...)`

2. `tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs` (12 errors)
   - Add `private readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();`
   - Replace `"test-id"` → `TestOrderId.From(_idProvider.NewGuid())`
   - Replace `id == "test-id"` → `id == testId`

3. `tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs` (6 errors)
   - Similar replacements

4. `tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs` (6 errors)
   - Similar replacements

### Phase 5: Documentation (MEDIUM PRIORITY)

**Location**: `/Users/philcarbone/src/whizbang-lib.github.io/src/assets/docs/v0.1.0/`

#### File: `core-concepts/whizbang-ids.md` (UPDATE)

Add comprehensive section on strongly-typed providers with 10+ registration patterns:

1. Auto-Register All Providers (Recommended)
2. Custom Base Provider
3. Override Specific ID Types
4. Test Project Overrides
5. No DI - Direct Provider Creation
6. Global Provider Configuration
7. Hybrid - Static + DI
8. Multi-Tenant ID Generation
9. Database Sequence IDs
10. Scoped vs Singleton Providers

Plus advanced scenarios: custom implementations, composite providers, logging wrappers.

#### Files to Create:

- `api/iwhizbangidprovider-generic.md` - Complete API reference
- `api/whizbangidproviderregistry.md` - Registry API reference
- `guides/migrating-to-typed-providers.md` - Migration guide
- `guides/testing-with-whizbang-ids.md` - Test patterns
- `guides/custom-id-providers.md` - Custom implementation guide

#### File: `tests/Whizbang.Documentation.Tests/WhizbangIdProvidersExampleTests.cs` (NEW)

Create 15+ tests validating all documentation examples.

### Phase 6: Final Steps (MEDIUM PRIORITY)

1. Run full test suite: `pwsh scripts/Test-All.ps1`
2. Run `dotnet format`
3. Regenerate code-docs mapping:
   ```bash
   cd /Users/philcarbone/src/whizbang-lib.github.io
   node src/scripts/generate-code-docs-map.mjs
   ```
4. Validate doc links: `mcp__whizbang-docs__validate-doc-links()`

## Implementation Notes

### Critical AOT Requirements

- ✅ No reflection in runtime code paths
- ✅ All provider types known at compile time
- ✅ ModuleInitializer for auto-registration
- ✅ Direct method calls only (no `Activator.CreateInstance`, no `MakeGenericMethod`)

### Key Architecture Decisions

1. **One Registration Class Per Assembly**: Each assembly gets one `WhizbangIdProviderRegistration.g.cs` that registers all ID types in that assembly.

2. **ModuleInitializer Pattern**: Runs automatically when assembly loads, registers factories before any user code runs.

3. **DI Callback Registration**: Each assembly registers a callback that `AddWhizbangIdProviders()` invokes to register typed providers.

4. **Thread Safety**: Registry uses locks for all operations.

5. **Namespace**: Extensions in `Microsoft.Extensions.DependencyInjection` namespace (standard .NET pattern).

### Testing Strategy

- **Generator Tests**: Verify code generation, templates, regions
- **Core Tests**: Verify registry, providers, DI integration
- **Integration Tests**: End-to-end with real DI container
- **Documentation Tests**: Validate all code examples

### Documentation Strategy

- **10+ Registration Patterns**: Cover simple to advanced scenarios
- **Code Examples**: Every pattern has tested example
- **API Reference**: Complete XML doc coverage
- **Migration Guide**: Step-by-step for existing codebases

## Quick Start for Next Session

```bash
# Navigate to library
cd /Users/philcarbone/src/whizbang

# Read this plan
cat plans/strongly-typed-id-providers-implementation.md

# Start with Phase 1: Generator Implementation
# Modify: src/Whizbang.Generators/WhizbangIdGenerator.cs
```

## References

- **Existing Code**: `/Users/philcarbone/src/whizbang/src/Whizbang.Generators/WhizbangIdGenerator.cs` (lines 376-574 for value object generation)
- **Template Utilities**: `/Users/philcarbone/src/whizbang/src/Whizbang.Generators/Shared/Utilities/TemplateUtilities.cs`
- **Similar Pattern**: See `MessageJsonContextGenerator.cs` for ModuleInitializer pattern
- **Testing Base**: `tests/Whizbang.Generators.Tests/GeneratorTestBase.cs`

## Success Criteria

- [ ] All generator tests pass (provider generation, registration generation)
- [ ] All Core library tests pass (registry, DI extensions, integration)
- [ ] EFCore.Postgres.Tests compiles and passes (26 errors fixed)
- [ ] Samples/ECommerce compiles with generated providers
- [ ] Full test suite passes: 340+ tests
- [ ] Documentation complete with tested examples
- [ ] Code formatted (`dotnet format`)
- [ ] Code-docs mapping regenerated and validated

## Estimated Remaining Work

- **Phase 1**: 2-3 hours (generator implementation)
- **Phase 2**: 2-3 hours (generator tests)
- **Phase 3**: 2-3 hours (core library tests)
- **Phase 4**: 1-2 hours (fix EFCore tests)
- **Phase 5**: 3-4 hours (comprehensive documentation)
- **Phase 6**: 1 hour (final steps)

**Total**: 11-16 hours

## Notes for Next Session

The core infrastructure is complete and working. The remaining work is primarily:

1. **Generator modifications** (highest impact, enables everything else)
2. **Tests** (validate functionality)
3. **Documentation** (user-facing value)

Start with Phase 1 (generator) since it unblocks testing and documentation work.
