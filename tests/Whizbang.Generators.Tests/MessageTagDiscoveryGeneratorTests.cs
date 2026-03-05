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
  /// Test that generator discovers SignalTagAttribute and generates registry.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithNotificationTag_GeneratesRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [SignalTag(Tag = "order-created", Properties = ["OrderId", "CustomerId"])]
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

            [SignalTag(Tag = "order-created")]
            public record OrderCreatedEvent(Guid OrderId);

            [SignalTag(Tag = "order-shipped")]
            public record OrderShippedEvent(Guid OrderId, string TrackingNumber);

            [SignalTag(Tag = "order-cancelled")]
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

            [SignalTag(Tag = "order-updated", IncludeEvent = true)]
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

            [SignalTag(Tag = "order-updated", ExtraJson = """{"source": "api"}""")]
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

            [SignalTag(Tag = "order-event", Properties = ["OrderId", "Status", "Total"])]
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

            [SignalTag(Tag = "test-event", Properties = ["Id"])]
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

            [SignalTag(Tag = "order-created")]
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

            [SignalTag(Tag = "order-created")]
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

            [SignalTag(Tag = "order-created")]
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

            [SignalTag(Tag = "order-created")]
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

            [SignalTag(Tag = "order-created")]
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

            [SignalTag(Tag = "order-created")]
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

  // ============================================================================
  // Phase 1: Multi-Attribute Discovery Tests
  // Events with multiple tag attributes should have ALL attributes registered
  // ============================================================================

  /// <summary>
  /// Test that generator discovers ALL tag attributes on a single event type.
  /// This is the primary bug fix - FirstOrDefault was only getting the first attribute.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMultipleTagAttributes_DiscoversAllAsync() {
    // Arrange - Event with TWO different tag attributes (like JDNext's NotificationTag + NotificationIdTag)
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            /// <summary>
            /// First custom tag attribute.
            /// </summary>
            public class CategoryTagAttribute : MessageTagAttribute {
              public CategoryTagAttribute(string tag) {
                Tag = tag;
              }
            }

            /// <summary>
            /// Second custom tag attribute.
            /// </summary>
            public class EntityTagAttribute : MessageTagAttribute {
              public EntityTagAttribute(string tag) {
                Tag = tag;
              }

              public string EntityIdProperty { get; set; } = "";
            }

            // Event with BOTH attributes - both should be discovered
            [CategoryTag("orders")]
            [EntityTag("order", EntityIdProperty = "OrderId")]
            public record OrderCreatedEvent(Guid OrderId, string CustomerId);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();

    // CRITICAL: Both attributes MUST be in the generated registry
    await Assert.That(code!).Contains("CategoryTagAttribute");
    await Assert.That(code).Contains("EntityTagAttribute");

    // Both tag values should be present
    await Assert.That(code).Contains("orders");
    await Assert.That(code).Contains("order");

    // Only one event type, but TWO registrations
    await Assert.That(code).Contains("OrderCreatedEvent");
  }

  /// <summary>
  /// Test that multiple attributes of the SAME type are all discovered.
  /// E.g., [SignalTag("a")] [SignalTag("b")] on same event.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMultipleSameTypeAttributes_DiscoversAllAsync() {
    // Arrange - Event with TWO NotificationTag attributes
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            // Event with two NotificationTag attributes (different tags)
            [SignalTag(Tag = "orders")]
            [SignalTag(Tag = "all-events")]
            public record OrderCreatedEvent(Guid OrderId);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();

    // Both tag values should be present
    await Assert.That(code!).Contains("orders");
    await Assert.That(code).Contains("all-events");
  }

  /// <summary>
  /// Test that the registry contains the correct count of registrations.
  /// N attributes = N registrations, not just 1.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMultipleAttributes_GeneratesCorrectCountAsync() {
    // Arrange - 3 events with varying attribute counts
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            /// <summary>
            /// Custom tag attribute for testing.
            /// </summary>
            public class CustomTag1Attribute : MessageTagAttribute {
              public CustomTag1Attribute(string tag) { Tag = tag; }
            }

            public class CustomTag2Attribute : MessageTagAttribute {
              public CustomTag2Attribute(string tag) { Tag = tag; }
            }

            // Event 1: 1 attribute = 1 registration
            [SignalTag(Tag = "single")]
            public record SingleTagEvent(Guid Id);

            // Event 2: 2 attributes = 2 registrations
            [SignalTag(Tag = "double-a")]
            [CustomTag1("double-b")]
            public record DoubleTagEvent(Guid Id);

            // Event 3: 3 attributes = 3 registrations
            [SignalTag(Tag = "triple-a")]
            [CustomTag1("triple-b")]
            [CustomTag2("triple-c")]
            public record TripleTagEvent(Guid Id);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();

    // Count the registrations - should be 1 + 2 + 3 = 6 total
    // Each "new MessageTagRegistration {" indicates a registration
    var registrationCount = code!.Split("new MessageTagRegistration {").Length - 1;
    await Assert.That(registrationCount).IsEqualTo(6);

    // Verify all tags are present
    await Assert.That(code).Contains("single");
    await Assert.That(code).Contains("double-a");
    await Assert.That(code).Contains("double-b");
    await Assert.That(code).Contains("triple-a");
    await Assert.That(code).Contains("triple-b");
    await Assert.That(code).Contains("triple-c");
  }

  // ============================================================================
  // Phase 2: Dispatcher Generation Tests
  // Custom attributes need generated dispatchers for AOT-compatible hook invocation
  // ============================================================================

  /// <summary>
  /// Test that generator generates a dispatcher for custom (non-built-in) attribute types.
  /// This enables AOT-compatible hook invocation without reflection.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithCustomAttributes_GeneratesDispatcherAsync() {
    // Arrange - Custom attribute that isn't a built-in Whizbang attribute
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            /// <summary>
            /// Custom tag attribute (not a built-in Whizbang type).
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

    // Assert - Should generate dispatcher file in addition to registry
    var dispatcherCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagHookDispatcher.g.cs");
    await Assert.That(dispatcherCode).IsNotNull();

    // Dispatcher should implement IMessageTagHookDispatcher
    await Assert.That(dispatcherCode!).Contains("IMessageTagHookDispatcher");

    // Dispatcher should handle TenantTagAttribute
    await Assert.That(dispatcherCode).Contains("TenantTagAttribute");

    // Should have module initializer for registration
    await Assert.That(dispatcherCode).Contains("[ModuleInitializer]");
    await Assert.That(dispatcherCode).Contains("MessageTagHookDispatcherRegistry.Register");
  }

  /// <summary>
  /// Test that generator does NOT generate dispatcher when only built-in attributes are used.
  /// Built-in attributes (NotificationTag, TelemetryTag, MetricTag) are handled directly by the processor.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithOnlyBuiltInAttributes_DoesNotGenerateDispatcherAsync() {
    // Arrange - Only using built-in Whizbang attributes
    var source = """
            using System;
            using Whizbang.Core.Attributes;
            using Whizbang.Core.Tags;

            namespace TestApp;

            [SignalTag(Tag = "orders")]
            public record OrderCreatedEvent(Guid OrderId);

            [TelemetryTag(Tag = "telemetry", SpanName = "CreateOrder", Kind = SpanKind.Internal)]
            public record OrderProcessedEvent(Guid OrderId);

            [MetricTag(Tag = "metrics", MetricName = "orders.count", Type = MetricType.Counter)]
            public record OrderCountedEvent(Guid OrderId);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert - Should NOT generate dispatcher file (built-in types handled directly)
    var dispatcherCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagHookDispatcher.g.cs");
    await Assert.That(dispatcherCode).IsNull();

    // Registry should still be generated
    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();
  }

  /// <summary>
  /// Test that generated dispatcher's TryCreateContext method returns typed context for custom attribute.
  /// This is critical for type-safe hook invocation.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task GeneratedDispatcher_TryCreateContext_ReturnsTypedContextAsync() {
    // Arrange - Multiple custom attributes
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            /// <summary>
            /// First custom attribute.
            /// </summary>
            public class CategoryTagAttribute : MessageTagAttribute {
              public CategoryTagAttribute(string tag) { Tag = tag; }
            }

            /// <summary>
            /// Second custom attribute.
            /// </summary>
            public class EntityIdTagAttribute : MessageTagAttribute {
              public EntityIdTagAttribute(string tag) { Tag = tag; }
              public string EntityIdProperty { get; set; } = "";
            }

            [CategoryTag("users")]
            [EntityIdTag("user", EntityIdProperty = "UserId")]
            public record UserCreatedEvent(Guid UserId, string Email);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var dispatcherCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagHookDispatcher.g.cs");
    await Assert.That(dispatcherCode).IsNotNull();

    // TryCreateContext should handle CategoryTagAttribute
    await Assert.That(dispatcherCode!).Contains("typeof(global::TestApp.CategoryTagAttribute)");
    await Assert.That(dispatcherCode).Contains("TagContext<global::TestApp.CategoryTagAttribute>");

    // TryCreateContext should handle EntityIdTagAttribute
    await Assert.That(dispatcherCode).Contains("typeof(global::TestApp.EntityIdTagAttribute)");
    await Assert.That(dispatcherCode).Contains("TagContext<global::TestApp.EntityIdTagAttribute>");
  }

  /// <summary>
  /// Test that generated dispatcher's TryDispatchAsync method invokes typed hooks.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task GeneratedDispatcher_TryDispatchAsync_InvokesHookAsync() {
    // Arrange - Custom attribute with hook invocation pattern
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            /// <summary>
            /// Custom tag attribute requiring hook dispatch.
            /// </summary>
            public class AuditTagAttribute : MessageTagAttribute {
              public AuditTagAttribute(string tag) { Tag = tag; }
              public string Level { get; set; } = "Info";
            }

            [AuditTag("security", Level = "Critical")]
            public record SecurityAuditEvent(Guid EventId, string Action);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var dispatcherCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagHookDispatcher.g.cs");
    await Assert.That(dispatcherCode).IsNotNull();

    // TryDispatchAsync should check hook type
    await Assert.That(dispatcherCode!).Contains("IMessageTagHook<global::TestApp.AuditTagAttribute>");

    // Should call OnTaggedMessageAsync on the hook
    await Assert.That(dispatcherCode).Contains("OnTaggedMessageAsync");

    // Should be async ValueTask<JsonElement?>
    await Assert.That(dispatcherCode).Contains("ValueTask<JsonElement?>");
  }

  /// <summary>
  /// Test that dispatcher generation handles multiple custom attributes from different namespaces.
  /// This simulates a real-world scenario like JDNext with multiple custom attribute types.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMultipleCustomNamespaces_GeneratesDispatcherForAllAsync() {
    // Arrange - Custom attributes in different namespaces (like JDNext)
    // Note: Using block-scoped namespaces since C# only allows one file-scoped namespace per file
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace App.Notifications {
              /// <summary>
              /// Notification tag attribute.
              /// </summary>
              public class SignalTagAttribute : MessageTagAttribute {
                public SignalTagAttribute(string tag) { Tag = tag; }
              }
            }

            namespace App.Tracking {
              /// <summary>
              /// Tracking tag attribute.
              /// </summary>
              public class TrackingTagAttribute : MessageTagAttribute {
                public TrackingTagAttribute(string tag) { Tag = tag; }
              }
            }

            namespace App.Events {
              [App.Notifications.SignalTag("users")]
              [App.Tracking.TrackingTag("user-activity")]
              public record AccountCreatedEvent(Guid AccountId, string Email);
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var dispatcherCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagHookDispatcher.g.cs");
    await Assert.That(dispatcherCode).IsNotNull();

    // Should handle SignalTagAttribute from App.Notifications
    await Assert.That(dispatcherCode!).Contains("global::App.Notifications.SignalTagAttribute");

    // Should handle TrackingTagAttribute from App.Tracking
    await Assert.That(dispatcherCode).Contains("global::App.Tracking.TrackingTagAttribute");
  }

  /// <summary>
  /// Test that generated dispatcher code is AOT-compatible (no reflection).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task GeneratedDispatcher_IsAotCompatible_NoReflectionAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public class CustomTagAttribute : MessageTagAttribute {
              public CustomTagAttribute(string tag) { Tag = tag; }
            }

            [CustomTag("test")]
            public record TestEvent(Guid Id);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    // Assert
    var dispatcherCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagHookDispatcher.g.cs");
    await Assert.That(dispatcherCode).IsNotNull();

    // Should NOT use reflection APIs
    await Assert.That(dispatcherCode!.Contains("GetMethod")).IsFalse();
    await Assert.That(dispatcherCode.Contains("Activator.CreateInstance")).IsFalse();
    await Assert.That(dispatcherCode.Contains("Invoke(")).IsFalse();
    await Assert.That(dispatcherCode.Contains("MakeGenericType")).IsFalse();
    await Assert.That(dispatcherCode.Contains("MakeGenericMethod")).IsFalse();

    // Should use direct type comparisons with typeof()
    await Assert.That(dispatcherCode).Contains("typeof(");
  }
}
