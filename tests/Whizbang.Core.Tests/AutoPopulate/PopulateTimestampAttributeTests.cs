using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Tests for <see cref="PopulateTimestampAttribute"/> and <see cref="TimestampKind"/>.
/// Validates attribute behavior, property values, and enum coverage.
/// </summary>
[Category("Core")]
[Category("Attributes")]
[Category("AutoPopulate")]
public class PopulateTimestampAttributeTests {

  #region TimestampKind Enum Tests

  [Test]
  public async Task TimestampKind_SentAt_HasExpectedValueAsync() {
    // Arrange & Act
    var kind = TimestampKind.SentAt;

    // Assert
    await Assert.That((int)kind).IsEqualTo(0);
  }

  [Test]
  public async Task TimestampKind_QueuedAt_HasExpectedValueAsync() {
    // Arrange & Act
    var kind = TimestampKind.QueuedAt;

    // Assert
    await Assert.That((int)kind).IsEqualTo(1);
  }

  [Test]
  public async Task TimestampKind_DeliveredAt_HasExpectedValueAsync() {
    // Arrange & Act
    var kind = TimestampKind.DeliveredAt;

    // Assert
    await Assert.That((int)kind).IsEqualTo(2);
  }

  [Test]
  public async Task TimestampKind_AllValues_AreDistinctAsync() {
    // Arrange
    var values = Enum.GetValues<TimestampKind>();

    // Act
    var distinctCount = values.Distinct().Count();

    // Assert
    await Assert.That(distinctCount).IsEqualTo(values.Length);
  }

  [Test]
  public async Task TimestampKind_HasThreeValuesAsync() {
    // Arrange & Act
    var values = Enum.GetValues<TimestampKind>();

    // Assert - SentAt, QueuedAt, DeliveredAt
    await Assert.That(values.Length).IsEqualTo(3);
  }

  #endregion

  #region PopulateTimestampAttribute Constructor Tests

  [Test]
  public async Task PopulateTimestampAttribute_Constructor_SetsKindPropertyAsync() {
    // Arrange & Act
    var attribute = new PopulateTimestampAttribute(TimestampKind.SentAt);

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(TimestampKind.SentAt);
  }

  [Test]
  public async Task PopulateTimestampAttribute_Constructor_WithQueuedAt_SetsKindAsync() {
    // Arrange & Act
    var attribute = new PopulateTimestampAttribute(TimestampKind.QueuedAt);

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(TimestampKind.QueuedAt);
  }

  [Test]
  public async Task PopulateTimestampAttribute_Constructor_WithDeliveredAt_SetsKindAsync() {
    // Arrange & Act
    var attribute = new PopulateTimestampAttribute(TimestampKind.DeliveredAt);

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(TimestampKind.DeliveredAt);
  }

  #endregion

  #region PopulateTimestampAttribute Usage Tests

