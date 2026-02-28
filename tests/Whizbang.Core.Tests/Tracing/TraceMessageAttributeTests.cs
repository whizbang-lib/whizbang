using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for TraceMessageAttribute which marks message types for explicit tracing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/TraceMessageAttribute.cs</code-under-test>
public class TraceMessageAttributeTests {
  #region Constructor Tests

  [Test]
  public async Task DefaultConstructor_SetsVerbosity_ToVerboseAsync() {
    // Arrange & Act
    var attribute = new TraceMessageAttribute();

    // Assert - Default verbosity is Verbose
    await Assert.That(attribute.Verbosity).IsEqualTo(TraceVerbosity.Verbose);
  }

  [Test]
  public async Task Constructor_WithVerbosity_SetsPropertyAsync() {
    // Arrange & Act
    var attribute = new TraceMessageAttribute(TraceVerbosity.Debug);

    // Assert
    await Assert.That(attribute.Verbosity).IsEqualTo(TraceVerbosity.Debug);
  }

  [Test]
  public async Task Constructor_WithMinimal_SetsPropertyAsync() {
    // Arrange & Act
    var attribute = new TraceMessageAttribute(TraceVerbosity.Minimal);

    // Assert
    await Assert.That(attribute.Verbosity).IsEqualTo(TraceVerbosity.Minimal);
  }

  [Test]
  public async Task Constructor_WithNormal_SetsPropertyAsync() {
    // Arrange & Act
    var attribute = new TraceMessageAttribute(TraceVerbosity.Normal);

    // Assert
    await Assert.That(attribute.Verbosity).IsEqualTo(TraceVerbosity.Normal);
  }

  [Test]
  public async Task Constructor_WithOff_SetsPropertyAsync() {
    // Arrange & Act - Off is valid but unusual
    var attribute = new TraceMessageAttribute(TraceVerbosity.Off);

    // Assert
    await Assert.That(attribute.Verbosity).IsEqualTo(TraceVerbosity.Off);
  }

  #endregion

  #region Attribute Usage Tests

  [Test]
  public async Task TraceMessageAttribute_HasAttributeUsageAsync() {
    // Arrange
    var type = typeof(TraceMessageAttribute);
    var usageAttribute = Attribute.GetCustomAttribute(type, typeof(AttributeUsageAttribute)) as AttributeUsageAttribute;

    // Assert
    await Assert.That(usageAttribute).IsNotNull();
  }

  [Test]
  public async Task TraceMessageAttribute_TargetsClassesOnlyAsync() {
    // Arrange
    var type = typeof(TraceMessageAttribute);
    var usageAttribute = (AttributeUsageAttribute)Attribute.GetCustomAttribute(type, typeof(AttributeUsageAttribute))!;

    // Assert - Should target classes (records are classes)
    await Assert.That(usageAttribute.ValidOn).IsEqualTo(AttributeTargets.Class);
  }

  [Test]
  public async Task TraceMessageAttribute_DoesNotAllowMultipleAsync() {
    // Arrange
    var type = typeof(TraceMessageAttribute);
    var usageAttribute = (AttributeUsageAttribute)Attribute.GetCustomAttribute(type, typeof(AttributeUsageAttribute))!;

    // Assert - A message can only have one trace attribute
    await Assert.That(usageAttribute.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task TraceMessageAttribute_DoesNotInheritAsync() {
    // Arrange
    var type = typeof(TraceMessageAttribute);
    var usageAttribute = (AttributeUsageAttribute)Attribute.GetCustomAttribute(type, typeof(AttributeUsageAttribute))!;

    // Assert - Derived messages don't inherit the trace attribute
    await Assert.That(usageAttribute.Inherited).IsFalse();
  }

  #endregion

  #region Applied Attribute Tests

  [Test]
  public async Task CanBeApplied_ToRecordWithDefaultVerbosityAsync() {
    // Arrange
    var messageType = typeof(TestEventWithDefaultTrace);
    var attribute = Attribute.GetCustomAttribute(messageType, typeof(TraceMessageAttribute)) as TraceMessageAttribute;

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Verbosity).IsEqualTo(TraceVerbosity.Verbose);
  }

  [Test]
  public async Task CanBeApplied_ToRecordWithExplicitVerbosityAsync() {
    // Arrange
    var messageType = typeof(TestCommandWithDebugTrace);
    var attribute = Attribute.GetCustomAttribute(messageType, typeof(TraceMessageAttribute)) as TraceMessageAttribute;

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Verbosity).IsEqualTo(TraceVerbosity.Debug);
  }

  [Test]
  public async Task RecordWithoutAttribute_ReturnsNullAsync() {
    // Arrange
    var messageType = typeof(TestEventWithoutTrace);
    var attribute = Attribute.GetCustomAttribute(messageType, typeof(TraceMessageAttribute));

    // Assert
    await Assert.That(attribute).IsNull();
  }

  [Test]
  public async Task CanBeApplied_ToClassAsync() {
    // Arrange - For non-record classes (backward compatibility)
    var messageType = typeof(TestMessageClassWithTrace);
    var attribute = Attribute.GetCustomAttribute(messageType, typeof(TraceMessageAttribute)) as TraceMessageAttribute;

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Verbosity).IsEqualTo(TraceVerbosity.Normal);
  }

  #endregion

  #region Sealed Class Tests

  [Test]
  public async Task TraceMessageAttribute_IsSealedAsync() {
    // Arrange
    var type = typeof(TraceMessageAttribute);

    // Assert - Attribute should be sealed for performance
    await Assert.That(type.IsSealed).IsTrue();
  }

  #endregion

  #region Test Fixtures

  [TraceMessage]
  private sealed record TestEventWithDefaultTrace;

  [TraceMessage(TraceVerbosity.Debug)]
  private sealed record TestCommandWithDebugTrace;

  private sealed record TestEventWithoutTrace;

  [TraceMessage(TraceVerbosity.Normal)]
  private sealed class TestMessageClassWithTrace { }

  #endregion
}
