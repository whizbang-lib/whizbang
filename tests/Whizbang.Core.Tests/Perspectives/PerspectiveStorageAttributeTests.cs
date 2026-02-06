using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="PerspectiveStorageAttribute"/>.
/// Validates attribute behavior, properties, and targeting rules.
/// </summary>
/// <docs>perspectives/physical-fields</docs>
[Category("Core")]
[Category("Attributes")]
[Category("PhysicalFields")]
public class PerspectiveStorageAttributeTests {
  [Test]
  public async Task PerspectiveStorageAttribute_Constructor_SetsModeAsync() {
    // Arrange & Act
    var attribute = new PerspectiveStorageAttribute(FieldStorageMode.Split);

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute.Mode).IsEqualTo(FieldStorageMode.Split);
  }

  [Test]
  public async Task PerspectiveStorageAttribute_Constructor_AcceptsExtractedModeAsync() {
    // Arrange & Act
    var attribute = new PerspectiveStorageAttribute(FieldStorageMode.Extracted);

    // Assert
    await Assert.That(attribute.Mode).IsEqualTo(FieldStorageMode.Extracted);
  }

  [Test]
  public async Task PerspectiveStorageAttribute_Constructor_AcceptsJsonOnlyModeAsync() {
    // Arrange & Act
    var attribute = new PerspectiveStorageAttribute(FieldStorageMode.JsonOnly);

    // Assert
    await Assert.That(attribute.Mode).IsEqualTo(FieldStorageMode.JsonOnly);
  }

  [Test]
  public async Task PerspectiveStorageAttribute_AttributeUsage_AllowsClassTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PerspectiveStorageAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Class)).IsTrue();
  }

  [Test]
  public async Task PerspectiveStorageAttribute_AttributeUsage_AllowsStructTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PerspectiveStorageAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Struct)).IsTrue();
  }

  [Test]
  public async Task PerspectiveStorageAttribute_AttributeUsage_DoesNotAllowMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PerspectiveStorageAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task PerspectiveStorageAttribute_AttributeUsage_IsNotInheritedAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PerspectiveStorageAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert - not inherited to avoid confusion with derived types
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.Inherited).IsFalse();
  }

  [Test]
  public async Task PerspectiveStorageAttribute_IsSealedAsync() {
    // Assert - attribute class should be sealed for performance
    await Assert.That(typeof(PerspectiveStorageAttribute).IsSealed).IsTrue();
  }
}
