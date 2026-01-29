using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for WhizbangIdGenerator - ensures strongly-typed ID generation with UUIDv7 support.
/// Following TDD: These tests are written BEFORE the generator implementation.
/// All tests should FAIL initially (RED phase), then pass after implementation (GREEN phase).
/// </summary>
public partial class WhizbangIdGeneratorTests {
  /// <summary>
  /// Test that type-based discovery generates a complete value object.
  /// Usage: [WhizbangId] public readonly partial struct ProductId;
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithExplicitTypeDeclaration_GeneratesValueObjectAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Generator should produce ProductId.g.cs
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Assert - Should contain struct declaration with correct namespace
    await Assert.That(generatedSource!).Contains("namespace MyApp.Domain");
    await Assert.That(generatedSource).Contains("public readonly partial struct ProductId");

    // Assert - Should contain TrackedGuid-backed storage
    await Assert.That(generatedSource).Contains("private readonly TrackedGuid _tracked");
    await Assert.That(generatedSource).Contains("public Guid Value => _tracked.Value");

    // Assert - Should contain factory methods
    await Assert.That(generatedSource).Contains("public static ProductId From(Guid value)");
    await Assert.That(generatedSource).Contains("public static ProductId New()");

    // Assert - Should contain IEquatable implementation
    await Assert.That(generatedSource).Contains("IEquatable<ProductId>");

