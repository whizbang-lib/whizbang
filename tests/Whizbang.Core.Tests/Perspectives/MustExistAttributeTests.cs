using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="MustExistAttribute"/>.
/// </summary>
/// <docs>attributes/must-exist</docs>
public class MustExistAttributeTests {
  [Test]
  public async Task MustExistAttribute_CanBeInstantiatedAsync() {
    // Arrange & Act
    var attribute = new MustExistAttribute();

    // Assert
    await Assert.That(attribute).IsNotNull();
  }

  [Test]
  public async Task MustExistAttribute_TargetsMethodsOnlyAsync() {
    // Arrange
    var attributeUsage = typeof(MustExistAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn).IsEqualTo(AttributeTargets.Method);
  }

  [Test]
  public async Task MustExistAttribute_DoesNotAllowMultipleAsync() {
    // Arrange
    var attributeUsage = typeof(MustExistAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task MustExistAttribute_IsInheritedAsync() {
    // Arrange
    var attributeUsage = typeof(MustExistAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.Inherited).IsTrue();
  }

  [Test]
  public async Task MustExistAttribute_IsSealedAsync() {
    // Assert - attribute class should be sealed for performance
    await Assert.That(typeof(MustExistAttribute).IsSealed).IsTrue();
  }
}
