using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.Attributes;

[Category("Core")]
[Category("Attributes")]
public class PinnedIdAttributeTests {
  [Test]
  public async Task Constructor_WithValidGuid_SetsIdAsync() {
    const string id = "a1b2c3d4-e5f6-7890-abcd-1234567890ab";

    var attribute = new PinnedIdAttribute(id);

    await Assert.That(attribute.Id).IsEqualTo(id);
  }

  [Test]
  public async Task Constructor_WithNullId_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new PinnedIdAttribute(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithEmptyId_ThrowsArgumentExceptionAsync() {
    await Assert.That(() => new PinnedIdAttribute(string.Empty))
        .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task Constructor_WithWhitespaceId_ThrowsArgumentExceptionAsync() {
    await Assert.That(() => new PinnedIdAttribute("   "))
        .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task Constructor_DoesNotValidateGuidFormatAsync() {
    // Attribute accepts any non-empty string; WHIZ102 analyzer enforces GUID format.
    const string notAGuid = "definitely-not-a-guid";

    var attribute = new PinnedIdAttribute(notAGuid);

    await Assert.That(attribute.Id).IsEqualTo(notAGuid);
  }

  [Test]
  public async Task PinnedIdAttribute_CanBeAppliedToClassAsync() {
    var type = typeof(TestClassWithPinnedId);
    var attributes = type.GetCustomAttributes(typeof(PinnedIdAttribute), false);

    await Assert.That(attributes).IsNotEmpty();
    var attr = attributes.First() as PinnedIdAttribute;
    await Assert.That(attr).IsNotNull();
    await Assert.That(attr!.Id).IsEqualTo("11111111-2222-3333-4444-555555555555");
  }

  [Test]
  public async Task PinnedIdAttribute_CanBeAppliedToStructAsync() {
    var type = typeof(TestStructWithPinnedId);
    var attributes = type.GetCustomAttributes(typeof(PinnedIdAttribute), false);

    await Assert.That(attributes).IsNotEmpty();
    var attr = attributes.First() as PinnedIdAttribute;
    await Assert.That(attr).IsNotNull();
    await Assert.That(attr!.Id).IsEqualTo("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
  }

  [Test]
  public async Task PinnedIdAttribute_AttributeUsageAllowsClassAndStructAsync() {
    var attributeUsage = typeof(PinnedIdAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .FirstOrDefault() as AttributeUsageAttribute;

    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn).IsEqualTo(AttributeTargets.Class | AttributeTargets.Struct);
  }

  [Test]
  public async Task PinnedIdAttribute_AttributeUsageDoesNotAllowMultipleAsync() {
    var attributeUsage = typeof(PinnedIdAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .FirstOrDefault() as AttributeUsageAttribute;

    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task PinnedIdAttribute_AttributeUsageIsNotInheritedAsync() {
    var attributeUsage = typeof(PinnedIdAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .FirstOrDefault() as AttributeUsageAttribute;

    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.Inherited).IsFalse();
  }

  [PinnedId("11111111-2222-3333-4444-555555555555")]
  private sealed class TestClassWithPinnedId { }

  [PinnedId("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
  private readonly struct TestStructWithPinnedId { }
}
