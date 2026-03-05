using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Tests for <see cref="PopulateFromServiceAttribute"/> and <see cref="ServiceKind"/>.
/// Validates attribute behavior for service instance info population.
/// </summary>
[Category("Core")]
[Category("Attributes")]
[Category("AutoPopulate")]
public class PopulateFromServiceAttributeTests {

  #region ServiceKind Enum Tests

  [Test]
  public async Task ServiceKind_ServiceName_HasExpectedValueAsync() {
    // Arrange & Act
    var kind = ServiceKind.ServiceName;

    // Assert
    await Assert.That((int)kind).IsEqualTo(0);
  }

  [Test]
  public async Task ServiceKind_InstanceId_HasExpectedValueAsync() {
    // Arrange & Act
    var kind = ServiceKind.InstanceId;

    // Assert
    await Assert.That((int)kind).IsEqualTo(1);
  }

  [Test]
  public async Task ServiceKind_HostName_HasExpectedValueAsync() {
    // Arrange & Act
    var kind = ServiceKind.HostName;

    // Assert
    await Assert.That((int)kind).IsEqualTo(2);
  }

  [Test]
  public async Task ServiceKind_ProcessId_HasExpectedValueAsync() {
    // Arrange & Act
    var kind = ServiceKind.ProcessId;

    // Assert
    await Assert.That((int)kind).IsEqualTo(3);
  }

  [Test]
  public async Task ServiceKind_AllValues_AreDistinctAsync() {
    // Arrange
    var values = Enum.GetValues<ServiceKind>();

    // Act
    var distinctCount = values.Distinct().Count();

    // Assert
    await Assert.That(distinctCount).IsEqualTo(values.Length);
  }

  [Test]
  public async Task ServiceKind_HasFourValuesAsync() {
    // Arrange & Act
    var values = Enum.GetValues<ServiceKind>();

    // Assert - ServiceName, InstanceId, HostName, ProcessId
    await Assert.That(values.Length).IsEqualTo(4);
  }

  #endregion

  #region PopulateFromServiceAttribute Constructor Tests

  [Test]
  public async Task PopulateFromServiceAttribute_Constructor_SetsKindPropertyAsync() {
    // Arrange & Act
    var attribute = new PopulateFromServiceAttribute(ServiceKind.ServiceName);

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(ServiceKind.ServiceName);
  }

  [Test]
  public async Task PopulateFromServiceAttribute_Constructor_WithInstanceId_SetsKindAsync() {
    // Arrange & Act
    var attribute = new PopulateFromServiceAttribute(ServiceKind.InstanceId);

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(ServiceKind.InstanceId);
  }

  [Test]
  public async Task PopulateFromServiceAttribute_Constructor_WithHostName_SetsKindAsync() {
    // Arrange & Act
    var attribute = new PopulateFromServiceAttribute(ServiceKind.HostName);

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(ServiceKind.HostName);
  }

  [Test]
  public async Task PopulateFromServiceAttribute_Constructor_WithProcessId_SetsKindAsync() {
    // Arrange & Act
    var attribute = new PopulateFromServiceAttribute(ServiceKind.ProcessId);

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(ServiceKind.ProcessId);
  }

  #endregion

  #region PopulateFromServiceAttribute Usage Tests

  [Test]
  public async Task PopulateFromServiceAttribute_AttributeUsage_AllowsPropertyTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateFromServiceAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Property)).IsTrue();
  }

  [Test]
  public async Task PopulateFromServiceAttribute_AttributeUsage_AllowsParameterTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateFromServiceAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Parameter)).IsTrue();
  }

  [Test]
  public async Task PopulateFromServiceAttribute_AttributeUsage_DoesNotAllowMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateFromServiceAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task PopulateFromServiceAttribute_AttributeUsage_IsInheritedAsync() {
    // Arrange & Act
    var attributeUsage = typeof(PopulateFromServiceAttribute)
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
  public async Task PopulateFromServiceAttribute_InheritedFromBaseRecord_IsDiscoverableAsync() {
    // Arrange - Derived record inherits from base with attribute
    var derivedType = typeof(DerivedEventWithService);
    var property = derivedType.GetProperty("ProcessedBy");

    // Act - Should find inherited attribute
    var attribute = property?.GetCustomAttributes(typeof(PopulateFromServiceAttribute), inherit: true)
      .Cast<PopulateFromServiceAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Kind).IsEqualTo(ServiceKind.ServiceName);
  }

  [Test]
  public async Task PopulateFromServiceAttribute_AllServiceKinds_OnSameRecord_AreDiscoverableAsync() {
    // Arrange
    var type = typeof(TestEventWithAllServiceKinds);
    var serviceNameProp = type.GetProperty("ServiceName");
    var instanceIdProp = type.GetProperty("InstanceId");
    var hostNameProp = type.GetProperty("HostName");
    var processIdProp = type.GetProperty("ProcessId");

    // Act
    var serviceNameAttr = serviceNameProp?.GetCustomAttributes(typeof(PopulateFromServiceAttribute), true)
      .Cast<PopulateFromServiceAttribute>().FirstOrDefault();
    var instanceIdAttr = instanceIdProp?.GetCustomAttributes(typeof(PopulateFromServiceAttribute), true)
      .Cast<PopulateFromServiceAttribute>().FirstOrDefault();
    var hostNameAttr = hostNameProp?.GetCustomAttributes(typeof(PopulateFromServiceAttribute), true)
      .Cast<PopulateFromServiceAttribute>().FirstOrDefault();
    var processIdAttr = processIdProp?.GetCustomAttributes(typeof(PopulateFromServiceAttribute), true)
      .Cast<PopulateFromServiceAttribute>().FirstOrDefault();

    // Assert
    await Assert.That(serviceNameAttr).IsNotNull();
    await Assert.That(serviceNameAttr!.Kind).IsEqualTo(ServiceKind.ServiceName);
    await Assert.That(instanceIdAttr).IsNotNull();
    await Assert.That(instanceIdAttr!.Kind).IsEqualTo(ServiceKind.InstanceId);
    await Assert.That(hostNameAttr).IsNotNull();
    await Assert.That(hostNameAttr!.Kind).IsEqualTo(ServiceKind.HostName);
    await Assert.That(processIdAttr).IsNotNull();
    await Assert.That(processIdAttr!.Kind).IsEqualTo(ServiceKind.ProcessId);
  }

  // Base record with service attribute
  private record BaseEventWithService {
    [PopulateFromService(ServiceKind.ServiceName)]
    public string? ProcessedBy { get; init; }
  }

  // Derived record inherits the attribute
  private sealed record DerivedEventWithService : BaseEventWithService {
    public string? AdditionalData { get; init; }
  }

  // Record with all service kinds
  private sealed record TestEventWithAllServiceKinds(
    Guid Id,
    [property: PopulateFromService(ServiceKind.ServiceName)] string? ServiceName = null,
    [property: PopulateFromService(ServiceKind.InstanceId)] Guid? InstanceId = null,
    [property: PopulateFromService(ServiceKind.HostName)] string? HostName = null,
    [property: PopulateFromService(ServiceKind.ProcessId)] int? ProcessId = null
  );

  #endregion
}
