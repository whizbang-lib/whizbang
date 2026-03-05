using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Tests for <see cref="PopulateFromContextAttribute"/> and <see cref="ContextKind"/>.
/// Validates attribute behavior for security context population.
/// </summary>
[Category("Core")]
[Category("Attributes")]
[Category("AutoPopulate")]
public class PopulateFromContextAttributeTests {

  #region ContextKind Enum Tests

  [Test]
  public async Task ContextKind_UserId_HasExpectedValueAsync() {
    // Arrange & Act
    var kind = ContextKind.UserId;

    // Assert
    await Assert.That((int)kind).IsEqualTo(0);
  }

  [Test]
  public async Task ContextKind_TenantId_HasExpectedValueAsync() {
    // Arrange & Act
    var kind = ContextKind.TenantId;

    // Assert
    await Assert.That((int)kind).IsEqualTo(1);
  }

  [Test]
  public async Task ContextKind_AllValues_AreDistinctAsync() {
    // Arrange
    var values = Enum.GetValues<ContextKind>();

    // Act
    var distinctCount = values.Distinct().Count();

    // Assert
    await Assert.That(distinctCount).IsEqualTo(values.Length);
  }

  [Test]
  public async Task ContextKind_HasTwoValuesAsync() {
    // Arrange & Act
    var values = Enum.GetValues<ContextKind>();

    // Assert - UserId, TenantId
    await Assert.That(values.Length).IsEqualTo(2);
  }

  #endregion

  #region PopulateFromContextAttribute Constructor Tests

  [Test]
  public async Task PopulateFromContextAttribute_Constructor_SetsKindPropertyAsync() {
    // Arrange & Act
    var attribute = new PopulateFromContextAttribute(ContextKind.UserId);

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(ContextKind.UserId);
  }

  [Test]
  public async Task PopulateFromContextAttribute_Constructor_WithTenantId_SetsKindAsync() {
    // Arrange & Act
    var attribute = new PopulateFromContextAttribute(ContextKind.TenantId);

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(ContextKind.TenantId);
  }

  #endregion

  #region PopulateFromContextAttribute Usage Tests

  [Test]
  public async Task PopulateFromContextAttribute_AttributeUsage_AllowsPropertyTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateFromContextAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Property)).IsTrue();
  }

  [Test]
  public async Task PopulateFromContextAttribute_AttributeUsage_AllowsParameterTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateFromContextAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Parameter)).IsTrue();
  }

  [Test]
  public async Task PopulateFromContextAttribute_AttributeUsage_DoesNotAllowMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateFromContextAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task PopulateFromContextAttribute_AttributeUsage_IsInheritedAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateFromContextAttribute)
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
  public async Task PopulateFromContextAttribute_CanBeAppliedToRecordParameterAsync() {
    // Arrange - Test record defined in test assembly
    // Note: [property:] target puts attribute on the property, not the parameter
    var property = typeof(TestEventWithContext).GetProperty("CreatedBy");

    // Assert
    await Assert.That(property).IsNotNull();

    var attribute = property!.GetCustomAttributes(typeof(PopulateFromContextAttribute), true)
      .Cast<PopulateFromContextAttribute>()
      .FirstOrDefault();

    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Kind).IsEqualTo(ContextKind.UserId);
  }

  // Test record for attribute application tests
  private sealed record TestEventWithContext(
    Guid Id,
    [property: PopulateFromContext(ContextKind.UserId)] string? CreatedBy = null,
    [property: PopulateFromContext(ContextKind.TenantId)] string? TenantId = null
  );

  #endregion

  #region Inheritance Discovery Tests

  [Test]
  public async Task PopulateFromContextAttribute_InheritedFromBaseRecord_IsDiscoverableAsync() {
    // Arrange - Derived record inherits from base with attribute
    var derivedType = typeof(DerivedEventWithContext);
    var property = derivedType.GetProperty("CreatedBy");

    // Act - Should find inherited attribute
    var attribute = property?.GetCustomAttributes(typeof(PopulateFromContextAttribute), inherit: true)
      .Cast<PopulateFromContextAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Kind).IsEqualTo(ContextKind.UserId);
  }

  [Test]
  public async Task PopulateFromContextAttribute_InheritedFromAbstractBase_IsDiscoverableAsync() {
    // Arrange
    var concreteType = typeof(ConcreteEventWithContext);
    var property = concreteType.GetProperty("TenantId");

    // Act
    var attribute = property?.GetCustomAttributes(typeof(PopulateFromContextAttribute), inherit: true)
      .Cast<PopulateFromContextAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Kind).IsEqualTo(ContextKind.TenantId);
  }

  [Test]
  public async Task PopulateFromContextAttribute_BothContextKinds_OnSameRecord_AreDiscoverableAsync() {
    // Arrange
    var type = typeof(TestEventWithBothContextKinds);
    var userIdProp = type.GetProperty("CreatedBy");
    var tenantIdProp = type.GetProperty("TenantId");

    // Act
    var userIdAttr = userIdProp?.GetCustomAttributes(typeof(PopulateFromContextAttribute), true)
      .Cast<PopulateFromContextAttribute>().FirstOrDefault();
    var tenantIdAttr = tenantIdProp?.GetCustomAttributes(typeof(PopulateFromContextAttribute), true)
      .Cast<PopulateFromContextAttribute>().FirstOrDefault();

    // Assert
    await Assert.That(userIdAttr).IsNotNull();
    await Assert.That(userIdAttr!.Kind).IsEqualTo(ContextKind.UserId);
    await Assert.That(tenantIdAttr).IsNotNull();
    await Assert.That(tenantIdAttr!.Kind).IsEqualTo(ContextKind.TenantId);
  }

  // Base record with context attribute
  private record BaseEventWithContext {
    [PopulateFromContext(ContextKind.UserId)]
    public string? CreatedBy { get; init; }
  }

  // Derived record inherits the attribute
  private sealed record DerivedEventWithContext : BaseEventWithContext {
    public string? AdditionalData { get; init; }
  }

  // Abstract base class pattern
  private abstract record AbstractEventWithContext {
    [PopulateFromContext(ContextKind.TenantId)]
    public virtual string? TenantId { get; init; }
  }

  private sealed record ConcreteEventWithContext : AbstractEventWithContext {
    public string? Payload { get; init; }
  }

  // Record with both context kinds
  private sealed record TestEventWithBothContextKinds(
    Guid Id,
    [property: PopulateFromContext(ContextKind.UserId)] string? CreatedBy = null,
    [property: PopulateFromContext(ContextKind.TenantId)] string? TenantId = null
  );

  #endregion
}
