// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for <see cref="StreamIdAttribute"/> attribute.
/// Validates attribute behavior, attribute usage configuration, and targeting rules.
/// </summary>
[Category("Core")]
[Category("Attributes")]
[Category("StreamId")]
public class StreamIdAttributeTests {

  [Test]
  public async Task StreamIdAttribute_DefaultConstructor_CreatesInstanceAsync() {
    // Arrange & Act
    var attribute = new StreamIdAttribute();

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute).IsTypeOf<StreamIdAttribute>();
  }

  [Test]
  public async Task StreamIdAttribute_AttributeUsage_AllowsPropertyTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(StreamIdAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Property)).IsTrue();
  }

  [Test]
  public async Task StreamIdAttribute_AttributeUsage_AllowsParameterTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(StreamIdAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Parameter)).IsTrue();
  }

  [Test]
  public async Task StreamIdAttribute_AttributeUsage_DoesNotAllowMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(StreamIdAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task StreamIdAttribute_AttributeUsage_IsInheritedAsync() {
    // Arrange & Act
    var attributeUsage = typeof(StreamIdAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.Inherited).IsTrue();
  }
  [Test]
  public async Task StreamIdAttribute_Override_DefaultsToFalseAsync() {
    // A plain [StreamId] stays what it always was: no override, so hiding an inherited one is still reported (#1833)
    var attribute = new StreamIdAttribute();

    await Assert.That(attribute.Override).IsFalse();
  }

  [Test]
  public async Task StreamIdAttribute_Override_CanBeSetAsync() {
    var attribute = new StreamIdAttribute { Override = true };

    await Assert.That(attribute.Override).IsTrue();
  }
}
