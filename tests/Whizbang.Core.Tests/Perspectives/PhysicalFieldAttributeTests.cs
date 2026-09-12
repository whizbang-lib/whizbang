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
    await Assert.That(attribute.Unique).IsFalse();
    await Assert.That(attribute.ColumnName).IsNull();
    await Assert.That(attribute.MaxLength).IsEqualTo(-1);
  }

  [Test]
  public async Task PhysicalFieldAttribute_Properties_CanBeSetAsync() {
    var attribute = new PhysicalFieldAttribute {
      Unique = true,
      ColumnName = "custom_column",
      MaxLength = 200
    };

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

  /// <summary>
  /// Promotion says nothing about indexing, because one attribute asks for an index and it is not
  /// this one.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>[PhysicalField]</c> used to carry an <c>Indexed</c> flag, which meant an author had to know
  /// their model was stored as a document to choose between that flag and a different attribute for
  /// a field that was not promoted. Two spellings for one intent, and the thing that separated them
  /// was a storage detail the framework already knows.
  /// </para>
  /// <para>
  /// <c>[Indexed]</c> is now the only way to ask, and it works on either side of the promotion.
  /// Uniqueness stays here, because a unique constraint is a property of the column rather than a
  /// request for an index.
  /// </para>
  /// </remarks>
  [Test]
  public async Task PromotionCarriesNoIndexFlagAsync() {
    var properties = typeof(PhysicalFieldAttribute).GetProperties().Select(p => p.Name).ToList();

    await Assert.That(properties).DoesNotContain("Indexed")
      .Because("[Indexed] is the one way to ask for an index, on a promoted field and a document "
        + "field alike, so a second spelling here would be a second answer to the same question");
    await Assert.That(properties).Contains("Unique")
      .Because("a unique constraint is a property of the column rather than a request for an index");
  }
}
