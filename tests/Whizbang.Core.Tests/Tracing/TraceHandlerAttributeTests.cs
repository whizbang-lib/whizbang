using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for TraceHandlerAttribute which marks handlers for explicit tracing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/TraceHandlerAttribute.cs</code-under-test>
public class TraceHandlerAttributeTests {
  #region Constructor Tests

  [Test]
  public async Task DefaultConstructor_SetsVerbosity_ToVerboseAsync() {
    // Arrange & Act
    var attribute = new TraceHandlerAttribute();

    // Assert - Default verbosity is Verbose
    await Assert.That(attribute.Verbosity).IsEqualTo(TraceVerbosity.Verbose);
  }

  [Test]
  public async Task Constructor_WithVerbosity_SetsPropertyAsync() {
    // Arrange & Act
    var attribute = new TraceHandlerAttribute(TraceVerbosity.Debug);

    // Assert
    await Assert.That(attribute.Verbosity).IsEqualTo(TraceVerbosity.Debug);
  }

  [Test]
  public async Task Constructor_WithMinimal_SetsPropertyAsync() {
    // Arrange & Act
    var attribute = new TraceHandlerAttribute(TraceVerbosity.Minimal);

    // Assert
    await Assert.That(attribute.Verbosity).IsEqualTo(TraceVerbosity.Minimal);
  }

  [Test]
  public async Task Constructor_WithNormal_SetsPropertyAsync() {
    // Arrange & Act
    var attribute = new TraceHandlerAttribute(TraceVerbosity.Normal);

    // Assert
    await Assert.That(attribute.Verbosity).IsEqualTo(TraceVerbosity.Normal);
  }

  [Test]
  public async Task Constructor_WithOff_SetsPropertyAsync() {
    // Arrange & Act - Off is valid but unusual
    var attribute = new TraceHandlerAttribute(TraceVerbosity.Off);

    // Assert
    await Assert.That(attribute.Verbosity).IsEqualTo(TraceVerbosity.Off);
  }

  #endregion

  #region Attribute Usage Tests

  [Test]
  public async Task TraceHandlerAttribute_HasAttributeUsageAsync() {
    // Arrange
    var type = typeof(TraceHandlerAttribute);
    var usageAttribute = Attribute.GetCustomAttribute(type, typeof(AttributeUsageAttribute)) as AttributeUsageAttribute;

    // Assert
    await Assert.That(usageAttribute).IsNotNull();
  }

  [Test]
  public async Task TraceHandlerAttribute_TargetsClassesOnlyAsync() {
    // Arrange
    var type = typeof(TraceHandlerAttribute);
    var usageAttribute = (AttributeUsageAttribute)Attribute.GetCustomAttribute(type, typeof(AttributeUsageAttribute))!;

    // Assert - Should only target classes (handlers are classes)
    await Assert.That(usageAttribute.ValidOn).IsEqualTo(AttributeTargets.Class);
  }

  [Test]
  public async Task TraceHandlerAttribute_DoesNotAllowMultipleAsync() {
    // Arrange
    var type = typeof(TraceHandlerAttribute);
    var usageAttribute = (AttributeUsageAttribute)Attribute.GetCustomAttribute(type, typeof(AttributeUsageAttribute))!;

    // Assert - A handler can only have one trace attribute
    await Assert.That(usageAttribute.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task TraceHandlerAttribute_DoesNotInheritAsync() {
    // Arrange
    var type = typeof(TraceHandlerAttribute);
    var usageAttribute = (AttributeUsageAttribute)Attribute.GetCustomAttribute(type, typeof(AttributeUsageAttribute))!;

    // Assert - Derived handlers don't inherit the trace attribute
    await Assert.That(usageAttribute.Inherited).IsFalse();
  }

  #endregion

  #region Applied Attribute Tests

  [Test]
  public async Task CanBeApplied_ToClassWithDefaultVerbosityAsync() {
    // Arrange
    var handlerType = typeof(TestHandlerWithDefaultTrace);
    var attribute = Attribute.GetCustomAttribute(handlerType, typeof(TraceHandlerAttribute)) as TraceHandlerAttribute;

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Verbosity).IsEqualTo(TraceVerbosity.Verbose);
  }

  [Test]
  public async Task CanBeApplied_ToClassWithExplicitVerbosityAsync() {
    // Arrange
    var handlerType = typeof(TestHandlerWithDebugTrace);
    var attribute = Attribute.GetCustomAttribute(handlerType, typeof(TraceHandlerAttribute)) as TraceHandlerAttribute;

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Verbosity).IsEqualTo(TraceVerbosity.Debug);
  }

  [Test]
  public async Task ClassWithoutAttribute_ReturnsNullAsync() {
    // Arrange
    var handlerType = typeof(TestHandlerWithoutTrace);
    var attribute = Attribute.GetCustomAttribute(handlerType, typeof(TraceHandlerAttribute));

    // Assert
    await Assert.That(attribute).IsNull();
  }

  #endregion

  #region Inheritance Tests

  [Test]
  public async Task DerivedClass_DoesNotInheritAttribute_WhenNotAppliedAsync() {
    // Arrange
    var derivedType = typeof(DerivedHandlerWithoutOwnAttribute);
    var attribute = Attribute.GetCustomAttribute(derivedType, typeof(TraceHandlerAttribute), inherit: true);

    // Assert - Even with inherit: true, should not find it (Inherited = false)
    await Assert.That(attribute).IsNull();
  }

  [Test]
  public async Task DerivedClass_HasOwnAttribute_WhenAppliedAsync() {
    // Arrange
    var derivedType = typeof(DerivedHandlerWithOwnAttribute);
    var attribute = Attribute.GetCustomAttribute(derivedType, typeof(TraceHandlerAttribute)) as TraceHandlerAttribute;

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Verbosity).IsEqualTo(TraceVerbosity.Normal);
  }

  #endregion

  #region Sealed Class Tests

  [Test]
  public async Task TraceHandlerAttribute_IsSealedAsync() {
    // Arrange
    var type = typeof(TraceHandlerAttribute);

    // Assert - Attribute should be sealed for performance
    await Assert.That(type.IsSealed).IsTrue();
  }

  #endregion

  #region Test Fixtures

  [TraceHandler]
  private sealed class TestHandlerWithDefaultTrace { }

  [TraceHandler(TraceVerbosity.Debug)]
  private sealed class TestHandlerWithDebugTrace { }

  private sealed class TestHandlerWithoutTrace { }

  [TraceHandler(TraceVerbosity.Verbose)]
  private class BaseHandlerWithTrace { }

  private sealed class DerivedHandlerWithoutOwnAttribute : BaseHandlerWithTrace { }

  [TraceHandler(TraceVerbosity.Normal)]
  private sealed class DerivedHandlerWithOwnAttribute : BaseHandlerWithTrace { }

  #endregion
}
