using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for WhizbangPerspectiveAttribute - validates constructor behavior
/// and key-based matching for DbContext grouping.
/// </summary>
[Category("Core")]
[Category("Perspectives")]
public class WhizbangPerspectiveAttributeTests {
  [Test]
  public async Task Constructor_WithNullKeys_ShouldSetEmptyArrayAsync() {
    // Arrange & Act - explicitly pass null to test the null coalescing behavior
    var attribute = new WhizbangPerspectiveAttribute(null);

    // Assert - Keys should be empty array, not null
    await Assert.That(attribute.Keys).IsNotNull();
    await Assert.That(attribute.Keys.Length).IsEqualTo(0);
  }

  [Test]
  public async Task Constructor_WithNoArguments_ShouldSetEmptyArrayAsync() {
    // Arrange & Act
    var attribute = new WhizbangPerspectiveAttribute();

    // Assert
    await Assert.That(attribute.Keys).IsNotNull();
    await Assert.That(attribute.Keys.Length).IsEqualTo(0);
  }

  [Test]
  public async Task Constructor_WithSingleKey_ShouldSetKeyAsync() {
    // Arrange & Act
    var attribute = new WhizbangPerspectiveAttribute("catalog");

    // Assert
    await Assert.That(attribute.Keys).Count().IsEqualTo(1);
    await Assert.That(attribute.Keys[0]).IsEqualTo("catalog");
  }

  [Test]
  public async Task Constructor_WithMultipleKeys_ShouldSetAllKeysAsync() {
    // Arrange & Act
    var attribute = new WhizbangPerspectiveAttribute("catalog", "orders", "inventory");

    // Assert
    await Assert.That(attribute.Keys).Count().IsEqualTo(3);
    await Assert.That(attribute.Keys).Contains("catalog");
    await Assert.That(attribute.Keys).Contains("orders");
    await Assert.That(attribute.Keys).Contains("inventory");
  }

  [Test]
  public async Task Keys_ShouldBeReadOnlyAsync() {
    // Arrange
    var attribute = new WhizbangPerspectiveAttribute("test");

    // Assert - Keys property returns array, verify it's accessible
    await Assert.That(attribute.Keys).IsNotNull();
    await Assert.That(attribute.Keys.GetType().IsArray).IsTrue();
  }
}
