using System;
using System.Linq;
using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for <see cref="StreamKeyAttribute"/> attribute.
/// Validates attribute behavior, attribute usage configuration, and targeting rules.
/// </summary>
[Category("Core")]
[Category("Attributes")]
[Category("StreamKey")]
public class StreamKeyAttributeTests {

  [Test]
  public async Task StreamKeyAttribute_DefaultConstructor_CreatesInstanceAsync() {
    // Arrange & Act
    var attribute = new StreamKeyAttribute();

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute).IsTypeOf<StreamKeyAttribute>();
  }

  [Test]
  public async Task StreamKeyAttribute_AttributeUsage_AllowsPropertyTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(StreamKeyAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Property)).IsTrue();
  }

  [Test]
  public async Task StreamKeyAttribute_AttributeUsage_AllowsParameterTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(StreamKeyAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Parameter)).IsTrue();
  }

  [Test]
  public async Task StreamKeyAttribute_AttributeUsage_DoesNotAllowMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(StreamKeyAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task StreamKeyAttribute_AttributeUsage_IsInheritedAsync() {
    // Arrange & Act
    var attributeUsage = typeof(StreamKeyAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.Inherited).IsTrue();
  }
}