  [Test]
  public async Task PopulateTimestampAttribute_AttributeUsage_AllowsPropertyTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateTimestampAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Property)).IsTrue();
  }

  [Test]
  public async Task PopulateTimestampAttribute_AttributeUsage_AllowsParameterTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateTimestampAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Parameter)).IsTrue();
  }

  [Test]
  public async Task PopulateTimestampAttribute_AttributeUsage_DoesNotAllowMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateTimestampAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task PopulateTimestampAttribute_AttributeUsage_IsInheritedAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateTimestampAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.Inherited).IsTrue();
  }

  #endregion

  #region Record Parameter Usage Tests

  [Test]
  public async Task PopulateTimestampAttribute_CanBeAppliedToRecordParameterAsync() {
    // Arrange - Test record defined in test assembly
    // Note: [property:] target puts attribute on the property, not the parameter
    var property = typeof(TestEventWithTimestamp).GetProperty("SentAt");

    // Assert
    await Assert.That(property).IsNotNull();

    var attribute = property!.GetCustomAttributes(typeof(PopulateTimestampAttribute), true)
      .Cast<PopulateTimestampAttribute>()
      .FirstOrDefault();

    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Kind).IsEqualTo(TimestampKind.SentAt);
  }

  // Test record for attribute application tests
  private sealed record TestEventWithTimestamp(
    Guid Id,
    [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null
  );

  #endregion

  #region Inheritance Discovery Tests

  [Test]
  public async Task PopulateTimestampAttribute_InheritedFromBaseRecord_IsDiscoverableAsync() {
    // Arrange - Derived record inherits from base with attribute
    var derivedType = typeof(DerivedEventWithTimestamp);
    var property = derivedType.GetProperty("SentAt");

    // Act - Should find inherited attribute
    var attribute = property?.GetCustomAttributes(typeof(PopulateTimestampAttribute), inherit: true)
      .Cast<PopulateTimestampAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Kind).IsEqualTo(TimestampKind.SentAt);
  }

  [Test]
  public async Task PopulateTimestampAttribute_InheritedFromAbstractBase_IsDiscoverableAsync() {
    // Arrange
    var concreteType = typeof(ConcreteEventWithTimestamp);
    var property = concreteType.GetProperty("SentAt");

    // Act
    var attribute = property?.GetCustomAttributes(typeof(PopulateTimestampAttribute), inherit: true)
      .Cast<PopulateTimestampAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Kind).IsEqualTo(TimestampKind.SentAt);
  }

  [Test]
  public async Task PopulateTimestampAttribute_MultipleTimestamps_OnSameRecord_AreAllDiscoverableAsync() {
    // Arrange
    var type = typeof(TestEventWithMultipleTimestamps);
    var sentAtProp = type.GetProperty("SentAt");
    var queuedAtProp = type.GetProperty("QueuedAt");
    var deliveredAtProp = type.GetProperty("DeliveredAt");

    // Act
    var sentAtAttr = sentAtProp?.GetCustomAttributes(typeof(PopulateTimestampAttribute), true)
      .Cast<PopulateTimestampAttribute>().FirstOrDefault();
    var queuedAtAttr = queuedAtProp?.GetCustomAttributes(typeof(PopulateTimestampAttribute), true)
      .Cast<PopulateTimestampAttribute>().FirstOrDefault();
    var deliveredAtAttr = deliveredAtProp?.GetCustomAttributes(typeof(PopulateTimestampAttribute), true)
      .Cast<PopulateTimestampAttribute>().FirstOrDefault();

    // Assert
    await Assert.That(sentAtAttr).IsNotNull();
    await Assert.That(sentAtAttr!.Kind).IsEqualTo(TimestampKind.SentAt);
    await Assert.That(queuedAtAttr).IsNotNull();
    await Assert.That(queuedAtAttr!.Kind).IsEqualTo(TimestampKind.QueuedAt);
    await Assert.That(deliveredAtAttr).IsNotNull();
    await Assert.That(deliveredAtAttr!.Kind).IsEqualTo(TimestampKind.DeliveredAt);
  }

  // Base record with timestamp attribute
  private record BaseEventWithTimestamp {
    [PopulateTimestamp(TimestampKind.SentAt)]
    public DateTimeOffset? SentAt { get; init; }
  }

  // Derived record inherits the attribute
  private sealed record DerivedEventWithTimestamp : BaseEventWithTimestamp {
    public string? AdditionalData { get; init; }
  }

  // Abstract base class pattern
  private abstract record AbstractEventWithTimestamp {
    [PopulateTimestamp(TimestampKind.SentAt)]
    public virtual DateTimeOffset? SentAt { get; init; }
  }

  private sealed record ConcreteEventWithTimestamp : AbstractEventWithTimestamp {
    public string? Payload { get; init; }
  }

  // Record with multiple timestamp attributes
  private sealed record TestEventWithMultipleTimestamps(
    Guid Id,
    [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null,
    [property: PopulateTimestamp(TimestampKind.QueuedAt)] DateTimeOffset? QueuedAt = null,
    [property: PopulateTimestamp(TimestampKind.DeliveredAt)] DateTimeOffset? DeliveredAt = null
  );

  #endregion
}
