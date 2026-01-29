using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for ScopedLensFactoryGenerator.
/// Validates discovery of lens types and generation of lens registry.
/// </summary>
/// <tests>Whizbang.Generators/ScopedLensFactoryGenerator.cs</tests>
[Category("Generators")]
[Category("Lenses")]
public class ScopedLensFactoryGeneratorTests {

  /// <summary>
  /// Test that generator discovers lens interface and generates registry.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithLensInterface_GeneratesRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string TenantId, string Status);

            public interface IOrderLens : ILensQuery<Order> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ScopedLensFactoryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "LensRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("IOrderLens");
    await Assert.That(code).Contains("Order");
  }

  /// <summary>
  /// Test that generator handles multiple lens types.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMultipleLenses_GeneratesAllAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);
            public record Product(Guid Id, string Name);
            public record Customer(Guid Id, string Email);

            public interface IOrderLens : ILensQuery<Order> { }
            public interface IProductLens : ILensQuery<Product> { }
            public interface ICustomerLens : ILensQuery<Customer> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ScopedLensFactoryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "LensRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("IOrderLens");
    await Assert.That(code).Contains("IProductLens");
    await Assert.That(code).Contains("ICustomerLens");
  }

  /// <summary>
  /// Test that generator discovers tenant-scoped models.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithTenantScopedModel_DetectsScopeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public interface ITenantScoped {
              string TenantId { get; }
            }

            public record Order(Guid Id, string TenantId, string Status) : ITenantScoped;

            public interface IOrderLens : ILensQuery<Order> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ScopedLensFactoryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "LensRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("TenantId");
  }

  /// <summary>
  /// Test that generator produces no output when no lenses exist.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithNoLenses_GeneratesEmptyRegistryAsync() {
    // Arrange
    var source = """
            using System;

            namespace TestApp;

            public record Order(Guid Id, string Status);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ScopedLensFactoryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "LensRegistry.g.cs");
    // Generator may produce empty registry or skip output entirely
    if (code is not null) {
      await Assert.That(code).Contains("LensRegistry");
    }
  }

  /// <summary>
  /// Test that generator produces compilable code.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ProducesCompilableCodeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);
            public interface IOrderLens : ILensQuery<Order> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ScopedLensFactoryGenerator>(source);

    // Assert - no compilation errors in generated code
    var errors = result.Diagnostics
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .ToArray();

    await Assert.That(errors).IsEmpty();
  }

  /// <summary>
  /// Test that generator handles lens with user scope.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithUserScopedModel_DetectsScopeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public interface IUserScoped {
              string UserId { get; }
            }

            public record UserSettings(Guid Id, string UserId, string Theme) : IUserScoped;

            public interface IUserSettingsLens : ILensQuery<UserSettings> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ScopedLensFactoryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "LensRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("UserId");
  }

  /// <summary>
  /// Test that generator generates model type information.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GeneratesModelTypeInfoAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);
            public interface IOrderLens : ILensQuery<Order> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ScopedLensFactoryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "LensRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("typeof");
    await Assert.That(code).Contains("Order");
  }

  /// <summary>
  /// Test that generator handles classes implementing ILensQuery directly.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithClassImplementingILensQuery_GeneratesRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Product(Guid Id, string Name);

            public class ProductLens : ILensQuery<Product> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ScopedLensFactoryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "LensRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("ProductLens");
  }
}
