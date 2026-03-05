using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Tests for <see cref="PopulateFromIdentifierAttribute"/> and <see cref="IdentifierKind"/>.
/// Validates attribute behavior for message identifier population.
/// </summary>
[Category("Core")]
[Category("Attributes")]
[Category("AutoPopulate")]
public class PopulateFromIdentifierAttributeTests {

  #region IdentifierKind Enum Tests

  [Test]
  public async Task IdentifierKind_MessageId_HasExpectedValueAsync() {
    // Arrange & Act
    var kind = IdentifierKind.MessageId;

    // Assert
    await Assert.That((int)kind).IsEqualTo(0);
  }

  [Test]
  public async Task IdentifierKind_CorrelationId_HasExpectedValueAsync() {
    // Arrange & Act
    var kind = IdentifierKind.CorrelationId;

    // Assert
    await Assert.That((int)kind).IsEqualTo(1);
  }

  [Test]
  public async Task IdentifierKind_CausationId_HasExpectedValueAsync() {
    // Arrange & Act
    var kind = IdentifierKind.CausationId;

    // Assert
    await Assert.That((int)kind).IsEqualTo(2);
  }

  [Test]
  public async Task IdentifierKind_StreamId_HasExpectedValueAsync() {
    // Arrange & Act
    var kind = IdentifierKind.StreamId;

    // Assert
    await Assert.That((int)kind).IsEqualTo(3);
  }

  [Test]
  public async Task IdentifierKind_AllValues_AreDistinctAsync() {
    // Arrange
    var values = Enum.GetValues<IdentifierKind>();

    // Act
    var distinctCount = values.Distinct().Count();

    // Assert
    await Assert.That(distinctCount).IsEqualTo(values.Length);
  }

  [Test]
  public async Task IdentifierKind_HasFourValuesAsync() {
    // Arrange & Act
    var values = Enum.GetValues<IdentifierKind>();

    // Assert - MessageId, CorrelationId, CausationId, StreamId
    await Assert.That(values.Length).IsEqualTo(4);
  }

  #endregion

  #region PopulateFromIdentifierAttribute Constructor Tests

  [Test]
  public async Task PopulateFromIdentifierAttribute_Constructor_SetsKindPropertyAsync() {
    // Arrange & Act
    var attribute = new PopulateFromIdentifierAttribute(IdentifierKind.MessageId);

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(IdentifierKind.MessageId);
  }

  [Test]
  public async Task PopulateFromIdentifierAttribute_Constructor_WithCorrelationId_SetsKindAsync() {
    // Arrange & Act
    var attribute = new PopulateFromIdentifierAttribute(IdentifierKind.CorrelationId);

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(IdentifierKind.CorrelationId);
  }

  [Test]
  public async Task PopulateFromIdentifierAttribute_Constructor_WithCausationId_SetsKindAsync() {
    // Arrange & Act
    var attribute = new PopulateFromIdentifierAttribute(IdentifierKind.CausationId);

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(IdentifierKind.CausationId);
  }

  [Test]
  public async Task PopulateFromIdentifierAttribute_Constructor_WithStreamId_SetsKindAsync() {
    // Arrange & Act
    var attribute = new PopulateFromIdentifierAttribute(IdentifierKind.StreamId);

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(IdentifierKind.StreamId);
  }

  #endregion

  #region PopulateFromIdentifierAttribute Usage Tests

