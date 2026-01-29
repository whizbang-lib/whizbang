using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Persistence;

namespace Whizbang.Core.Tests.Persistence;

/// <summary>
/// Tests for <see cref="PersistenceStrategyAttribute"/> and <see cref="PersistenceMode"/>.
/// Validates the per-receptor persistence configuration.
/// </summary>
/// <tests>Whizbang.Core/Attributes/PersistenceStrategyAttribute.cs</tests>
/// <tests>Whizbang.Core/Persistence/PersistenceMode.cs</tests>
[Category("Core")]
[Category("Persistence")]
public class PersistenceStrategyTests {

  // ========================================
  // PersistenceMode Enum Tests
  // ========================================

  [Test]
  public async Task PersistenceMode_HasImmediateValueAsync() {
    // Assert
    await Assert.That(Enum.IsDefined(PersistenceMode.Immediate)).IsTrue();
  }

  [Test]
  public async Task PersistenceMode_HasBatchedValueAsync() {
    // Assert
    await Assert.That(Enum.IsDefined(PersistenceMode.Batched)).IsTrue();
  }

  [Test]
  public async Task PersistenceMode_HasOutboxValueAsync() {
    // Assert
    await Assert.That(Enum.IsDefined(PersistenceMode.Outbox)).IsTrue();
  }

  [Test]
  public async Task PersistenceMode_DefaultIsImmediateAsync() {
    // Arrange
    var defaultValue = default(PersistenceMode);

    // Assert
    await Assert.That(defaultValue).IsEqualTo(PersistenceMode.Immediate);
  }

  // ========================================
  // PersistenceStrategyAttribute Tests
  // ========================================

  [Test]
  public async Task PersistenceStrategyAttribute_AttributeUsage_AllowsClassTargetAsync() {
    // Arrange
    var usage = typeof(PersistenceStrategyAttribute).GetCustomAttribute<AttributeUsageAttribute>();

    // Assert
    await Assert.That(usage).IsNotNull();
    await Assert.That((usage!.ValidOn & AttributeTargets.Class) != 0).IsTrue();
  }

  [Test]
  public async Task PersistenceStrategyAttribute_AttributeUsage_DoesNotAllowMultipleAsync() {
    // Arrange
    var usage = typeof(PersistenceStrategyAttribute).GetCustomAttribute<AttributeUsageAttribute>();

    // Assert
    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task PersistenceStrategyAttribute_AttributeUsage_AllowsInheritedAsync() {
    // Arrange
    var usage = typeof(PersistenceStrategyAttribute).GetCustomAttribute<AttributeUsageAttribute>();

    // Assert
    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.Inherited).IsTrue();
  }

  [Test]
  public async Task PersistenceStrategyAttribute_Mode_CanBeSetViaConstructorAsync() {
    // Arrange & Act
    var attr = new PersistenceStrategyAttribute(PersistenceMode.Batched);

    // Assert
    await Assert.That(attr.Mode).IsEqualTo(PersistenceMode.Batched);
  }

  [Test]
  public async Task PersistenceStrategyAttribute_StrategyName_CanBeSetViaConstructorAsync() {
    // Arrange & Act
    var attr = new PersistenceStrategyAttribute("high-throughput-batch");

    // Assert
    await Assert.That(attr.StrategyName).IsEqualTo("high-throughput-batch");
    await Assert.That(attr.Mode is null).IsTrue();
  }

  [Test]
  public async Task PersistenceStrategyAttribute_Mode_IsNullWhenUsingStrategyNameAsync() {
    // Arrange & Act
    var attr = new PersistenceStrategyAttribute("custom-strategy");

    // Assert
    await Assert.That(attr.Mode is null).IsTrue();
  }

  [Test]
  public async Task PersistenceStrategyAttribute_StrategyName_IsNullWhenUsingModeAsync() {
    // Arrange & Act
    var attr = new PersistenceStrategyAttribute(PersistenceMode.Immediate);

    // Assert
    await Assert.That(attr.StrategyName is null).IsTrue();
  }

  [Test]
  public async Task PersistenceStrategyAttribute_CanBeAppliedToReceptorAsync() {
    // Arrange
    var type = typeof(TestBatchedReceptor);

    // Act
    var attr = type.GetCustomAttribute<PersistenceStrategyAttribute>();

    // Assert
    await Assert.That(attr).IsNotNull();
    await Assert.That(attr!.Mode).IsEqualTo(PersistenceMode.Batched);
  }

  [Test]
  public async Task PersistenceStrategyAttribute_CanBeAppliedWithCustomStrategyAsync() {
    // Arrange
    var type = typeof(TestCustomStrategyReceptor);

    // Act
    var attr = type.GetCustomAttribute<PersistenceStrategyAttribute>();

    // Assert
    await Assert.That(attr).IsNotNull();
    await Assert.That(attr!.StrategyName).IsEqualTo("high-throughput");
  }

  [Test]
  public async Task PersistenceStrategyAttribute_NotPresentOnDefaultReceptorAsync() {
    // Arrange
    var type = typeof(TestDefaultReceptor);

    // Act
    var attr = type.GetCustomAttribute<PersistenceStrategyAttribute>();

    // Assert
    await Assert.That(attr is null).IsTrue();
  }

  // Test types
  [PersistenceStrategy(PersistenceMode.Batched)]
  private sealed class TestBatchedReceptor { }

  [PersistenceStrategy("high-throughput")]
  private sealed class TestCustomStrategyReceptor { }

  private sealed class TestDefaultReceptor { }
}
