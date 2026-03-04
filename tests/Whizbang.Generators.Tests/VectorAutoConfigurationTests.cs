using TUnit.Assertions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// TDD tests for automatic pgvector configuration.
/// Verifies that HasPostgresExtension("vector") is generated when [VectorField] is used.
/// </summary>
/// <docs>features/vector-search#auto-config</docs>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/EFCorePerspectiveConfigurationGenerator.cs</tests>
[Category("Unit")]
public class VectorAutoConfigurationTests {
  /// <summary>
  /// RED TEST: When a perspective model has [VectorField], generated code should include HasPostgresExtension("vector").
  /// This ensures pgvector extension is automatically created in the database.
  /// </summary>
  [Test]
  public async Task ConfigureWhizbang_WithVectorField_GeneratesHasPostgresExtensionAsync() {
    // Arrange - Model with [VectorField] attribute
    var source = @"
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;

        [VectorField(1536)]
        public float[]? Embedding { get; init; }
      }

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Generated code should include HasPostgresExtension("vector")
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    await Assert.That(generatedCode).Contains("HasPostgresExtension(\"vector\")");
  }

  /// <summary>
  /// RED TEST: When no perspectives have [VectorField], generated code should NOT include HasPostgresExtension.
  /// This avoids unnecessary database extension installation.
  /// </summary>
  [Test]
  public async Task ConfigureWhizbang_WithoutVectorField_DoesNotGenerateHasPostgresExtensionAsync() {
    // Arrange - Model without [VectorField] attribute
    var source = @"
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
      }

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Generated code should NOT include HasPostgresExtension
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    await Assert.That(generatedCode).DoesNotContain("HasPostgresExtension");
  }

  /// <summary>
  /// RED TEST: Multiple perspectives - only one HasPostgresExtension call needed.
  /// Even with multiple vector fields, only one extension call is required.
  /// </summary>
  [Test]
  public async Task ConfigureWhizbang_MultipleVectorFields_GeneratesSingleHasPostgresExtensionAsync() {
    // Arrange - Multiple models with [VectorField]
    var source = @"
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto {
        public Guid Id { get; init; }
        [VectorField(1536)]
        public float[]? Embedding { get; init; }
      }

      public record ArticleDto {
        public Guid Id { get; init; }
        [VectorField(768)]
        public float[]? ContentEmbedding { get; init; }
      }

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public class ArticlePerspective(IPerspectiveStore<ArticleDto> store)
        : IPerspectiveFor<ArticleDto, ArticleCreated> {
        public ArticleDto Apply(ArticleDto currentData, ArticleCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
      public record ArticleCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Generated code should have exactly one HasPostgresExtension call
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    await Assert.That(generatedCode).Contains("HasPostgresExtension(\"vector\")");

    // Count occurrences - should be exactly 1
    var count = generatedCode.Split(["HasPostgresExtension"], StringSplitOptions.None).Length - 1;
    await Assert.That(count).IsEqualTo(1);
  }

  /// <summary>
  /// RED TEST: Mixed models - vector config generated if ANY model has vector field.
  /// </summary>
  [Test]
  public async Task ConfigureWhizbang_MixedModels_GeneratesHasPostgresExtensionAsync() {
    // Arrange - One model with [VectorField], one without
    var source = @"
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
      }

      public record ArticleDto {
        public Guid Id { get; init; }
        [VectorField(768)]
        public float[]? Embedding { get; init; }
      }

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public class ArticlePerspective(IPerspectiveStore<ArticleDto> store)
        : IPerspectiveFor<ArticleDto, ArticleCreated> {
        public ArticleDto Apply(ArticleDto currentData, ArticleCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
      public record ArticleCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - Generated code should include HasPostgresExtension (because ArticleDto has vector)
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    await Assert.That(generatedCode).Contains("HasPostgresExtension(\"vector\")");
  }

  /// <summary>
  /// RED TEST: HasPostgresExtension should be called early in ConfigureWhizbang, before entity configuration.
  /// This ensures the extension is available before column type mappings are applied.
  /// </summary>
  [Test]
  public async Task ConfigureWhizbang_WithVectorField_HasPostgresExtensionCalledBeforeEntityConfigAsync() {
    // Arrange
    var source = @"
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ProductDto {
        public Guid Id { get; init; }
        [VectorField(1536)]
        public float[]? Embedding { get; init; }
      }

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;
    ";

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorAsync(source);

    // Assert - HasPostgresExtension should appear before entity configuration
    var generatedCode = result.GeneratedSources
      .First(s => s.HintName == "WhizbangModelBuilderExtensions.g.cs")
      .SourceText.ToString();

    var extensionIndex = generatedCode.IndexOf("HasPostgresExtension(\"vector\")", StringComparison.Ordinal);
    var entityConfigIndex = generatedCode.IndexOf("Entity<PerspectiveRow", StringComparison.Ordinal);

    await Assert.That(extensionIndex).IsGreaterThan(-1);
    await Assert.That(entityConfigIndex).IsGreaterThan(-1);
    await Assert.That(extensionIndex).IsLessThan(entityConfigIndex);
  }

  /// <summary>
  /// RED TEST: Turnkey extension method should be generated for DbContext.
  /// </summary>
  [Test]
  public async Task TurnkeyExtension_WithVectorField_GeneratesAddDbContextMethodAsync() {
    // Arrange - Model with [VectorField] and DbContext
    var source = @"
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record ProductDto {
        public Guid Id { get; init; }
        [VectorField(1536)]
        public float[]? Embedding { get; init; }
      }

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
    ";

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - Should generate turnkey extension with UseVector()
    var extensionSource = result.GeneratedSources
      .FirstOrDefault(s => s.HintName == "TestDbContextExtensions.g.cs");

    await Assert.That(extensionSource).IsNotNull();
    var code = extensionSource!.SourceText.ToString();

    // Should have AddTestDbContext method
    await Assert.That(code).Contains("public static IServiceCollection AddTestDbContext(");
    // Should configure UseVector() on data source builder
    await Assert.That(code).Contains("dataSourceBuilder.UseVector()");
    // Should configure UseVector() on EF Core options
    await Assert.That(code).Contains("npgsqlOptions.UseVector()");
  }

  /// <summary>
  /// RED TEST: Turnkey extension without vector fields should not include UseVector().
  /// </summary>
  [Test]
  public async Task TurnkeyExtension_WithoutVectorField_DoesNotIncludeUseVectorAsync() {
    // Arrange - Model without [VectorField] and DbContext
    var source = @"
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record ProductDto {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
      }

      public class ProductPerspective(IPerspectiveStore<ProductDto> store)
        : IPerspectiveFor<ProductDto, ProductCreated> {
        public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
      }

      public record ProductCreated : IEvent;

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
    ";

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - Should generate turnkey extension WITHOUT UseVector()
    var extensionSource = result.GeneratedSources
      .FirstOrDefault(s => s.HintName == "TestDbContextExtensions.g.cs");

    await Assert.That(extensionSource).IsNotNull();
    var code = extensionSource!.SourceText.ToString();

    // Should have AddTestDbContext method
    await Assert.That(code).Contains("public static IServiceCollection AddTestDbContext(");
    // Should NOT include UseVector()
    await Assert.That(code).DoesNotContain("UseVector()");
    // Should NOT include pgvector usings
    await Assert.That(code).DoesNotContain("Pgvector");
  }
}
