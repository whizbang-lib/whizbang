using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for WhizbangIdGenerator TrackedGuid integration.
/// Ensures generated IDs use TrackedGuid backing and implement IWhizbangId.
/// </summary>
[Category("SourceGenerators")]
public class WhizbangIdGeneratorTrackedGuidTests {
  /// <summary>
  /// Test that generated ID uses TrackedGuid backing field.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratedId_UsesTrackedGuidBackingFieldAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should use TrackedGuid backing field instead of Guid
    await Assert.That(generatedSource!).Contains("TrackedGuid _tracked");
    await Assert.That(generatedSource).DoesNotContain("Guid _value");
  }

  /// <summary>
  /// Test that generated New() method calls TrackedGuid.NewMedo().
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratedNew_CallsTrackedGuidNewMedoAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should use TrackedGuid.NewMedo() for ID generation
    await Assert.That(generatedSource!).Contains("TrackedGuid.NewMedo()");
  }

  /// <summary>
  /// Test that generated ID implements IWhizbangId interface.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratedId_ImplementsIWhizbangIdAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should implement IWhizbangId interface
    await Assert.That(generatedSource!).Contains("IWhizbangId");
    await Assert.That(generatedSource).Contains("global::Whizbang.Core.IWhizbangId");
  }

  /// <summary>
  /// Test that generated ID has IsTimeOrdered property.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratedId_HasIsTimeOrderedPropertyAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should have IsTimeOrdered property that delegates to TrackedGuid
    await Assert.That(generatedSource!).Contains("bool IsTimeOrdered");
    await Assert.That(generatedSource).Contains("_tracked.IsTimeOrdered");
  }

  /// <summary>
  /// Test that generated ID has SubMillisecondPrecision property.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratedId_HasSubMillisecondPrecisionPropertyAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should have SubMillisecondPrecision property that delegates to TrackedGuid
    await Assert.That(generatedSource!).Contains("bool SubMillisecondPrecision");
    await Assert.That(generatedSource).Contains("_tracked.SubMillisecondPrecision");
  }

  /// <summary>
  /// Test that generated ID has Timestamp property.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratedId_HasTimestampPropertyAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should have Timestamp property that delegates to TrackedGuid
    await Assert.That(generatedSource!).Contains("DateTimeOffset Timestamp");
    await Assert.That(generatedSource).Contains("_tracked.Timestamp");
  }

  /// <summary>
  /// Test that generated ID has ToGuid() method for IWhizbangId.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratedId_HasToGuidMethodAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should have ToGuid method
    await Assert.That(generatedSource!).Contains("Guid ToGuid()");
    await Assert.That(generatedSource).Contains("_tracked.Value");
  }

  /// <summary>
  /// Test that generated From(Guid) validates v7.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratedFrom_WithNonV7Guid_ThrowsAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should validate UUIDv7 in From(Guid) method
    await Assert.That(generatedSource!).Contains("Version != 7");
    await Assert.That(generatedSource).Contains("ArgumentException");
    await Assert.That(generatedSource).Contains("UUIDv7");
  }

  /// <summary>
  /// Test that generated ID has From(TrackedGuid) method that preserves metadata.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratedId_HasFromTrackedGuidMethodAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should have From(TrackedGuid) method
    await Assert.That(generatedSource!).Contains("From(TrackedGuid");
  }
}
