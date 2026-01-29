using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for MessageTagDiscoveryGenerator.
/// Validates discovery and registration of message tag attributes.
/// </summary>
/// <tests>Whizbang.Generators/MessageTagDiscoveryGenerator.cs</tests>
[Category("Generators")]
[Category("Tags")]
public class MessageTagDiscoveryGeneratorTests {

  /// <summary>
  /// Test that generator discovers NotificationTagAttribute and generates registry.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithNotificationTag_GeneratesRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [NotificationTag(Tag = "order-created", Properties = ["OrderId", "CustomerId"])]
            public record OrderCreatedEvent(Guid OrderId, Guid CustomerId, string Status);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("OrderCreatedEvent");
    await Assert.That(code).Contains("order-created");
  }

  /// <summary>
  /// Test that generator handles TelemetryTagAttribute.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithTelemetryTag_GeneratesRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;
            using Whizbang.Core.Tags;

            namespace TestApp;

            [TelemetryTag(Tag = "payment-processed", SpanName = "ProcessPayment", Kind = SpanKind.Internal)]
            public record PaymentProcessedEvent(Guid PaymentId, decimal Amount);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("PaymentProcessedEvent");
    await Assert.That(code).Contains("payment-processed");
  }

  /// <summary>
  /// Test that generator handles MetricTagAttribute.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMetricTag_GeneratesRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;
            using Whizbang.Core.Tags;

            namespace TestApp;

            [MetricTag(Tag = "orders-metric", MetricName = "orders.created", Type = MetricType.Counter)]
            public record OrderCountEvent(Guid OrderId);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("OrderCountEvent");
    await Assert.That(code).Contains("orders-metric");
  }

  /// <summary>
  /// Test that generator handles AuditEventAttribute (inherits from MessageTagAttribute).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithAuditEvent_GeneratesRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;
            using Whizbang.Core.Audit;

            namespace TestApp;

            [AuditEvent(Reason = "Customer data accessed", Level = AuditLevel.Warning)]
            public record CustomerDataViewedEvent(Guid CustomerId, string ViewedBy);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("CustomerDataViewedEvent");
    // Note: Generator can't see constructor-initialized Tag values, only named arguments
    await Assert.That(code).Contains("AuditEventAttribute");
  }

  /// <summary>
  /// Test that generator handles multiple tagged types.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMultipleTaggedTypes_GeneratesAllAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [NotificationTag(Tag = "order-created")]
            public record OrderCreatedEvent(Guid OrderId);

            [NotificationTag(Tag = "order-shipped")]
            public record OrderShippedEvent(Guid OrderId, string TrackingNumber);

            [NotificationTag(Tag = "order-cancelled")]
            public record OrderCancelledEvent(Guid OrderId, string Reason);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("order-created");
    await Assert.That(code).Contains("order-shipped");
    await Assert.That(code).Contains("order-cancelled");
  }

  /// <summary>
  /// Test that generator handles IncludeEvent property.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithIncludeEvent_GeneratesPayloadWithEventAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [NotificationTag(Tag = "order-updated", IncludeEvent = true)]
            public record OrderUpdatedEvent(Guid OrderId, string Status);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("__event");
  }

  /// <summary>
  /// Test that generator handles ExtraJson property.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithExtraJson_GeneratesMergeCodeAsync() {
    // Arrange
    var source = """"
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [NotificationTag(Tag = "order-updated", ExtraJson = """{"source": "api"}""")]
            public record OrderUpdatedEvent(Guid OrderId);
            """";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    // Should have code to merge extra JSON
    await Assert.That(code!).Contains("source");
  }

  /// <summary>
  /// Test that generator handles Properties array correctly.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithProperties_GeneratesPropertyExtractorsAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [NotificationTag(Tag = "order-event", Properties = ["OrderId", "Status", "Total"])]
            public record OrderEvent(Guid OrderId, string Status, decimal Total, string InternalNote);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    // Should extract only specified properties
    await Assert.That(code!).Contains("OrderId");
    await Assert.That(code).Contains("Status");
    await Assert.That(code).Contains("Total");
  }

  /// <summary>
  /// Test that generator produces no output when no tagged types exist.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithNoTaggedTypes_GeneratesEmptyRegistryAsync() {
    // Arrange
    var source = """
            using System;

            namespace TestApp;

            public record OrderCreatedEvent(Guid OrderId);
            public record OrderShippedEvent(Guid OrderId);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    // Generator may or may not produce output for empty case
    // If it does, should have empty registry
    if (code is not null) {
      await Assert.That(code).Contains("MessageTagRegistry");
    }
  }

  /// <summary>
  /// Test that generator produces compilable code.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ProducesCompilableCodeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [NotificationTag(Tag = "test-event", Properties = ["Id"])]
            public record TestEvent(Guid Id, string Name);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert - no compilation errors in generated code
    var errors = result.Diagnostics
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .ToArray();

    await Assert.That(errors).IsEmpty();
  }
}
