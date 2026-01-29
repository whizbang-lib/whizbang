using System;
using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for <see cref="WhizbangIdAttribute"/> attribute.
/// Validates attribute behavior, property values, and constructor overloads.
/// </summary>
[Category("Core")]
[Category("Attributes")]
public class WhizbangIdAttributeTests {

  [Test]
  public async Task WhizbangIdAttribute_DefaultConstructor_HasNullNamespaceAsync() {
    // Arrange & Act
    var attribute = new WhizbangIdAttribute();

    // Assert
    await Assert.That(attribute.Namespace).IsNull();
  }

  [Test]
  public async Task WhizbangIdAttribute_DefaultConstructor_HasFalseSuppressDuplicateWarningAsync() {
    // Arrange & Act
    var attribute = new WhizbangIdAttribute();

    // Assert
    await Assert.That(attribute.SuppressDuplicateWarning).IsFalse();
  }

  [Test]
  public async Task WhizbangIdAttribute_ConstructorWithNamespace_SetsNamespacePropertyAsync() {
    // Arrange
    var expectedNamespace = "MyApp.Messages";

    // Act
    var attribute = new WhizbangIdAttribute(expectedNamespace);

    // Assert
    await Assert.That(attribute.Namespace).IsEqualTo(expectedNamespace);
  }

  [Test]
  public async Task WhizbangIdAttribute_NamespaceProperty_CanBeSetAsync() {
    // Arrange
    var attribute = new WhizbangIdAttribute();
    var expectedNamespace = "MyApp.Domain";

    // Act
    attribute.Namespace = expectedNamespace;

    // Assert
    await Assert.That(attribute.Namespace).IsEqualTo(expectedNamespace);
  }

  [Test]
  public async Task WhizbangIdAttribute_SuppressDuplicateWarningProperty_CanBeSetAsync() {
    // Arrange
    var attribute = new WhizbangIdAttribute {
      // Act
      SuppressDuplicateWarning = true
    };

    // Assert
    await Assert.That(attribute.SuppressDuplicateWarning).IsTrue();
  }

  [Test]
  public async Task WhizbangIdAttribute_AttributeUsage_AllowsStructTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(WhizbangIdAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Struct)).IsTrue();
  }

  [Test]
  public async Task WhizbangIdAttribute_AttributeUsage_AllowsPropertyTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(WhizbangIdAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Property)).IsTrue();
  }

  [Test]
  public async Task WhizbangIdAttribute_AttributeUsage_AllowsParameterTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(WhizbangIdAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Parameter)).IsTrue();
  }

  [Test]
  public async Task WhizbangIdAttribute_AttributeUsage_DoesNotAllowMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(WhizbangIdAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task WhizbangIdAttribute_AttributeUsage_IsNotInheritedAsync() {
    // Arrange & Act
    var attributeUsage = typeof(WhizbangIdAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.Inherited).IsFalse();
  }
}
