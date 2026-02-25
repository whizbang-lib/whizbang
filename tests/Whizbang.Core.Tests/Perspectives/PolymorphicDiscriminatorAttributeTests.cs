using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Helpers;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="PolymorphicDiscriminatorAttribute"/>.
/// Validates attribute behavior, properties, and targeting rules.
/// </summary>
/// <docs>perspectives/polymorphic-discriminator</docs>
[Category("Core")]
[Category("Attributes")]
[Category("PolymorphicDiscriminator")]
public class PolymorphicDiscriminatorAttributeTests {
  [Test]
  public async Task PolymorphicDiscriminatorAttribute_DefaultConstructor_HasDefaultValuesAsync() {
    var attribute = new PolymorphicDiscriminatorAttribute();
    await Assert.That(attribute.ColumnName).IsNull();
  }

  [Test]
  public async Task PolymorphicDiscriminatorAttribute_ColumnName_CanBeSetAsync() {
    var attribute = new PolymorphicDiscriminatorAttribute {
      ColumnName = "discriminator_type"
    };

    await Assert.That(attribute.ColumnName).IsEqualTo("discriminator_type");
  }

  [Test]
  public async Task PolymorphicDiscriminatorAttribute_AttributeUsage_PropertyOnly_AllowsMultiple_IsInheritedAsync() {
    var attributeUsage = AttributeTestHelpers.GetAttributeUsage<PolymorphicDiscriminatorAttribute>();
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn).IsEqualTo(AttributeTargets.Property);
    await Assert.That(attributeUsage.AllowMultiple).IsFalse();
    await Assert.That(attributeUsage.Inherited).IsTrue();
  }

  [Test]
  public async Task PolymorphicDiscriminatorAttribute_IsSealedAsync() {
    await Assert.That(typeof(PolymorphicDiscriminatorAttribute).IsSealed).IsTrue();
  }
}
