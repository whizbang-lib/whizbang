using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// TDD tests for EFCorePerspectiveConfigurationGenerator diagnostic output.
/// These tests verify that the generated code implements IWhizbangDiscoveryDiagnostics
/// and provides useful diagnostic information about discovered perspectives.
/// </summary>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/EFCorePerspectiveConfigurationGenerator.cs</tests>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/EFCoreConfigurationTemplate.cs</tests>
public class EFCorePerspectiveConfigurationGeneratorDiagnosticsTests {
  /// <summary>
  /// RED TEST: Generated code should implement IWhizbangDiscoveryDiagnostics interface.
  /// This ensures consumers can use a consistent API across all generators.
  /// </summary>
  [Test]
  public async Task GeneratedCode_ImplementsIDiagnosticsInterfaceAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto(string Name, decimal Price);

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {

        public ProductDto Apply(ProductDto currentData, ProductCreated @event) {
          return currentData;
        }
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Generated class should implement IWhizbangDiscoveryDiagnostics
    await Assert.That(result.GeneratedSources).Count().IsGreaterThanOrEqualTo(1);

    var generatedCode = result.GeneratedSources
      .FirstOrDefault(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs");

    await Assert.That(generatedCode).IsNotNull();
    await Assert.That(generatedCode!.SourceText.ToString())
      .Contains("IWhizbangDiscoveryDiagnostics");
  }

  /// <summary>
  /// RED TEST: Generated diagnostic class should have correct generator name.
  /// </summary>
  [Test]
  public async Task GeneratedDiagnostics_HasCorrectGeneratorNameAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto(string Name);

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    await Assert.That(generatedCode).Contains("EFCorePerspectiveConfigurationGenerator");
  }

  /// <summary>
  /// RED TEST: Generated diagnostic should report discovered perspective count.
  /// When 1 perspective is discovered, TotalDiscoveredCount should be 1.
  /// </summary>
  [Test]
  public async Task GeneratedDiagnostics_ReportsCorrectPerspectiveCountAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto(string Name);

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Should discover 1 perspective
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    // Should contain perspective count in the diagnostics output (no deduplication for single perspective)
    await Assert.That(generatedCode).Contains("1 perspective(s)");

