using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Helpers;

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
  [Arguments(FieldStorageMode.JsonOnly)]
  [Arguments(FieldStorageMode.Extracted)]
  [Arguments(FieldStorageMode.Split)]
  public async Task PerspectiveStorageAttribute_Constructor_SetsModeAsync(FieldStorageMode mode) {
    var attribute = new PerspectiveStorageAttribute(mode);
    await Assert.That(attribute.Mode).IsEqualTo(mode);
  }

  [Test]
  public async Task PerspectiveStorageAttribute_AttributeUsage_AllowsClassAndStructAsync() {
    var attributeUsage = AttributeTestHelpers.GetAttributeUsage<PerspectiveStorageAttribute>();
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Class)).IsTrue();
    await Assert.That(attributeUsage.ValidOn.HasFlag(AttributeTargets.Struct)).IsTrue();
  }

  [Test]
  public async Task PerspectiveStorageAttribute_AttributeUsage_DoesNotAllowMultiple_NotInheritedAsync() {
    var attributeUsage = AttributeTestHelpers.GetAttributeUsage<PerspectiveStorageAttribute>();
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
    await Assert.That(attributeUsage.Inherited).IsFalse();
  }

  [Test]
  public async Task PerspectiveStorageAttribute_IsSealedAsync() {
    await Assert.That(typeof(PerspectiveStorageAttribute).IsSealed).IsTrue();
  }
}
