using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for [GenerateStreamId] source generator support in StreamIdGenerator.
/// Verifies that GetGenerationPolicy is correctly generated with the right switch arms.
/// </summary>
public class GenerateStreamIdGeneratorTests {
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithGenerateStreamIdAttribute_GeneratesGetGenerationPolicyAsync() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record OrderCreatedEvent : IEvent {
              [StreamId] [GenerateStreamId]
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should generate GetGenerationPolicy method with entry for OrderCreatedEvent
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("GetGenerationPolicy");
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("(true, false)"); // ShouldGenerate=true, OnlyIfEmpty=false (default)
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithOnlyIfEmptyTrue_GeneratesCorrectPolicyAsync() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record InventoryReservedEvent : IEvent {
              [StreamId] [GenerateStreamId(OnlyIfEmpty = true)]
              public Guid ReservationId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - OnlyIfEmpty should be true
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("GetGenerationPolicy");
    await Assert.That(generatedSource).Contains("InventoryReservedEvent");
    await Assert.That(generatedSource).Contains("(true, true)"); // ShouldGenerate=true, OnlyIfEmpty=true
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithoutGenerateStreamId_NoGenerationPolicyEntryAsync() {
    // Arrange - Event has [StreamId] but NOT [GenerateStreamId]
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record OrderItemAddedEvent : IEvent {
              [StreamId]
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - GetGenerationPolicy should exist but NOT have an entry for this event
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("GetGenerationPolicy");
    // The method exists but there's no dispatch case for OrderItemAddedEvent
    // It falls through to the default return (false, false)
    await Assert.That(generatedSource).DoesNotContain("(true, false)");
    await Assert.That(generatedSource).DoesNotContain("(true, true)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithClassLevelGenerateStreamId_GeneratesPolicyAsync() {
    // Arrange - [GenerateStreamId] on class, [StreamId] inherited from base
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public abstract record BaseEvent : IEvent {
              [StreamId]
              public Guid StreamId { get; init; }
            }

            [GenerateStreamId]
            public record OrderCreatedEvent : BaseEvent {
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should generate GetGenerationPolicy for the derived type with class-level attribute
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("GetGenerationPolicy");
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("(true, false)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleEvents_GeneratesCorrectPoliciesAsync() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record OrderCreatedEvent : IEvent {
              [StreamId] [GenerateStreamId]
              public Guid OrderId { get; init; }
            }

            public record InventoryReservedEvent : IEvent {
              [StreamId] [GenerateStreamId(OnlyIfEmpty = true)]
              public Guid ReservationId { get; init; }
            }

            public record OrderItemAddedEvent : IEvent {
              [StreamId]
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Only events with [GenerateStreamId] should appear in generation policy
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("GetGenerationPolicy");
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("InventoryReservedEvent");
    // OrderItemAddedEvent should NOT have a generation policy entry
    // It should still have extract methods but not generation policy
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithRecordParameter_GeneratesPolicyAsync() {
    // Arrange - [property: GenerateStreamId] on record parameter
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record OrderCreatedEvent([property: StreamId] [property: GenerateStreamId] Guid OrderId) : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("GetGenerationPolicy");
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("(true, false)");
  }
}
