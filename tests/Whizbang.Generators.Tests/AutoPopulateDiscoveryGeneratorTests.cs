using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for AutoPopulateDiscoveryGenerator.
/// Validates discovery and registration of auto-populate attributes on message properties.
/// </summary>
/// <tests>Whizbang.Generators/AutoPopulateDiscoveryGenerator.cs</tests>
[Category("Generators")]
[Category("AutoPopulate")]
public class AutoPopulateDiscoveryGeneratorTests {

  // ============================================================================
  // Basic Discovery Tests
  // ============================================================================

  /// <summary>
  /// Test that generator discovers PopulateTimestamp attribute and generates registry.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithTimestampAttribute_GeneratesRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record OrderCreatedEvent(
              Guid OrderId,
              [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("OrderCreatedEvent");
    await Assert.That(code).Contains("SentAt");
    await Assert.That(code).Contains("TimestampKind.SentAt");
  }

  /// <summary>
  /// Test that generator discovers PopulateFromContext attribute.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithContextAttribute_GeneratesRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record OrderCreatedEvent(
              Guid OrderId,
              [property: PopulateFromContext(ContextKind.UserId)] string? CreatedBy = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("OrderCreatedEvent");
    await Assert.That(code).Contains("CreatedBy");
    await Assert.That(code).Contains("ContextKind.UserId");
  }

  /// <summary>
  /// Test that generator discovers PopulateFromService attribute.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithServiceAttribute_GeneratesRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record OrderCreatedEvent(
              Guid OrderId,
              [property: PopulateFromService(ServiceKind.ServiceName)] string? ProcessedBy = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("OrderCreatedEvent");
    await Assert.That(code).Contains("ProcessedBy");
    await Assert.That(code).Contains("ServiceKind.ServiceName");
  }

  /// <summary>
  /// Test that generator discovers PopulateFromIdentifier attribute.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithIdentifierAttribute_GeneratesRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record OrderCreatedEvent(
              Guid OrderId,
              [property: PopulateFromIdentifier(IdentifierKind.CorrelationId)] Guid? CorrelationId = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("OrderCreatedEvent");
    await Assert.That(code).Contains("CorrelationId");
    await Assert.That(code).Contains("IdentifierKind.CorrelationId");
  }

  // ============================================================================
  // Multiple Attributes Tests
  // ============================================================================

  /// <summary>
  /// Test that generator discovers multiple different attribute types on same message.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMultipleAttributeTypes_DiscoversAllAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record OrderCreatedEvent(
              Guid OrderId,
              [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null,
              [property: PopulateFromContext(ContextKind.UserId)] string? CreatedBy = null,
              [property: PopulateFromService(ServiceKind.ServiceName)] string? ProcessedBy = null,
              [property: PopulateFromIdentifier(IdentifierKind.CorrelationId)] Guid? CorrelationId = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("SentAt");
    await Assert.That(code).Contains("CreatedBy");
    await Assert.That(code).Contains("ProcessedBy");
    await Assert.That(code).Contains("CorrelationId");
  }

  /// <summary>
  /// Test that generator discovers attributes across multiple message types.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMultipleMessageTypes_DiscoversAllAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record OrderCreatedEvent(
              Guid OrderId,
              [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null
            );

            public record OrderShippedEvent(
              Guid OrderId,
              [property: PopulateFromContext(ContextKind.UserId)] string? ShippedBy = null
            );

            public record OrderDeliveredEvent(
              Guid OrderId,
              [property: PopulateTimestamp(TimestampKind.DeliveredAt)] DateTimeOffset? DeliveredAt = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("OrderCreatedEvent");
    await Assert.That(code).Contains("OrderShippedEvent");
    await Assert.That(code).Contains("OrderDeliveredEvent");
  }

  // ============================================================================
  // All Enum Values Tests
  // ============================================================================

  /// <summary>
  /// Test that generator handles all TimestampKind values.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithAllTimestampKinds_GeneratesAllAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record FullTimestampEvent(
              Guid Id,
              [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null,
              [property: PopulateTimestamp(TimestampKind.QueuedAt)] DateTimeOffset? QueuedAt = null,
              [property: PopulateTimestamp(TimestampKind.DeliveredAt)] DateTimeOffset? DeliveredAt = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("TimestampKind.SentAt");
    await Assert.That(code).Contains("TimestampKind.QueuedAt");
    await Assert.That(code).Contains("TimestampKind.DeliveredAt");
  }

  /// <summary>
  /// Test that generator handles all ContextKind values.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithAllContextKinds_GeneratesAllAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record FullContextEvent(
              Guid Id,
              [property: PopulateFromContext(ContextKind.UserId)] string? UserId = null,
              [property: PopulateFromContext(ContextKind.TenantId)] string? TenantId = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("ContextKind.UserId");
    await Assert.That(code).Contains("ContextKind.TenantId");
  }

  /// <summary>
  /// Test that generator handles all ServiceKind values.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithAllServiceKinds_GeneratesAllAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record FullServiceEvent(
              Guid Id,
              [property: PopulateFromService(ServiceKind.ServiceName)] string? ServiceName = null,
              [property: PopulateFromService(ServiceKind.InstanceId)] Guid? InstanceId = null,
              [property: PopulateFromService(ServiceKind.HostName)] string? HostName = null,
              [property: PopulateFromService(ServiceKind.ProcessId)] int? ProcessId = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("ServiceKind.ServiceName");
    await Assert.That(code).Contains("ServiceKind.InstanceId");
    await Assert.That(code).Contains("ServiceKind.HostName");
    await Assert.That(code).Contains("ServiceKind.ProcessId");
  }

  /// <summary>
  /// Test that generator handles all IdentifierKind values.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithAllIdentifierKinds_GeneratesAllAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record FullIdentifierEvent(
              Guid Id,
              [property: PopulateFromIdentifier(IdentifierKind.MessageId)] Guid? MessageId = null,
              [property: PopulateFromIdentifier(IdentifierKind.CorrelationId)] Guid? CorrelationId = null,
              [property: PopulateFromIdentifier(IdentifierKind.CausationId)] Guid? CausationId = null,
              [property: PopulateFromIdentifier(IdentifierKind.StreamId)] Guid? StreamId = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("IdentifierKind.MessageId");
    await Assert.That(code).Contains("IdentifierKind.CorrelationId");
    await Assert.That(code).Contains("IdentifierKind.CausationId");
    await Assert.That(code).Contains("IdentifierKind.StreamId");
  }

  // ============================================================================
  // Registry Pattern Tests
  // ============================================================================

  /// <summary>
  /// Test that generated code implements IAutoPopulateRegistry.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ImplementsIAutoPopulateRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record TestEvent(
              Guid Id,
              [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("IAutoPopulateRegistry");
    await Assert.That(code).Contains("class GeneratedAutoPopulateRegistry_");
  }

  /// <summary>
  /// Test that generated code includes ModuleInitializer for auto-registration.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GeneratesModuleInitializerAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record TestEvent(
              Guid Id,
              [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("[ModuleInitializer]");
    await Assert.That(code).Contains("AutoPopulateRegistry.Register");
  }

  /// <summary>
  /// Test that generated code uses priority 100 for contracts assemblies.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_UsesPriority100Async() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record TestEvent(
              Guid Id,
              [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("priority: 100");
  }

  // ============================================================================
  // AOT Compatibility Tests
  // ============================================================================

  /// <summary>
  /// Test that generated code is AOT-compatible (no reflection).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_OutputIsAotCompatible_NoReflectionAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record TestEvent(
              Guid Id,
              [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    // Should NOT use reflection APIs
    await Assert.That(code!.Contains("GetMethod")).IsFalse();
    await Assert.That(code.Contains("Activator.CreateInstance")).IsFalse();
    await Assert.That(code.Contains("Invoke(")).IsFalse();
    // Should use typeof() for type comparison
    await Assert.That(code).Contains("typeof(");
  }

  /// <summary>
  /// Test that generated code produces no compilation errors.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ProducesCompilableCodeAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public record TestEvent(
              Guid Id,
              [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null,
              [property: PopulateFromContext(ContextKind.UserId)] string? CreatedBy = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert - no compilation errors in generated code
    var errors = result.Diagnostics
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .ToArray();

    await Assert.That(errors).IsEmpty();
  }

  // ============================================================================
  // Edge Cases
  // ============================================================================

  /// <summary>
  /// Test that generator produces empty registry when no attributes exist.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithNoAttributes_GeneratesEmptyRegistryAsync() {
    // Arrange
    var source = """
            using System;

            namespace TestApp;

            public record OrderCreatedEvent(Guid OrderId);
            public record OrderShippedEvent(Guid OrderId, string TrackingNumber);
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    // Generator may or may not produce output for empty case
    if (code is not null) {
      await Assert.That(code).Contains("AutoPopulateRegistry");
    }
  }

  /// <summary>
  /// Test that generator handles class-based message types (not just records).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithClassMessage_GeneratesRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public class OrderCreatedCommand {
              public Guid OrderId { get; set; }

              [PopulateTimestamp(TimestampKind.SentAt)]
              public DateTimeOffset? SentAt { get; set; }

              [PopulateFromContext(ContextKind.UserId)]
              public string? CreatedBy { get; set; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("OrderCreatedCommand");
    await Assert.That(code).Contains("SentAt");
    await Assert.That(code).Contains("CreatedBy");
  }

  /// <summary>
  /// Test that generator discovers inherited attributes from base class.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithInheritedAttribute_DiscoversFromBaseAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public abstract class BaseEvent {
              [PopulateTimestamp(TimestampKind.SentAt)]
              public DateTimeOffset? SentAt { get; init; }

              [PopulateFromContext(ContextKind.UserId)]
              public string? CreatedBy { get; init; }
            }

            public class OrderCreatedEvent : BaseEvent {
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    // Should discover attributes from derived type (inherited from base)
    await Assert.That(code!).Contains("OrderCreatedEvent");
    await Assert.That(code).Contains("SentAt");
    await Assert.That(code).Contains("CreatedBy");
  }

  /// <summary>
  /// Test that only public types are discovered (not internal/private).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_IgnoresNonPublicTypes_Async() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            internal record InternalEvent(
              Guid Id,
              [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null
            );

            public record PublicEvent(
              Guid Id,
              [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null
            );
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("PublicEvent");
    // Internal type should NOT be in the registry
    await Assert.That(code.Contains("InternalEvent")).IsFalse();
  }
}
