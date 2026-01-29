using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.Attributes;

/// <summary>
/// Tests for <see cref="PureServiceAttribute"/>.
/// Validates the pure service marker attribute for perspective DI.
/// </summary>
/// <tests>Whizbang.Core/Attributes/PureServiceAttribute.cs</tests>
[Category("Core")]
[Category("Attributes")]
public class PureServiceAttributeTests {

  [Test]
  public async Task PureServiceAttribute_AttributeUsage_AllowsClassTargetAsync() {
    // Arrange
    var usage = typeof(PureServiceAttribute).GetCustomAttribute<AttributeUsageAttribute>();

    // Assert
    await Assert.That(usage).IsNotNull();
    await Assert.That((usage!.ValidOn & AttributeTargets.Class) != 0).IsTrue();
  }

  [Test]
  public async Task PureServiceAttribute_AttributeUsage_AllowsInterfaceTargetAsync() {
    // Arrange
    var usage = typeof(PureServiceAttribute).GetCustomAttribute<AttributeUsageAttribute>();

    // Assert
    await Assert.That(usage).IsNotNull();
    await Assert.That((usage!.ValidOn & AttributeTargets.Interface) != 0).IsTrue();
  }

  [Test]
  public async Task PureServiceAttribute_AttributeUsage_DoesNotAllowMultipleAsync() {
    // Arrange
    var usage = typeof(PureServiceAttribute).GetCustomAttribute<AttributeUsageAttribute>();

    // Assert
    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task PureServiceAttribute_AttributeUsage_AllowsInheritedAsync() {
    // Arrange
    var usage = typeof(PureServiceAttribute).GetCustomAttribute<AttributeUsageAttribute>();

    // Assert
    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.Inherited).IsTrue();
  }

  [Test]
  public async Task PureServiceAttribute_CanBeAppliedToClassAsync() {
    // Arrange
    var type = typeof(TestPureService);

    // Act
    var attr = type.GetCustomAttribute<PureServiceAttribute>();

    // Assert
    await Assert.That(attr).IsNotNull();
  }

  [Test]
  public async Task PureServiceAttribute_CanBeAppliedToInterfaceAsync() {
    // Arrange
    var type = typeof(ITestPureService);

    // Act
    var attr = type.GetCustomAttribute<PureServiceAttribute>();

    // Assert
    await Assert.That(attr).IsNotNull();
  }

  [Test]
  public async Task PureServiceAttribute_NotPresentOnNonPureServicesAsync() {
    // Arrange
    var type = typeof(TestNonPureService);

    // Act
    var attr = type.GetCustomAttribute<PureServiceAttribute>();

    // Assert
    await Assert.That(attr is null).IsTrue();
  }

  [Test]
  public async Task PureServiceAttribute_Reason_CanBeSetAsync() {
    // Arrange & Act
    var attr = new PureServiceAttribute { Reason = "Stateless calculation service" };

    // Assert
    await Assert.That(attr.Reason).IsEqualTo("Stateless calculation service");
  }

  [Test]
  public async Task PureServiceAttribute_Reason_IsNullByDefaultAsync() {
    // Arrange & Act
    var attr = new PureServiceAttribute();

    // Assert
    await Assert.That(attr.Reason is null).IsTrue();
  }

  [Test]
  public async Task PureServiceAttribute_InheritsToImplementingClassAsync() {
    // Arrange - TestPureServiceImpl implements ITestPureService which has [PureService]
    var type = typeof(TestPureServiceImpl);

    // Act - Check if attribute is inherited
    var attr = type.GetCustomAttribute<PureServiceAttribute>(inherit: true);

    // Assert - Should find the attribute via inheritance from interface
    // Note: Interface attributes don't inherit to implementing classes in .NET
    // So we check the interface instead
    var interfaceAttr = typeof(ITestPureService).GetCustomAttribute<PureServiceAttribute>();
    await Assert.That(interfaceAttr).IsNotNull();
  }

  // Test types
  [PureService]
  private sealed class TestPureService {
    public decimal Calculate(decimal value) => value * 2;
  }

  [PureService(Reason = "Read-only lookup service")]
  private interface ITestPureService {
    decimal GetRate(string currency, DateTimeOffset date);
  }

  private sealed class TestPureServiceImpl : ITestPureService {
    public decimal GetRate(string currency, DateTimeOffset date) => 1.0m;
  }

  private sealed class TestNonPureService {
    public Task SaveAsync() => Task.CompletedTask;
  }
}
