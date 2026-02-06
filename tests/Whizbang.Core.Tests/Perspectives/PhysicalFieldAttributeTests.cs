using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="PhysicalFieldAttribute"/>.
/// Validates attribute behavior, properties, and targeting rules.
/// </summary>
/// <docs>perspectives/physical-fields</docs>
[Category("Core")]
[Category("Attributes")]
[Category("PhysicalFields")]
public class PhysicalFieldAttributeTests {
  [Test]
  public async Task PhysicalFieldAttribute_DefaultConstructor_CreatesInstanceAsync() {
    // Arrange & Act
    var attribute = new PhysicalFieldAttribute();

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute).IsTypeOf<PhysicalFieldAttribute>();
  }

  [Test]
  public async Task PhysicalFieldAttribute_DefaultConstructor_HasDefaultValuesAsync() {
    // Arrange & Act
    var attribute = new PhysicalFieldAttribute();

    // Assert - verify defaults
    await Assert.That(attribute.Indexed).IsFalse();
    await Assert.That(attribute.Unique).IsFalse();
    await Assert.That(attribute.ColumnName).IsNull();
    await Assert.That(attribute.MaxLength).IsEqualTo(-1); // -1 means "not set" (unlimited TEXT)
  }

  [Test]
  public async Task PhysicalFieldAttribute_Indexed_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new PhysicalFieldAttribute { Indexed = true };

    // Assert
    await Assert.That(attribute.Indexed).IsTrue();
  }

  [Test]
  public async Task PhysicalFieldAttribute_Unique_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new PhysicalFieldAttribute { Unique = true };

    // Assert
    await Assert.That(attribute.Unique).IsTrue();
  }

  [Test]
  public async Task PhysicalFieldAttribute_ColumnName_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new PhysicalFieldAttribute { ColumnName = "custom_column" };

    // Assert
    await Assert.That(attribute.ColumnName).IsEqualTo("custom_column");
  }

  [Test]
  public async Task PhysicalFieldAttribute_MaxLength_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new PhysicalFieldAttribute { MaxLength = 200 };

    // Assert
    await Assert.That(attribute.MaxLength).IsEqualTo(200);
  }

  [Test]
  public async Task PhysicalFieldAttribute_AttributeUsage_AllowsPropertyTargetOnlyAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PhysicalFieldAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn).IsEqualTo(AttributeTargets.Property);
  }

  [Test]
  public async Task PhysicalFieldAttribute_AttributeUsage_DoesNotAllowMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PhysicalFieldAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task PhysicalFieldAttribute_AttributeUsage_IsInheritedAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PhysicalFieldAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.Inherited).IsTrue();
  }

  [Test]
  public async Task PhysicalFieldAttribute_IsSealedAsync() {
    // Assert - attribute class should be sealed for performance
    await Assert.That(typeof(PhysicalFieldAttribute).IsSealed).IsTrue();
  }
}
