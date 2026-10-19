// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
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
  // The names the generator derives, computed here so each test sits exactly where it claims: a table is
  // "wh_per_" plus the model name in snake case (a lowercase name is unchanged and strips no suffix), and an
  // index is "ix_{table}_{column}". Postgres allows 63 bytes (#1431).
  private const string TABLE_PREFIX = "wh_per_";
  private const string INDEX_PREFIX = "ix_";
  private const int LIMIT = 63;

  // A lowercase identifier of exactly the given length; its snake case is itself.
  private static string _name(int length) => "z" + new string('q', length - 1);

  private static string _indexedFieldSource(string model, string column, bool unique) => $$"""
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record {{model}} {
        public string Name { get; init; } = "";

        {{(unique ? "[PhysicalField(Unique = true)]" : "[PhysicalField]\n        [Indexed]")}}
        public string {{column}} { get; init; } = "";
      }

      public class TestPerspective(IPerspectiveStore<{{model}}> store) : IPerspectiveFor<{{model}}, ProductCreated> {
        public {{model}} Apply({{model}} currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
      """;

  private static string _modelSource(string model) => $$"""
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record {{model}}(string Name);

      public class TestPerspective(IPerspectiveStore<{{model}}> store) : IPerspectiveFor<{{model}}, ProductCreated> {
        public {{model}} Apply({{model}} currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
      """;

  private static string[] _limitIds(GeneratorResult result) =>
    [.. result.Diagnostics.Select(static d => d.Id).Where(static id => id is "WHIZ820" or "WHIZ821" or "WHIZ822").Order(StringComparer.Ordinal)];

  /// <summary>
  /// RED TEST: Generated code should implement IWhizbangDiscoveryDiagnostics interface.
  /// This ensures consumers can use a consistent API across all generators.
  /// </summary>
  [Test]
  public async Task GeneratedCode_ImplementsIDiagnosticsInterfaceAsync() {
    // Arrange
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    // ProductDto → wh_per_product (Dto suffix stripped by default configuration)
    await Assert.That(generatedCode).Contains("wh_per_product");
  }

  /// <summary>
  /// RED TEST: When no perspectives discovered, should report 0 and list fixed entities only.
  /// </summary>
  [Test]
  public async Task GeneratedDiagnostics_WithNoPerspectives_ReportsZeroAsync() {
    // Arrange - Source with no perspectives
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = """

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

      [WhizbangDbContext(Schema = "inventory")]
      public class InventoryDbContext : DbContext {
        public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }
      }
    
""";

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
    const string source = @"
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
    // The old bug compared the schema literal with itself, so the guard could never fire.
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
    const string source = @"
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
    const string source = @"
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

  #region Identifier Length Validation Diagnostics (WHIZ820-822)

  /// <summary>
  /// Test that WHIZ820 error is emitted when perspective model generates a table name exceeding 63 bytes.
  /// PostgreSQL NAMEDATALEN is 64, so max identifier is 63 bytes.
  /// Table name format: wh_per_ + snake_case(model_name)
  /// </summary>
  [Test]
  public async Task Generator_WithLongTableName_EmitsWHIZ820ErrorAsync() {
    // Arrange - Create a model with a very long name that exceeds 63 bytes when converted to table name
    // Table name format: wh_per_ (7 bytes) + snake_case name
    // We need total > 63 bytes, so model name part needs > 56 bytes in snake_case
    // "VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres" → ~70+ bytes
    const string source = @"
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres(string Name);

      public class TestPerspective(IPerspectiveStore<VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres> store)
        : IPerspectiveFor<VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres, ProductCreated> {
        public VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres Apply(VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - WHIZ820 diagnostic should be emitted
    var whiz820 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ820");
    await Assert.That(whiz820).IsNotNull();
    await Assert.That(whiz820!.Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    await Assert.That(whiz820.GetMessage(CultureInfo.InvariantCulture)).Contains("exceeds");
    await Assert.That(whiz820.GetMessage(CultureInfo.InvariantCulture)).Contains("PostgreSQL");
    await Assert.That(whiz820.GetMessage(CultureInfo.InvariantCulture)).Contains("63 bytes");
  }

  /// <summary>
  /// Test that no WHIZ820 error is emitted when perspective model generates a table name within 63 bytes.
  /// </summary>
  [Test]
  public async Task Generator_WithShortTableName_DoesNotEmitWHIZ820ErrorAsync() {
    // Arrange - Create a model with a short name
    const string source = @"
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

    // Assert - WHIZ820 diagnostic should NOT be emitted
    var whiz820 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ820");
    await Assert.That(whiz820).IsNull();
  }

  /// <summary>
  /// Test that WHIZ821 error is emitted when a physical field column name exceeds 63 bytes.
  /// </summary>
  [Test]
  public async Task Generator_WithLongColumnName_EmitsWHIZ821ErrorAsync() {
    // Arrange - Create a model with a physical field with a very long property name
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto {
        public string Name { get; init; } = "";

        [PhysicalField]
        [Indexed]
        public string VeryLongPropertyNameThatWillExceedTheSixtyThreeByteLimitForPostgresColumnNames { get; init; } = "";
      }

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    
""";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - WHIZ821 diagnostic should be emitted
    var whiz821 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ821");
    await Assert.That(whiz821).IsNotNull();
    await Assert.That(whiz821!.Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    await Assert.That(whiz821.GetMessage(CultureInfo.InvariantCulture)).Contains("column name");
    await Assert.That(whiz821.GetMessage(CultureInfo.InvariantCulture)).Contains("exceeds");
    await Assert.That(whiz821.GetMessage(CultureInfo.InvariantCulture)).Contains("PostgreSQL");
    await Assert.That(DiagnosticSourceText.Of(whiz821)).IsEqualTo("ProductDto")
      .Because("it is reported at the model's declaration, so it can be suppressed there (#1429)");
  }

  /// <summary>
  /// Test that no WHIZ821 error is emitted when physical field column name is within 63 bytes.
  /// </summary>
  [Test]
  public async Task Generator_WithShortColumnName_DoesNotEmitWHIZ821ErrorAsync() {
    // Arrange - Create a model with a physical field with a short property name
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto {
        public string Name { get; init; } = "";

        [PhysicalField]
        [Indexed]
        public string Status { get; init; } = "";
      }

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    
""";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - WHIZ821 diagnostic should NOT be emitted
    var whiz821 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ821");
    await Assert.That(whiz821).IsNull();
  }

  /// <summary>
  /// WHIZ822, and only WHIZ822, when the table and column names are each within the limit but the index name
  /// built from them is not. Index name format: ix_{table_name}_{column_name}.
  /// </summary>
  [Test]
  public async Task Generator_WithLongIndexName_EmitsWHIZ822ErrorAsync() {
    // ix_ (3) + wh_per_order (12) + _ (1) + a 48-byte column = 64 bytes: one over.
    var column = _name(LIMIT - INDEX_PREFIX.Length - "wh_per_order".Length - 1 + 1);
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(_indexedFieldSource("order", column, unique: false));

    await Assert.That(_limitIds(result)).IsEquivalentTo(["WHIZ822"]);
    var whiz822 = result.Diagnostics.Single(d => d.Id == "WHIZ822");
    await Assert.That(whiz822.Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    await Assert.That(whiz822.GetMessage(CultureInfo.InvariantCulture)).Contains($"ix_wh_per_order_{column}");
    await Assert.That(whiz822.GetMessage(CultureInfo.InvariantCulture)).Contains("(64 bytes)");
    await Assert.That(DiagnosticSourceText.Of(whiz822)).IsEqualTo("order")
      .Because("it is reported at the model's declaration, so it can be suppressed there (#1429)");
  }

  /// <summary>An index name of exactly 63 bytes is within the limit: no WHIZ822.</summary>
  [Test]
  public async Task Generator_WithExactly63ByteIndexName_DoesNotEmitWHIZ822ErrorAsync() {
    var column = _name(LIMIT - INDEX_PREFIX.Length - "wh_per_order".Length - 1);
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(_indexedFieldSource("order", column, unique: false));

    await Assert.That($"ix_wh_per_order_{column}".Length).IsEqualTo(LIMIT);
    await Assert.That(_limitIds(result)).IsEmpty();
  }

  /// <summary>
  /// Test that no WHIZ822 error is emitted when index name is within 63 bytes.
  /// </summary>
  [Test]
  public async Task Generator_WithShortIndexName_DoesNotEmitWHIZ822ErrorAsync() {
    // Arrange - Create a model with short names
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto {
        public string Name { get; init; } = "";

        [PhysicalField]
        [Indexed]
        public string Status { get; init; } = "";
      }

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    
""";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - WHIZ822 diagnostic should NOT be emitted
    var whiz822 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ822");
    await Assert.That(whiz822).IsNull();
  }

  /// <summary>
  /// A table name of exactly 63 bytes does not emit WHIZ820, and one byte more does.
  /// </summary>
  [Test]
  public async Task Generator_WithExactly63ByteTableName_DoesNotEmitWHIZ820ErrorAsync() {
    var model = _name(LIMIT - TABLE_PREFIX.Length);
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(_modelSource(model));
    var generated = result.GeneratedSources.First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs").SourceText.ToString();

    await Assert.That((TABLE_PREFIX + model).Length).IsEqualTo(LIMIT);
    await Assert.That(generated).Contains($"\"{TABLE_PREFIX}{model}\"")
      .Because("the test is only at the boundary if the generator derives the table name it computes");
    await Assert.That(_limitIds(result)).IsEmpty();
  }

  /// <summary>A table name of 64 bytes emits WHIZ820.</summary>
  [Test]
  public async Task Generator_With64ByteTableName_EmitsWHIZ820ErrorAsync() {
    var model = _name(LIMIT - TABLE_PREFIX.Length + 1);
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(_modelSource(model));

    await Assert.That(_limitIds(result)).IsEquivalentTo(["WHIZ820"]);
    await Assert.That(result.Diagnostics.Single(d => d.Id == "WHIZ820").GetMessage(CultureInfo.InvariantCulture)).Contains("(64 bytes)");
    await Assert.That(DiagnosticSourceText.Of(result.Diagnostics.Single(d => d.Id == "WHIZ820"))).IsEqualTo(model);
  }

  /// <summary>
  /// Test that perspective with identifier limit error is excluded from generated output.
  /// </summary>
  [Test]
  public async Task Generator_WithIdentifierError_ExcludesPerspectiveFromOutputAsync() {
    // Arrange - Create one valid and one invalid perspective
    const string source = @"
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      // Valid short model
      public record ValidDto(string Name);

      // Invalid long model
      public record VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres(string Name);

      public class ValidPerspective(IPerspectiveStore<ValidDto> store)
        : IPerspectiveFor<ValidDto, ProductCreated> {
        public ValidDto Apply(ValidDto currentData, ProductCreated @event) => currentData;
      }

      public class InvalidPerspective(IPerspectiveStore<VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres> store)
        : IPerspectiveFor<VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres, ProductCreated> {
        public VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres Apply(VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - WHIZ820 should be emitted for the invalid perspective
    var whiz820 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ820");
    await Assert.That(whiz820).IsNotNull();

    // Valid perspective should still be in generated output
    var generatedCode = result.GeneratedSources
        .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
        .SourceText.ToString();

    await Assert.That(generatedCode).Contains("ValidDto");
    await Assert.That(generatedCode).Contains("wh_per_valid");

    // Invalid perspective should NOT be in generated output
    await Assert.That(generatedCode).DoesNotContain("VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres");
  }

  /// <summary>
  /// A unique physical field's index is validated too: WHIZ822, and only WHIZ822, for a unique field whose
  /// column fits but whose index name does not.
  /// </summary>
  [Test]
  public async Task Generator_WithLongUniqueFieldIndexName_EmitsWHIZ822ErrorAsync() {
    var column = _name(LIMIT - INDEX_PREFIX.Length - "wh_per_order".Length - 1 + 1);
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(_indexedFieldSource("order", column, unique: true));

    await Assert.That(_limitIds(result)).IsEquivalentTo(["WHIZ822"]);
  }

  #endregion
}
