using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for WhizbangIdGenerator Guid storage.
/// Ensures generated IDs use Guid backing for EF Core ComplexProperty compatibility
/// and implement IWhizbangId.
/// </summary>
[Category("SourceGenerators")]
public class WhizbangIdGeneratorTrackedGuidTests {
  /// <summary>
  /// Test that generated ID uses TrackedGuid backing field for metadata tracking.
  /// EF Core sees only the Guid Value property.
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

    // Should use TrackedGuid backing field for metadata tracking
    await Assert.That(generatedSource!).Contains("TrackedGuid _tracked");
    // EF Core sees only the Guid Value property
    await Assert.That(generatedSource).Contains("public Guid Value { get => _tracked.Value; init => _tracked = TrackedGuid.FromExternal(value); }");
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
  /// Test that generated ID has IsTimeOrdered property delegating to TrackedGuid.
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

    // Should have IsTimeOrdered delegating to _tracked
    await Assert.That(generatedSource!).Contains("IsTimeOrdered => _tracked.IsTimeOrdered");
  }

  /// <summary>
  /// Test that generated ID has SubMillisecondPrecision property that delegates to TrackedGuid.
  /// Fresh IDs via New() return true, deserialized IDs return false.
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

    // Should have SubMillisecondPrecision delegating to _tracked
    await Assert.That(generatedSource!).Contains("SubMillisecondPrecision => _tracked.SubMillisecondPrecision");
    // Also has public convenience method delegating to _tracked
    await Assert.That(generatedSource).Contains("GetSubMillisecondPrecision() => _tracked.SubMillisecondPrecision");
  }

  /// <summary>
  /// Test that generated ID has Timestamp property delegating to TrackedGuid.
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

    // Should have Timestamp delegating to _tracked
    await Assert.That(generatedSource!).Contains("DateTimeOffset");
    await Assert.That(generatedSource).Contains("Timestamp => _tracked.Timestamp");
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

    // Should have ToGuid method that returns _tracked.Value
    await Assert.That(generatedSource!).Contains("Guid ToGuid()");
    await Assert.That(generatedSource).Contains("ToGuid() => _tracked.Value");
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
