using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Helpers;

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
  public async Task PhysicalFieldAttribute_DefaultConstructor_HasDefaultValuesAsync() {
    var attribute = new PhysicalFieldAttribute();
    await Assert.That(attribute.Indexed).IsFalse();
    await Assert.That(attribute.Unique).IsFalse();
    await Assert.That(attribute.ColumnName).IsNull();
    await Assert.That(attribute.MaxLength).IsEqualTo(-1);
  }

  [Test]
  public async Task PhysicalFieldAttribute_Properties_CanBeSetAsync() {
    var attribute = new PhysicalFieldAttribute {
      Indexed = true,
      Unique = true,
      ColumnName = "custom_column",
      MaxLength = 200
    };

    await Assert.That(attribute.Indexed).IsTrue();
    await Assert.That(attribute.Unique).IsTrue();
    await Assert.That(attribute.ColumnName).IsEqualTo("custom_column");
    await Assert.That(attribute.MaxLength).IsEqualTo(200);
  }

  [Test]
  public async Task PhysicalFieldAttribute_AttributeUsage_PropertyOnly_AllowsMultiple_IsInheritedAsync() {
    var attributeUsage = AttributeTestHelpers.GetAttributeUsage<PhysicalFieldAttribute>();
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn).IsEqualTo(AttributeTargets.Property);
    await Assert.That(attributeUsage.AllowMultiple).IsFalse();
    await Assert.That(attributeUsage.Inherited).IsTrue();
  }

  [Test]
  public async Task PhysicalFieldAttribute_IsSealedAsync() {
    await Assert.That(typeof(PhysicalFieldAttribute).IsSealed).IsTrue();
  }
}