    // Assert - Should generate JSON converter with UUIDv7
    var converterSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductIdJsonConverter.g.cs");
    await Assert.That(converterSource).IsNotNull();
    await Assert.That(converterSource!).Contains("Uuid7");
  }

  /// <summary>
  /// Test that multiple ID types are generated independently.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleIdTypes_GeneratesAllAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;

            [WhizbangId]
            public readonly partial struct OrderId;

            [WhizbangId]
            public readonly partial struct CustomerId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should generate all three ID types
    var productIdSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    var orderIdSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderId.g.cs");
    var customerIdSource = GeneratorTestHelper.GetGeneratedSource(result, "CustomerId.g.cs");

    await Assert.That(productIdSource).IsNotNull();
    await Assert.That(orderIdSource).IsNotNull();
    await Assert.That(customerIdSource).IsNotNull();

    // Assert - Each should be independent
    await Assert.That(productIdSource!).Contains("struct ProductId");
    await Assert.That(orderIdSource!).Contains("struct OrderId");
    await Assert.That(customerIdSource!).Contains("struct CustomerId");
  }

  /// <summary>
  /// Test that namespace can be overridden via attribute parameter.
  /// Usage: [WhizbangId("Custom.Namespace")] public readonly partial struct ProductId;
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithCustomNamespace_UsesSpecifiedNamespaceAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId("MyApp.Custom.Ids")]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should use custom namespace
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("namespace MyApp.Custom.Ids");
    await Assert.That(generatedSource).DoesNotContain("namespace MyApp.Domain");
  }

  /// <summary>
  /// Test that namespace can be overridden via property initializer.
  /// Usage: [WhizbangId] { Namespace = "Custom.Namespace" } public readonly partial struct ProductId;
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNamespaceProperty_UsesSpecifiedNamespaceAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId(Namespace = "MyApp.Ids")]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should use custom namespace
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("namespace MyApp.Ids");
  }

  /// <summary>
  /// Test that non-partial struct produces diagnostic.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNonPartialStruct_ProducesDiagnosticAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly struct ProductId;  // Missing 'partial' keyword
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should produce WHIZ021 warning
    var diagnostics = result.Diagnostics;
    var warning = diagnostics.FirstOrDefault(d => d.Id == "WHIZ021");
    await Assert.That(warning).IsNotNull();
    await Assert.That(warning!.Severity).IsEqualTo(DiagnosticSeverity.Warning);
  }

  /// <summary>
  /// Test that generator produces JSON converter for each ID type.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithIdType_GeneratesJsonConverterAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should generate ProductIdJsonConverter.g.cs
    var converterSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductIdJsonConverter.g.cs");
    await Assert.That(converterSource).IsNotNull();

    // Assert - Should extend JsonConverter
    await Assert.That(converterSource!).Contains("JsonConverter<ProductId>");
    await Assert.That(converterSource).Contains("class ProductIdJsonConverter");
  }

  /// <summary>
  /// Test property-based discovery - [WhizbangId] on property type.
  /// Usage: public class Foo { [WhizbangId] public ProductId Id { get; set; } }
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPropertyBasedDiscovery_GeneratesValueObjectAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Commands;

            public class CreateProductCommand {
              [WhizbangId]
              public ProductId Id { get; set; }
              public string Name { get; set; } = string.Empty;
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should generate ProductId.g.cs in same namespace as declaring type
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("namespace MyApp.Commands");
    await Assert.That(generatedSource).Contains("public readonly partial struct ProductId");
  }

  /// <summary>
  /// Test parameter-based discovery - [WhizbangId] on primary constructor parameter.
  /// Usage: public record CreateProductCommand([WhizbangId] ProductId Id);
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithParameterBasedDiscovery_GeneratesValueObjectAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Commands;

            public record CreateProductCommand(
              [WhizbangId] ProductId Id,
              string Name
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should generate ProductId.g.cs in same namespace as declaring type
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("namespace MyApp.Commands");
    await Assert.That(generatedSource).Contains("public readonly partial struct ProductId");
  }

  /// <summary>
  /// Test hybrid discovery - multiple patterns in same compilation.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithHybridDiscovery_GeneratesAllIdsAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            // Explicit type
            [WhizbangId]
            public readonly partial struct OrderId;

            // Property-based
            public class CreateProductCommand {
              [WhizbangId]
              public ProductId Id { get; set; }
            }

            // Parameter-based
            public record CreateCustomerCommand(
              [WhizbangId] CustomerId Id,
              string Name
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should generate all three ID types
    var orderIdSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderId.g.cs");
    var productIdSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    var customerIdSource = GeneratorTestHelper.GetGeneratedSource(result, "CustomerId.g.cs");

    await Assert.That(orderIdSource).IsNotNull();
    await Assert.That(productIdSource).IsNotNull();
    await Assert.That(customerIdSource).IsNotNull();
  }

  /// <summary>
  /// Test that property-based discovery respects custom namespace.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPropertyBasedAndCustomNamespace_UsesCustomNamespaceAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Commands;

            public class CreateProductCommand {
              [WhizbangId(Namespace = "MyApp.Domain.Ids")]
              public ProductId Id { get; set; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should use custom namespace
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("namespace MyApp.Domain.Ids");
    await Assert.That(generatedSource).DoesNotContain("namespace MyApp.Commands");
  }

  /// <summary>
  /// Test deduplication - same ID discovered via multiple patterns should only generate once.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithDuplicateDiscovery_GeneratesOnlyOnceAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            // Explicit type declaration
            [WhizbangId]
            public readonly partial struct ProductId;

            // Property-based discovery (same type)
            public class CreateProductCommand {
              [WhizbangId]
              public ProductId Id { get; set; }
            }

            // Parameter-based discovery (same type)
            public record UpdateProductCommand(
              [WhizbangId] ProductId Id,
              string Name
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should generate ProductId only once
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Count occurrences of struct declaration (should be exactly 1)
    var structDeclarationCount = MyRegex().Count(generatedSource);
    await Assert.That(structDeclarationCount).IsEqualTo(1);
  }

  /// <summary>
  /// Test collision detection - same type name in different namespaces should warn.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithCollision_EmitsDiagnosticAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;

            namespace MyApp.Commands;

            public class CreateProductCommand {
              [WhizbangId(Namespace = "MyApp.Commands")]
              public ProductId Id { get; set; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should emit WHIZ024 warning for collision
    var diagnostics = result.Diagnostics;
    var warning = diagnostics.FirstOrDefault(d => d.Id == "WHIZ024");
    await Assert.That(warning).IsNotNull();
    await Assert.That(warning!.Severity).IsEqualTo(DiagnosticSeverity.Warning);

    // Should still generate both (in different namespaces with qualified names)
    var domainSource = GeneratorTestHelper.GetGeneratedSource(result, "MyAppDomain.ProductId.g.cs");
    var commandsSource = GeneratorTestHelper.GetGeneratedSource(result, "MyAppCommands.ProductId.g.cs");
    await Assert.That(domainSource).IsNotNull();
    await Assert.That(commandsSource).IsNotNull();
  }

  /// <summary>
  /// Test that SuppressDuplicateWarning suppresses the collision warning.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithCollisionSuppressed_NoWarningAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;

            namespace MyApp.Commands;

            public class CreateProductCommand {
              [WhizbangId(Namespace = "MyApp.Commands", SuppressDuplicateWarning = true)]
              public ProductId Id { get; set; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should NOT emit WHIZ024 warning
    var diagnostics = result.Diagnostics;
    var warning = diagnostics.FirstOrDefault(d => d.Id == "WHIZ024");
    await Assert.That(warning).IsNull();
  }

  /// <summary>
  /// Test that generator creates CreateProvider() static method on value object.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesCreateProviderMethodAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct TestId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should generate CreateProvider method
    var valueObjectSource = GeneratorTestHelper.GetGeneratedSource(result, "TestId.g.cs");
    await Assert.That(valueObjectSource).IsNotNull();
    await Assert.That(valueObjectSource!).Contains("public static global::Whizbang.Core.IWhizbangIdProvider<TestId> CreateProvider(");
    await Assert.That(valueObjectSource).Contains("return new TestIdProvider(baseProvider);");
  }

  [System.Text.RegularExpressions.GeneratedRegex(@"public readonly partial struct ProductId"
  )]
  private static partial System.Text.RegularExpressions.Regex MyRegex();
}