    // TotalDiscoveredCount should return 1
    await Assert.That(generatedCode).Contains("TotalDiscoveredCount");
  }

  /// <summary>
  /// RED TEST: LogDiscoveryDiagnostics should output perspective details.
  /// When called, should log each discovered perspective with model type and table name.
  /// </summary>
  [Test]
  public async Task LogDiscoveryDiagnostics_OutputsPerspectiveDetailsAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto(string Name);

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Generated LogDiscoveryDiagnostics method should log perspective info
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    await Assert.That(generatedCode).Contains("LogDiscoveryDiagnostics");
    await Assert.That(generatedCode).Contains("ProductDto");
    await Assert.That(generatedCode).Contains("wh_per_product_dto");  // Whizbang table name with prefix
  }

  /// <summary>
  /// RED TEST: When no perspectives discovered, should report 0 and list fixed entities only.
  /// </summary>
  [Test]
  public async Task GeneratedDiagnostics_WithNoPerspectives_ReportsZeroAsync() {
    // Arrange - Source with no perspectives
    var source = @"
      using Whizbang.Core;

      namespace TestApp;

      public class SomeClass {
        public void DoSomething() { }
      }
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Should discover 0 perspectives
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    await Assert.That(generatedCode).Contains("0 perspective(s)");
    await Assert.That(generatedCode).Contains("InboxRecord");  // Fixed entities still configured
    await Assert.That(generatedCode).Contains("OutboxRecord");
    await Assert.That(generatedCode).Contains("EventStoreRecord");
  }

  /// <summary>
  /// RED TEST: Multiple perspectives with same model type should be deduplicated.
  /// Only 1 PerspectiveRow<ProductDto> configuration should be generated.
  /// </summary>
  [Test]
  public async Task GeneratedDiagnostics_DeduplicatesPerspectivesAsync() {
    // Arrange - Two perspectives using same model type
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto(string Name);

      public class ProductCreatedPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public class ProductUpdatedPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductUpdated> {
        public ProductDto Apply(ProductDto currentData, ProductUpdated @event) => currentData;
      }

      public record ProductCreated : IEvent;
      public record ProductUpdated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Should discover 1 unique model type (ProductDto)
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    await Assert.That(generatedCode).Contains("1 unique model type(s) from 2 perspective(s)");
  }

  #region Schema Configuration Tests

  /// <summary>
  /// Test that when schema is "public", HasDefaultSchema("public") is called.
  /// This is critical for EF Core to correctly resolve FindEntityType().GetSchema().
  /// Bug fix: Previously the condition "public" != "public" prevented this call.
  /// </summary>
  /// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/EFCoreConfigurationTemplate.cs:32</tests>
  [Test]
  public async Task GeneratedCode_WithPublicSchema_CallsHasDefaultSchemaAsync() {
    // Arrange - Source with no explicit schema (defaults to "public")
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto(string Name);

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - HasDefaultSchema("public") should be called (not skipped)
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    // The generated code should contain the HasDefaultSchema call
    await Assert.That(generatedCode).Contains("modelBuilder.HasDefaultSchema(\"public\")");

    // The condition should only check for non-empty, not exclude "public"
    await Assert.That(generatedCode).Contains("if (!string.IsNullOrEmpty(\"public\"))");

    // Should NOT have the old buggy condition that excluded "public"
    await Assert.That(generatedCode).DoesNotContain("\"public\" != \"public\"");
  }

  /// <summary>
  /// Test that when schema is a custom value, HasDefaultSchema is called with that value.
  /// Verifies custom schemas work correctly after the bug fix.
  /// Uses RunEFCoreGeneratorWithEFCoreReferencesAsync to enable DbContext discovery.
  /// </summary>
  [Test]
  public async Task GeneratedCode_WithCustomSchema_CallsHasDefaultSchemaWithCustomValueAsync() {
    // Arrange - Source with DbContext specifying custom schema
    // Note: Must use RunEFCoreGeneratorWithEFCoreReferencesAsync for DbContext discovery to work
    var source = @"
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp.InventoryWorker;

      public record InventoryItem(string Sku);

      public class InventoryPerspective(IPerspectiveStore<InventoryItem> store)
        : IPerspectiveFor<InventoryItem, ItemCreated> {
        public InventoryItem Apply(InventoryItem currentData, ItemCreated @event) => currentData;
      }

      public record ItemCreated : IEvent;

      [WhizbangDbContext(Schema = ""inventory"")]
      public class InventoryDbContext : DbContext {
        public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }
      }
    ";

    // Act - Use helper WITH EF Core references to enable DbContext schema discovery
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorWithEFCoreReferencesAsync(source);

    // Assert - HasDefaultSchema("inventory") should be called
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    // The generated code should contain HasDefaultSchema with the custom schema
    await Assert.That(generatedCode).Contains("modelBuilder.HasDefaultSchema(\"inventory\")");
  }

  /// <summary>
  /// Test that the schema condition correctly handles non-empty schemas.
  /// The condition should only check !string.IsNullOrEmpty, not compare to "public".
  /// </summary>
  [Test]
  public async Task GeneratedCode_SchemaCondition_OnlyChecksForNonEmptyAsync() {
    // Arrange - Any source that triggers generator
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto(string Name);

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Check the condition structure
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    // Should have the correct condition that only checks for non-empty
    await Assert.That(generatedCode).Contains("if (!string.IsNullOrEmpty(");

    // Should NOT have the buggy condition that also compares to "public"
    // The old bug was: if (!string.IsNullOrEmpty("public") && "public" != "public")
    await Assert.That(generatedCode).DoesNotContain("!= \"public\"");
  }

  /// <summary>
  /// Test that schema derived from namespace is used when no explicit schema is set.
  /// When no [WhizbangDbContext(Schema = ...)] specifies schema, it derives from namespace.
  /// Namespace "TestApp.InventoryWorker" -> schema "inventory" (Worker suffix removed).
  /// </summary>
  [Test]
  public async Task GeneratedCode_WithNamespaceDerivedSchema_CallsHasDefaultSchemaAsync() {
    // Arrange - Source with DbContext but no explicit Schema property
    // The schema should be derived from namespace: "TestApp.InventoryWorker" -> "inventory"
    var source = @"
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp.InventoryWorker;

      public record InventoryItem(string Sku);

      public class InventoryPerspective(IPerspectiveStore<InventoryItem> store)
        : IPerspectiveFor<InventoryItem, ItemCreated> {
        public InventoryItem Apply(InventoryItem currentData, ItemCreated @event) => currentData;
      }

      public record ItemCreated : IEvent;

      [WhizbangDbContext]  // No explicit Schema - should derive from namespace
      public class InventoryDbContext : DbContext {
        public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }
      }
    ";

    // Act - Use helper WITH EF Core references to enable DbContext discovery
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorWithEFCoreReferencesAsync(source);

    // Assert - HasDefaultSchema should be called with namespace-derived schema
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    // Should have HasDefaultSchema call with derived schema "inventory"
    // (namespace "TestApp.InventoryWorker" -> "inventory" after removing "Worker" suffix)
    await Assert.That(generatedCode).Contains("modelBuilder.HasDefaultSchema(\"inventory\")");

    // The condition should not skip based on "public" comparison
    await Assert.That(generatedCode).DoesNotContain("!= \"public\"");
  }

  /// <summary>
  /// Test that the generated comment explains why HasDefaultSchema is called for all schemas.
  /// Documentation helps future maintainers understand the fix.
  /// </summary>
  [Test]
  public async Task GeneratedCode_HasDefaultSchema_IncludesDocumentationCommentAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto(string Name);

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Should have explanatory comment about FindEntityType().GetSchema()
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    // Should contain the comment explaining the fix
    await Assert.That(generatedCode).Contains("FindEntityType");
    await Assert.That(generatedCode).Contains("GetSchema()");
  }

  #endregion
}
