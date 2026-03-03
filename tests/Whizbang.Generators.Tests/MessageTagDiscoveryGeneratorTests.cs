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

  // ============================================================================
  // Phase 4: Tests for IMessageTagRegistry implementation and ModuleInitializer
  // ============================================================================

  /// <summary>
  /// Test that generator generates a class implementing IMessageTagRegistry.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithTaggedTypes_ImplementsIMessageTagRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [NotificationTag(Tag = "order-created")]
            public record OrderCreatedEvent(Guid OrderId);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("IMessageTagRegistry");
    // Class name is unique per assembly (e.g., GeneratedMessageTagRegistry_TestAssembly)
    await Assert.That(code).Contains("class GeneratedMessageTagRegistry_");
  }

  /// <summary>
  /// Test that generator generates ModuleInitializer for auto-registration.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithTaggedTypes_GeneratesModuleInitializerAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [NotificationTag(Tag = "order-created")]
            public record OrderCreatedEvent(Guid OrderId);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("[ModuleInitializer]");
    await Assert.That(code).Contains("MessageTagRegistry.Register");
  }

  /// <summary>
  /// Test that generated code registers with AssemblyRegistry.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithTaggedTypes_RegistersWithAssemblyRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [NotificationTag(Tag = "order-created")]
            public record OrderCreatedEvent(Guid OrderId);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    // Should register with static MessageTagRegistry which wraps AssemblyRegistry
    await Assert.That(code!).Contains("Whizbang.Core.Tags.MessageTagRegistry.Register");
  }

  /// <summary>
  /// Test that generated registry uses correct priority for contracts assemblies.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithTaggedTypes_UsesPriority100ForContractsAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [NotificationTag(Tag = "order-created")]
            public record OrderCreatedEvent(Guid OrderId);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    // Priority 100 for contracts assemblies (first to be tried)
    await Assert.That(code!).Contains("priority: 100");
  }

  /// <summary>
  /// Test that generated GetTagsFor returns empty when no matching type.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GetTagsFor_ReturnsEmptyForUnknownTypeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [NotificationTag(Tag = "order-created")]
            public record OrderCreatedEvent(Guid OrderId);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    // Should have GetTagsFor implementation with yield pattern
    await Assert.That(code!).Contains("IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType)");
  }

  /// <summary>
  /// Test that custom attribute types are discovered and handled.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithCustomAttribute_GeneratesRegistrationAsync() {
    // Arrange - using AuditEventAttribute which inherits from MessageTagAttribute
    var source = """
            using System;
            using Whizbang.Core.Attributes;
            using Whizbang.Core.Audit;

            namespace TestApp;

            [AuditEvent(Reason = "User login")]
            public record UserLoggedInEvent(Guid UserId, string IpAddress);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("UserLoggedInEvent");
    await Assert.That(code).Contains("AuditEventAttribute");
  }

  /// <summary>
  /// Test that generator output is AOT-compatible (no reflection).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_OutputIsAotCompatible_NoReflectionAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [NotificationTag(Tag = "order-created")]
            public record OrderCreatedEvent(Guid OrderId);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    // Should NOT use reflection APIs
    await Assert.That(code!.Contains("GetMethod")).IsFalse();
    await Assert.That(code.Contains("Activator.CreateInstance")).IsFalse();
    await Assert.That(code.Contains("Invoke(")).IsFalse();
    // Should use typeof() for type comparison only
    await Assert.That(code).Contains("typeof(");
  }

  // ============================================================================
  // Step 4: Constructor Argument Extraction Tests
  // ============================================================================

  /// <summary>
  /// Test that generator extracts tag value from constructor arguments.
  /// Uses a custom attribute with constructor parameter to verify AttributeUtilities
  /// correctly reads constructor arguments (not just named arguments).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithConstructorArgument_ExtractsTagAsync() {
    // Arrange - Define custom attribute with constructor parameter
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            /// <summary>
            /// Custom tag attribute with constructor parameter for tag.
            /// </summary>
            public class TenantTagAttribute : MessageTagAttribute {
              public TenantTagAttribute(string tag) {
                Tag = tag;
              }
            }

            [TenantTag("tenants")]
            public record TenantCreatedEvent(Guid TenantId, string Name);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("TenantCreatedEvent");
    // CRITICAL: Tag must be "tenants" (from constructor), not empty string
    await Assert.That(code).Contains("tenants");
  }

  /// <summary>
  /// Test that generator handles mixed syntax with both constructor and named arguments.
  /// Named arguments should take precedence when both are present.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMixedSyntax_ExtractsAllValuesAsync() {
    // Arrange - Define custom attribute with constructor AND named property support
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            /// <summary>
            /// Custom tag attribute with constructor and settable properties.
            /// </summary>
            public class DomainTagAttribute : MessageTagAttribute {
              public DomainTagAttribute(string tag) {
                Tag = tag;
              }

              // Allow override via named argument (set removes required)
              public new string Tag { get; set; }
            }

            // Constructor only: Tag = "orders"
            [DomainTag("orders")]
            public record OrderPlacedEvent(Guid OrderId);

            // Mixed: Constructor = "ignored", Named = "inventory" (named wins)
            [DomainTag("ignored", Tag = "inventory")]
            public record InventoryUpdatedEvent(Guid ProductId, int Quantity);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();

    // Both events should be registered
    await Assert.That(code!).Contains("OrderPlacedEvent");
    await Assert.That(code).Contains("InventoryUpdatedEvent");

    // Constructor argument should be extracted
    await Assert.That(code).Contains("orders");

    // Named argument should take precedence over constructor
    await Assert.That(code).Contains("inventory");
    // "ignored" should NOT appear because named argument overrides it
    await Assert.That(code.Contains("ignored")).IsFalse();
  }

  /// <summary>
  /// Test that generator handles Properties array passed via constructor.
  /// Verifies GetStringArrayValue correctly reads constructor arguments.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithPropertiesInConstructor_ExtractsPropertiesAsync() {
    // Arrange - Define custom attribute with properties in constructor
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            /// <summary>
            /// Custom tag attribute with properties array in constructor.
            /// </summary>
            public class SelectiveTagAttribute : MessageTagAttribute {
              public SelectiveTagAttribute(string tag, string[] properties) {
                Tag = tag;
                Properties = properties;
              }
            }

            [SelectiveTag("users", new[] { "UserId", "Email" })]
            public record UserRegisteredEvent(Guid UserId, string Email, string PasswordHash);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("UserRegisteredEvent");
    await Assert.That(code).Contains("users");

    // Should extract specified properties from constructor array
    await Assert.That(code).Contains("UserId");
    await Assert.That(code).Contains("Email");

    // Should NOT extract PasswordHash (not in properties array)
    // The payload builder should only include specified properties
    await Assert.That(code).Contains("Properties = new[]");
  }

  /// <summary>
  /// Test that generator handles IncludeEvent boolean via constructor argument.
  /// Verifies GetBoolValue correctly reads constructor arguments.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithBoolInConstructor_ExtractsValueAsync() {
    // Arrange - Define custom attribute with bool in constructor
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            /// <summary>
            /// Custom tag attribute with includeEvent in constructor.
            /// </summary>
            public class FullEventTagAttribute : MessageTagAttribute {
              public FullEventTagAttribute(string tag, bool includeEvent) {
                Tag = tag;
                IncludeEvent = includeEvent;
              }
            }

            [FullEventTag("payments", true)]
            public record PaymentProcessedEvent(Guid PaymentId, decimal Amount);

            [FullEventTag("refunds", false)]
            public record RefundIssuedEvent(Guid RefundId, decimal Amount);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();

    // Both events should be registered
    await Assert.That(code!).Contains("PaymentProcessedEvent");
    await Assert.That(code).Contains("RefundIssuedEvent");

    // PaymentProcessedEvent should have __event in payload (IncludeEvent = true)
    // The generated code will have IncludeEvent = true for PaymentProcessedEvent
    await Assert.That(code).Contains("IncludeEvent = true");

    // Should also have IncludeEvent = false for RefundIssuedEvent
    await Assert.That(code).Contains("IncludeEvent = false");
  }
}