  [Test]
  public async Task PopulateFromIdentifierAttribute_AttributeUsage_AllowsPropertyTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateFromIdentifierAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Property)).IsTrue();
  }

  [Test]
  public async Task PopulateFromIdentifierAttribute_AttributeUsage_AllowsParameterTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateFromIdentifierAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Parameter)).IsTrue();
  }

  [Test]
  public async Task PopulateFromIdentifierAttribute_AttributeUsage_DoesNotAllowMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateFromIdentifierAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task PopulateFromIdentifierAttribute_AttributeUsage_IsInheritedAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateFromIdentifierAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.Inherited).IsTrue();
  }

  #endregion

  #region Inheritance Discovery Tests

  [Test]
  public async Task PopulateFromIdentifierAttribute_InheritedFromBaseRecord_IsDiscoverableAsync() {
    // Arrange - Derived record inherits from base with attribute
    var derivedType = typeof(DerivedEventWithIdentifier);
    var property = derivedType.GetProperty("CorrelationId");

    // Act - Should find inherited attribute
    var attribute = property?.GetCustomAttributes(typeof(PopulateFromIdentifierAttribute), inherit: true)
      .Cast<PopulateFromIdentifierAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Kind).IsEqualTo(IdentifierKind.CorrelationId);
  }

  [Test]
  public async Task PopulateFromIdentifierAttribute_AllIdentifierKinds_OnSameRecord_AreDiscoverableAsync() {
    // Arrange
    var type = typeof(TestEventWithAllIdentifierKinds);
    var messageIdProp = type.GetProperty("MessageId");
    var correlationIdProp = type.GetProperty("CorrelationId");
    var causationIdProp = type.GetProperty("CausationId");
    var streamIdProp = type.GetProperty("StreamId");

    // Act
    var messageIdAttr = messageIdProp?.GetCustomAttributes(typeof(PopulateFromIdentifierAttribute), true)
      .Cast<PopulateFromIdentifierAttribute>().FirstOrDefault();
    var correlationIdAttr = correlationIdProp?.GetCustomAttributes(typeof(PopulateFromIdentifierAttribute), true)
      .Cast<PopulateFromIdentifierAttribute>().FirstOrDefault();
    var causationIdAttr = causationIdProp?.GetCustomAttributes(typeof(PopulateFromIdentifierAttribute), true)
      .Cast<PopulateFromIdentifierAttribute>().FirstOrDefault();
    var streamIdAttr = streamIdProp?.GetCustomAttributes(typeof(PopulateFromIdentifierAttribute), true)
      .Cast<PopulateFromIdentifierAttribute>().FirstOrDefault();

    // Assert
    await Assert.That(messageIdAttr).IsNotNull();
    await Assert.That(messageIdAttr!.Kind).IsEqualTo(IdentifierKind.MessageId);
    await Assert.That(correlationIdAttr).IsNotNull();
    await Assert.That(correlationIdAttr!.Kind).IsEqualTo(IdentifierKind.CorrelationId);
    await Assert.That(causationIdAttr).IsNotNull();
    await Assert.That(causationIdAttr!.Kind).IsEqualTo(IdentifierKind.CausationId);
    await Assert.That(streamIdAttr).IsNotNull();
    await Assert.That(streamIdAttr!.Kind).IsEqualTo(IdentifierKind.StreamId);
  }

  [Test]
  public async Task PopulateFromIdentifierAttribute_OnSagaEvent_SupportsWorkflowPatternAsync() {
    // Arrange - Saga event pattern with correlation and causation
    var type = typeof(SagaEventWithIdentifiers);
    var workflowIdProp = type.GetProperty("WorkflowId");
    var triggeredByProp = type.GetProperty("TriggeredBy");

    // Act
    var workflowIdAttr = workflowIdProp?.GetCustomAttributes(typeof(PopulateFromIdentifierAttribute), true)
      .Cast<PopulateFromIdentifierAttribute>().FirstOrDefault();
    var triggeredByAttr = triggeredByProp?.GetCustomAttributes(typeof(PopulateFromIdentifierAttribute), true)
      .Cast<PopulateFromIdentifierAttribute>().FirstOrDefault();

    // Assert - WorkflowId uses CorrelationId, TriggeredBy uses CausationId
    await Assert.That(workflowIdAttr).IsNotNull();
    await Assert.That(workflowIdAttr!.Kind).IsEqualTo(IdentifierKind.CorrelationId);
    await Assert.That(triggeredByAttr).IsNotNull();
    await Assert.That(triggeredByAttr!.Kind).IsEqualTo(IdentifierKind.CausationId);
  }

  // Base record with identifier attribute
  private record BaseEventWithIdentifier {
    [PopulateFromIdentifier(IdentifierKind.CorrelationId)]
    public Guid? CorrelationId { get; init; }
  }

  // Derived record inherits the attribute
  private sealed record DerivedEventWithIdentifier : BaseEventWithIdentifier {
    public string? AdditionalData { get; init; }
  }

  // Record with all identifier kinds
  private sealed record TestEventWithAllIdentifierKinds(
    Guid Id,
    [property: PopulateFromIdentifier(IdentifierKind.MessageId)] Guid? MessageId = null,
    [property: PopulateFromIdentifier(IdentifierKind.CorrelationId)] Guid? CorrelationId = null,
    [property: PopulateFromIdentifier(IdentifierKind.CausationId)] Guid? CausationId = null,
    [property: PopulateFromIdentifier(IdentifierKind.StreamId)] Guid? StreamId = null
  );

  // Saga event pattern - realistic usage
  private sealed record SagaEventWithIdentifiers(
    Guid OrderId,
    string Status,
    [property: PopulateFromIdentifier(IdentifierKind.CorrelationId)] Guid? WorkflowId = null,
    [property: PopulateFromIdentifier(IdentifierKind.CausationId)] Guid? TriggeredBy = null
  );

  #endregion
}
