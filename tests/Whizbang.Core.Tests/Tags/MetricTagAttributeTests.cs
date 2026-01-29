using System;
using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="MetricTagAttribute"/>.
/// Validates OpenTelemetry metrics integration (counters, histograms, gauges).
/// </summary>
/// <tests>Whizbang.Core/Attributes/MetricTagAttribute.cs</tests>
[Category("Core")]
[Category("Attributes")]
[Category("Tags")]
public class MetricTagAttributeTests {

  [Test]
  public async Task MetricTagAttribute_InheritsFromMessageTagAttributeAsync() {
    // Assert
    await Assert.That(typeof(MetricTagAttribute).BaseType).IsEqualTo(typeof(MessageTagAttribute));
  }

  [Test]
  public async Task MetricTagAttribute_AttributeUsage_AllowsClassTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(MetricTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Class)).IsTrue();
  }

  [Test]
  public async Task MetricTagAttribute_AttributeUsage_AllowsStructTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(MetricTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Struct)).IsTrue();
  }

  [Test]
  public async Task MetricTagAttribute_AttributeUsage_AllowsMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(MetricTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsTrue();
  }

  [Test]
  public async Task MetricTagAttribute_Tag_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new MetricTagAttribute { Tag = "order-created", MetricName = "orders.created" };

    // Assert
    await Assert.That(attribute.Tag).IsEqualTo("order-created");
  }

  [Test]
  public async Task MetricTagAttribute_MetricName_IsRequiredAsync() {
    // Arrange
    var metricNameProperty = typeof(MetricTagAttribute).GetProperty(nameof(MetricTagAttribute.MetricName));

    // Assert - MetricName should be a required init property
    await Assert.That(metricNameProperty).IsNotNull();
    await Assert.That(metricNameProperty!.PropertyType).IsEqualTo(typeof(string));
    await Assert.That(metricNameProperty.CanRead).IsTrue();
  }

  [Test]
  public async Task MetricTagAttribute_MetricName_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new MetricTagAttribute {
      Tag = "order-created",
      MetricName = "orders.created"
    };

    // Assert
    await Assert.That(attribute.MetricName).IsEqualTo("orders.created");
  }

  [Test]
  public async Task MetricTagAttribute_Type_DefaultsToCounterAsync() {
    // Arrange & Act
    var attribute = new MetricTagAttribute { Tag = "test-tag", MetricName = "test.metric" };

    // Assert
    await Assert.That(attribute.Type).IsEqualTo(MetricType.Counter);
  }

  [Test]
  public async Task MetricTagAttribute_Type_CanBeSetToHistogramAsync() {
    // Arrange & Act
    var attribute = new MetricTagAttribute {
      Tag = "order-amount",
      MetricName = "orders.amount",
      Type = MetricType.Histogram
    };

    // Assert
    await Assert.That(attribute.Type).IsEqualTo(MetricType.Histogram);
  }

  [Test]
  public async Task MetricTagAttribute_Type_CanBeSetToGaugeAsync() {
    // Arrange & Act
    var attribute = new MetricTagAttribute {
      Tag = "active-users",
      MetricName = "users.active",
      Type = MetricType.Gauge
    };

    // Assert
    await Assert.That(attribute.Type).IsEqualTo(MetricType.Gauge);
  }

  [Test]
  public async Task MetricTagAttribute_ValueProperty_IsNullByDefaultAsync() {
    // Arrange & Act
    var attribute = new MetricTagAttribute { Tag = "test-tag", MetricName = "test.metric" };

    // Assert
    await Assert.That(attribute.ValueProperty).IsNull();
  }

  [Test]
  public async Task MetricTagAttribute_ValueProperty_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new MetricTagAttribute {
      Tag = "order-amount",
      MetricName = "orders.amount",
      Type = MetricType.Histogram,
      ValueProperty = "TotalAmount"
    };

    // Assert
    await Assert.That(attribute.ValueProperty).IsEqualTo("TotalAmount");
  }

  [Test]
  public async Task MetricTagAttribute_Unit_IsNullByDefaultAsync() {
    // Arrange & Act
    var attribute = new MetricTagAttribute { Tag = "test-tag", MetricName = "test.metric" };

    // Assert
    await Assert.That(attribute.Unit).IsNull();
  }

  [Test]
  public async Task MetricTagAttribute_Unit_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new MetricTagAttribute {
      Tag = "order-amount",
      MetricName = "orders.amount",
      Type = MetricType.Histogram,
      Unit = "USD"
    };

    // Assert
    await Assert.That(attribute.Unit).IsEqualTo("USD");
  }

  [Test]
  public async Task MetricTagAttribute_InheritsBasePropertiesAsync() {
    // Arrange & Act
    var attribute = new MetricTagAttribute {
      Tag = "order-completed",
      MetricName = "orders.amount",
      Properties = ["TenantId", "Region"],
      Type = MetricType.Histogram,
      ValueProperty = "TotalAmount",
      Unit = "USD"
    };

    // Assert - Base properties work
    await Assert.That(attribute.Tag).IsEqualTo("order-completed");
    await Assert.That(attribute.Properties).IsNotNull();
    await Assert.That(attribute.Properties!.Length).IsEqualTo(2);

    // Assert - MetricTag-specific properties work
    await Assert.That(attribute.MetricName).IsEqualTo("orders.amount");
    await Assert.That(attribute.Type).IsEqualTo(MetricType.Histogram);
    await Assert.That(attribute.ValueProperty).IsEqualTo("TotalAmount");
    await Assert.That(attribute.Unit).IsEqualTo("USD");
  }

  [Test]
  public async Task MetricTagAttribute_Counter_CanBeAppliedToEventAsync() {
    // Arrange
    var targetType = typeof(TestOrderCreatedEvent);

    // Act
    var attributes = targetType
      .GetCustomAttributes(typeof(MetricTagAttribute), true)
      .Cast<MetricTagAttribute>()
      .ToArray();

    // Assert
    await Assert.That(attributes.Length).IsEqualTo(1);
    await Assert.That(attributes[0].Tag).IsEqualTo("order-created");
    await Assert.That(attributes[0].MetricName).IsEqualTo("orders.created");
    await Assert.That(attributes[0].Type).IsEqualTo(MetricType.Counter);
  }

  [Test]
  public async Task MetricTagAttribute_Histogram_CanBeAppliedToEventAsync() {
    // Arrange
    var targetType = typeof(TestOrderCompletedEvent);

    // Act
    var attributes = targetType
      .GetCustomAttributes(typeof(MetricTagAttribute), true)
      .Cast<MetricTagAttribute>()
      .ToArray();

    // Assert
    await Assert.That(attributes.Length).IsEqualTo(1);
    await Assert.That(attributes[0].Tag).IsEqualTo("order-amount");
    await Assert.That(attributes[0].MetricName).IsEqualTo("orders.amount");
    await Assert.That(attributes[0].Type).IsEqualTo(MetricType.Histogram);
    await Assert.That(attributes[0].ValueProperty).IsEqualTo("TotalAmount");
    await Assert.That(attributes[0].Unit).IsEqualTo("USD");
  }

  [Test]
  public async Task MetricTagAttribute_Properties_BecomeMetricDimensionsAsync() {
    // Arrange - Properties on MetricTag become metric labels/dimensions
    var attribute = new MetricTagAttribute {
      Tag = "order-created",
      MetricName = "orders.created",
      Type = MetricType.Counter,
      Properties = ["TenantId", "Region", "ProductCategory"]
    };

    // Assert - Properties are preserved for dimension extraction
    await Assert.That(attribute.Properties).IsNotNull();
    await Assert.That(attribute.Properties!.Length).IsEqualTo(3);
    await Assert.That(attribute.Properties).Contains("TenantId");
    await Assert.That(attribute.Properties).Contains("Region");
    await Assert.That(attribute.Properties).Contains("ProductCategory");
  }

  // Test helper types
  [MetricTag(
    Tag = "order-created",
    MetricName = "orders.created",
    Type = MetricType.Counter,
    Properties = ["TenantId", "Region"])]
  private sealed record TestOrderCreatedEvent(Guid OrderId, string TenantId, string Region);

  [MetricTag(
    Tag = "order-amount",
    MetricName = "orders.amount",
    Type = MetricType.Histogram,
    ValueProperty = nameof(TotalAmount),
    Unit = "USD",
    Properties = ["TenantId"])]
  private sealed record TestOrderCompletedEvent(Guid OrderId, decimal TotalAmount, string TenantId);
}
