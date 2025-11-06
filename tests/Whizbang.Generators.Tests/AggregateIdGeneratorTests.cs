using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for AggregateIdGenerator - ensures zero-reflection aggregate ID extraction.
/// Following TDD: These tests are written BEFORE the generator implementation.
/// All tests should FAIL initially (RED phase), then pass after implementation (GREEN phase).
/// </summary>
public class AggregateIdGeneratorTests {
  [Test]
  public async Task Generator_WithAggregateIdAttribute_GeneratesExtractorAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder {
              [AggregateId]
              public Guid OrderId { get; init; }

              public string ProductName { get; init; } = string.Empty;
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Generator should produce AggregateIdExtractors.g.cs
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Assert - Should contain extraction logic
    await Assert.That(generatedSource!).Contains("ExtractAggregateId");
    await Assert.That(generatedSource).Contains("CreateOrder");
    await Assert.That(generatedSource).Contains("OrderId");
  }

  [Test]
  public async Task Generator_WithMultipleMessageTypes_GeneratesAllExtractorsAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder {
              [AggregateId]
              public Guid OrderId { get; init; }
            }

            public record UpdateCustomer {
              [AggregateId]
              public Guid CustomerId { get; init; }
              public string Name { get; init; } = string.Empty;
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should generate extractors for both types
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("CreateOrder");
    await Assert.That(generatedSource).Contains("UpdateCustomer");
    await Assert.That(generatedSource).Contains("OrderId");
    await Assert.That(generatedSource).Contains("CustomerId");
  }

  [Test]
  public async Task Generator_WithNullableGuid_HandlesCorrectlyAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder {
              [AggregateId]
              public Guid? OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should generate extractor that handles nullable Guid
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("OrderId");
  }

  [Test]
  public async Task Generator_WithNonGuidProperty_ReportsDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder {
              [AggregateId]
              public string OrderId { get; init; } = string.Empty; // WRONG: should be Guid
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should report WHIZ005 error
    var diagnostics = result.Diagnostics;
    var error = diagnostics.FirstOrDefault(d => d.Id == "WHIZ005");
    await Assert.That(error).IsNotNull();
    await Assert.That(error!.Severity).IsEqualTo(DiagnosticSeverity.Error);
  }

  [Test]
  public async Task Generator_WithMultipleAggregateIds_ReportsWarningAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder {
              [AggregateId]
              public Guid OrderId { get; init; }

              [AggregateId] // WRONG: Multiple [AggregateId] attributes
              public Guid CustomerId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should report WHIZ006 warning
    var diagnostics = result.Diagnostics;
    var warning = diagnostics.FirstOrDefault(d => d.Id == "WHIZ006");
    await Assert.That(warning).IsNotNull();
    await Assert.That(warning!.Severity).IsEqualTo(DiagnosticSeverity.Warning);
  }

  [Test]
  public async Task Generator_WithNoAggregateIds_GeneratesEmptyRegistryAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder {
              public Guid OrderId { get; init; }
              // No [AggregateId] attribute
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should still generate file but with empty/null-returning extractor
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("ExtractAggregateId");
    await Assert.That(generatedSource).Contains("return null"); // No extractors found
  }

  [Test]
  public async Task GeneratedExtractor_WithValidMessage_ExtractsCorrectIdAsync() {
    // Arrange - This test verifies the generated code actually works
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder {
              [AggregateId]
              public Guid OrderId { get; init; }

              public string ProductName { get; init; } = string.Empty;
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should generate working extractor
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Verify the extractor signature
    await Assert.That(generatedSource!).Contains("public static Guid? ExtractAggregateId(object message, Type messageType)");
  }

  [Test]
  public async Task GeneratedExtractor_WithUnknownType_ReturnsNullAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder {
              [AggregateId]
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Generated code should handle unknown types gracefully
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("return null"); // Fallback for unknown types
  }

  [Test]
  public async Task Generator_ReportsInfoDiagnostic_WhenPropertyDiscoveredAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder {
              [AggregateId]
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should report WHIZ004 info diagnostic
    var diagnostics = result.Diagnostics;
    var info = diagnostics.FirstOrDefault(d => d.Id == "WHIZ004");
    await Assert.That(info).IsNotNull();
    await Assert.That(info!.Severity).IsEqualTo(DiagnosticSeverity.Info);
    await Assert.That(info.GetMessage()).Contains("CreateOrder");
    await Assert.That(info.GetMessage()).Contains("OrderId");
  }

  [Test]
  public async Task Generator_WithInheritedAttribute_DiscoversPropertyAsync() {
    // Arrange - Test that [AggregateId] is inherited
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public abstract record OrderCommand {
              [AggregateId]
              public Guid OrderId { get; init; }
            }

            public record CreateOrder : OrderCommand {
              public string ProductName { get; init; } = string.Empty;
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should generate extractor for CreateOrder (inherits [AggregateId])
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("CreateOrder");
  }

  [Test]
  public async Task Generator_GeneratesCodeInCorrectNamespaceAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder {
              [AggregateId]
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Generated code should be in Whizbang.Core.Generated namespace
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("namespace Whizbang.Core.Generated");
  }

  [Test]
  public async Task Generator_GeneratesAutoGeneratedHeaderAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder {
              [AggregateId]
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should have auto-generated header
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("// <auto-generated/>");
    await Assert.That(generatedSource).Contains("#nullable enable");
  }
}
