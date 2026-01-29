using System;
using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="TelemetryTagAttribute"/>.
/// Validates OpenTelemetry distributed tracing integration.
/// </summary>
/// <tests>Whizbang.Core/Attributes/TelemetryTagAttribute.cs</tests>
[Category("Core")]
[Category("Attributes")]
[Category("Tags")]
public class TelemetryTagAttributeTests {

  [Test]
  public async Task TelemetryTagAttribute_InheritsFromMessageTagAttributeAsync() {
    // Assert
    await Assert.That(typeof(TelemetryTagAttribute).BaseType).IsEqualTo(typeof(MessageTagAttribute));
  }

  [Test]
  public async Task TelemetryTagAttribute_AttributeUsage_AllowsClassTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(TelemetryTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Class)).IsTrue();
  }

  [Test]
  public async Task TelemetryTagAttribute_AttributeUsage_AllowsStructTargetAsync() {
    // Arrange & Act
    var attributeUsage = typeof(TelemetryTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn.HasFlag(AttributeTargets.Struct)).IsTrue();
  }

  [Test]
  public async Task TelemetryTagAttribute_AttributeUsage_AllowsMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(TelemetryTagAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsTrue();
  }

  [Test]
  public async Task TelemetryTagAttribute_Tag_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new TelemetryTagAttribute { Tag = "payment-processed" };

    // Assert
    await Assert.That(attribute.Tag).IsEqualTo("payment-processed");
  }

  [Test]
  public async Task TelemetryTagAttribute_SpanName_IsNullByDefaultAsync() {
    // Arrange & Act
    var attribute = new TelemetryTagAttribute { Tag = "test-tag" };

    // Assert
    await Assert.That(attribute.SpanName).IsNull();
  }

  [Test]
  public async Task TelemetryTagAttribute_SpanName_CanBeSetAsync() {
    // Arrange & Act
    var attribute = new TelemetryTagAttribute {
      Tag = "payment-processed",
      SpanName = "ProcessPayment"
    };

    // Assert
    await Assert.That(attribute.SpanName).IsEqualTo("ProcessPayment");
  }

  [Test]
  public async Task TelemetryTagAttribute_Kind_DefaultsToInternalAsync() {
    // Arrange & Act
    var attribute = new TelemetryTagAttribute { Tag = "test-tag" };

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(SpanKind.Internal);
  }

  [Test]
  public async Task TelemetryTagAttribute_Kind_CanBeSetToServerAsync() {
    // Arrange & Act
    var attribute = new TelemetryTagAttribute {
      Tag = "api-request",
      Kind = SpanKind.Server
    };

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(SpanKind.Server);
  }

  [Test]
  public async Task TelemetryTagAttribute_Kind_CanBeSetToClientAsync() {
    // Arrange & Act
    var attribute = new TelemetryTagAttribute {
      Tag = "external-api-call",
      Kind = SpanKind.Client
    };

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(SpanKind.Client);
  }

  [Test]
  public async Task TelemetryTagAttribute_Kind_CanBeSetToProducerAsync() {
    // Arrange & Act
    var attribute = new TelemetryTagAttribute {
      Tag = "message-published",
      Kind = SpanKind.Producer
    };

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(SpanKind.Producer);
  }

  [Test]
  public async Task TelemetryTagAttribute_Kind_CanBeSetToConsumerAsync() {
    // Arrange & Act
    var attribute = new TelemetryTagAttribute {
      Tag = "message-received",
      Kind = SpanKind.Consumer
    };

    // Assert
    await Assert.That(attribute.Kind).IsEqualTo(SpanKind.Consumer);
  }

  [Test]
  public async Task TelemetryTagAttribute_RecordAsEvent_DefaultsToTrueAsync() {
    // Arrange & Act
    var attribute = new TelemetryTagAttribute { Tag = "test-tag" };

    // Assert
    await Assert.That(attribute.RecordAsEvent).IsTrue();
  }

  [Test]
  public async Task TelemetryTagAttribute_RecordAsEvent_CanBeSetToFalseAsync() {
    // Arrange & Act
    var attribute = new TelemetryTagAttribute {
      Tag = "span-only",
      RecordAsEvent = false
    };

    // Assert
    await Assert.That(attribute.RecordAsEvent).IsFalse();
  }

  [Test]
  public async Task TelemetryTagAttribute_InheritsBasePropertiesAsync() {
    // Arrange & Act
    var attribute = new TelemetryTagAttribute {
      Tag = "payment-processed",
      Properties = ["PaymentId", "Amount", "Currency"],
      IncludeEvent = true,
      SpanName = "ProcessPayment",
      Kind = SpanKind.Internal,
      RecordAsEvent = true
    };

    // Assert - Base properties work
    await Assert.That(attribute.Tag).IsEqualTo("payment-processed");
    await Assert.That(attribute.Properties).IsNotNull();
    await Assert.That(attribute.Properties!.Length).IsEqualTo(3);
    await Assert.That(attribute.IncludeEvent).IsTrue();

    // Assert - TelemetryTag-specific properties work
    await Assert.That(attribute.SpanName).IsEqualTo("ProcessPayment");
    await Assert.That(attribute.Kind).IsEqualTo(SpanKind.Internal);
    await Assert.That(attribute.RecordAsEvent).IsTrue();
  }

  [Test]
  public async Task TelemetryTagAttribute_CanBeAppliedToEventAsync() {
    // Arrange
    var targetType = typeof(TestPaymentProcessedEvent);

    // Act
    var attributes = targetType
      .GetCustomAttributes(typeof(TelemetryTagAttribute), true)
      .Cast<TelemetryTagAttribute>()
      .ToArray();

    // Assert
    await Assert.That(attributes.Length).IsEqualTo(1);
    await Assert.That(attributes[0].Tag).IsEqualTo("payment-processed");
    await Assert.That(attributes[0].SpanName).IsEqualTo("ProcessPayment");
    await Assert.That(attributes[0].Kind).IsEqualTo(SpanKind.Internal);
  }

  // Test helper type
  [TelemetryTag(
    Tag = "payment-processed",
    Properties = ["PaymentId", "Amount", "Currency"],
    SpanName = "ProcessPayment",
    Kind = SpanKind.Internal)]
  private sealed record TestPaymentProcessedEvent(Guid PaymentId, decimal Amount, string Currency);
}
