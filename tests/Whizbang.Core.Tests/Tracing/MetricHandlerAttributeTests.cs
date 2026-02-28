using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for <see cref="MetricHandlerAttribute"/>.
/// Validates attribute construction and usage constraints.
/// </summary>
[Category("Core")]
[Category("Tracing")]
public class MetricHandlerAttributeTests {

  #region Constructor Tests

  [Test]
  public async Task Constructor_CreatesInstance_SuccessfullyAsync() {
    // Arrange & Act
    var attribute = new MetricHandlerAttribute();

    // Assert - attribute is created successfully
    await Assert.That(attribute).IsNotNull();
  }

  #endregion

  #region Attribute Usage Tests

  [Test]
  public async Task AttributeUsage_AllowsClassTarget_OnlyAsync() {
    // Arrange
    var attributeType = typeof(MetricHandlerAttribute);
    var usageAttr = attributeType.GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert - only allows class targets
    await Assert.That(usageAttr).IsNotNull();
    await Assert.That(usageAttr!.ValidOn).IsEqualTo(AttributeTargets.Class);
  }

  [Test]
  public async Task AttributeUsage_AllowMultiple_IsFalseAsync() {
    // Arrange
    var attributeType = typeof(MetricHandlerAttribute);
    var usageAttr = attributeType.GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert - only one MetricHandler attribute per class
    await Assert.That(usageAttr).IsNotNull();
    await Assert.That(usageAttr!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task AttributeUsage_Inherited_IsFalseAsync() {
    // Arrange
    var attributeType = typeof(MetricHandlerAttribute);
    var usageAttr = attributeType.GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .FirstOrDefault();

    // Assert - attribute is not inherited to derived classes
    await Assert.That(usageAttr).IsNotNull();
    await Assert.That(usageAttr!.Inherited).IsFalse();
  }

  #endregion

  #region Applied To Class Tests

  [Test]
  public async Task MetricHandler_CanBeApplied_ToClassAsync() {
    // Arrange - class with MetricHandler attribute
    var classType = typeof(TestMetricHandlerClass);

    // Act
    var attributes = classType.GetCustomAttributes(typeof(MetricHandlerAttribute), false);

    // Assert
    await Assert.That(attributes.Length).IsEqualTo(1);
  }

  [Test]
  public async Task MetricHandler_CanCombine_WithTraceHandlerAsync() {
    // Arrange - class with both MetricHandler and TraceHandler attributes
    var classType = typeof(TestTracedAndMetricHandlerClass);

    // Act
    var metricAttrs = classType.GetCustomAttributes(typeof(MetricHandlerAttribute), false);
    var traceAttrs = classType.GetCustomAttributes(typeof(TraceHandlerAttribute), false);

    // Assert - both attributes can be applied
    await Assert.That(metricAttrs.Length).IsEqualTo(1);
    await Assert.That(traceAttrs.Length).IsEqualTo(1);
  }

  #endregion

  #region Test Classes

  [MetricHandler]
  private sealed class TestMetricHandlerClass { }

  [TraceHandler]
  [MetricHandler]
  private sealed class TestTracedAndMetricHandlerClass { }

  #endregion
}
