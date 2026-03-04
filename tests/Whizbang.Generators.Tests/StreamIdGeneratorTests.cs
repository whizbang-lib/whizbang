using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for StreamIdGenerator - ensures zero-reflection aggregate ID extraction.
/// Following TDD: These tests are written BEFORE the generator implementation.
/// All tests should FAIL initially (RED phase), then pass after implementation (GREEN phase).
/// </summary>
public class StreamIdGeneratorTests {
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithStreamIdAttribute_GeneratesExtractorAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder : IEvent {
              [StreamId]
              public Guid OrderId { get; init; }

              public string ProductName { get; init; } = string.Empty;
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Generator should produce StreamIdExtractors.g.cs
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Assert - Should contain extraction logic for Resolve method
    await Assert.That(generatedSource!).Contains("Resolve");
    await Assert.That(generatedSource).Contains("CreateOrder");
    await Assert.That(generatedSource).Contains("OrderId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleMessageTypes_GeneratesAllExtractorsAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder : IEvent {
              [StreamId]
              public Guid OrderId { get; init; }
            }

            public record UpdateCustomer : IEvent {
              [StreamId]
              public Guid CustomerId { get; init; }
              public string Name { get; init; } = string.Empty;
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should generate extractors for both types
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("CreateOrder");
    await Assert.That(generatedSource).Contains("UpdateCustomer");
    await Assert.That(generatedSource).Contains("OrderId");
    await Assert.That(generatedSource).Contains("CustomerId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNullableGuid_HandlesCorrectlyAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder : IEvent {
              [StreamId]
              public Guid? OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should generate extractor that handles nullable Guid
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("OrderId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNoStreamIds_OnEvent_ReportsWarningAsync() {
    // Arrange - IEvent without [StreamId] should report WHIZ009 warning
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder : IEvent {
              public Guid OrderId { get; init; }
              // No [StreamId] attribute
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should report WHIZ009 warning for missing [StreamId]
    var diagnostics = result.Diagnostics;
    var warning = diagnostics.FirstOrDefault(d => d.Id == "WHIZ009");
    await Assert.That(warning).IsNotNull();
    await Assert.That(warning!.Severity).IsEqualTo(DiagnosticSeverity.Warning);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNoEvents_GeneratesEmptyRegistryAsync() {
    // Arrange - No IEvent types
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder {
              public Guid OrderId { get; init; }
              // No [StreamId] attribute and no IEvent
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should still generate file but with no event extractors
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    // No events, so Resolve method should throw for any event passed
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task GeneratedExtractor_WithValidEvent_GeneratesResolveMethodAsync() {
    // Arrange - This test verifies the generated code contains Resolve method
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder : IEvent {
              [StreamId]
              public Guid OrderId { get; init; }

              public string ProductName { get; init; } = string.Empty;
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should generate working extractor
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Verify the Resolve method signature exists
    await Assert.That(generatedSource!).Contains("public static string Resolve(global::Whizbang.Core.IEvent @event)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task GeneratedExtractor_WithEvent_GeneratesTryResolveAsGuidAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder : IEvent {
              [StreamId]
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Generated code should have TryResolveAsGuid
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("TryResolveAsGuid");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReportsInfoDiagnostic_WhenPropertyDiscoveredAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder : IEvent {
              [StreamId]
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should report WHIZ010 info diagnostic (StreamIdDiscovered)
    var diagnostics = result.Diagnostics;
    var info = diagnostics.FirstOrDefault(d => d.Id == "WHIZ010");
    await Assert.That(info).IsNotNull();
    await Assert.That(info!.Severity).IsEqualTo(DiagnosticSeverity.Info);
    await Assert.That(info.GetMessage(CultureInfo.InvariantCulture)).Contains("CreateOrder");
    await Assert.That(info.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithInheritedAttribute_DiscoversPropertyAsync() {
    // Arrange - Test that [StreamId] is inherited
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public abstract record OrderEvent : IEvent {
              [StreamId]
              public Guid OrderId { get; init; }
            }

            public record CreateOrder : OrderEvent {
              public string ProductName { get; init; } = string.Empty;
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should generate extractor for CreateOrder (inherits [StreamId])
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("CreateOrder");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesCodeInCorrectNamespaceAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder : IEvent {
              [StreamId]
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Generated code should be in TestAssembly.Generated namespace
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("namespace TestAssembly.Generated");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesAutoGeneratedHeaderAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record CreateOrder : IEvent {
              [StreamId]
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should have auto-generated header
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("// <auto-generated/>");
    await Assert.That(generatedSource).Contains("#nullable enable");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithTypeInGlobalNamespace_HandlesCorrectlyAsync() {
    // Arrange - Type with no namespace (tests GetSimpleName with no dots)
    var source = """
            using System;
            using Whizbang.Core;

            public record CreateOrder : IEvent {
              [StreamId]
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should generate extractor
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("CreateOrder");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithStruct_SkipsAsync() {
    // Arrange - Struct with [StreamId] (generator only processes class/record)
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public struct CreateOrderStruct : IEvent {
              [StreamId]
              public Guid OrderId { get; set; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Generator only processes class/record declarations
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    // Struct should not have extractor generated
    await Assert.That(generatedSource!).DoesNotContain("CreateOrderStruct");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithDeepInheritanceChain_DiscoversAllLevelsAsync() {
    // Arrange - Tests while loop with multiple iterations (baseType.BaseType traversal)
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record GrandParentEvent : IEvent {
              [StreamId]
              public Guid RootId { get; init; }
            }

            public record ParentEvent : GrandParentEvent {
              public string Data { get; init; } = string.Empty;
            }

            public record ChildEvent : ParentEvent {
              public string MoreData { get; init; } = string.Empty;
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should discover [StreamId] from grandparent
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("ChildEvent");
    await Assert.That(generatedSource).Contains("RootId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StreamIdGenerator_SimpleInheritanceChain_TraversesToSystemObjectAsync() {
    // Arrange - Tests inheritance chain traversal
    var source = """
            using Whizbang.Core;

            namespace TestNamespace {
              public record BaseOrder : IEvent {
                [StreamId]
                public System.Guid Id { get; init; }
              }

              public record ChildOrder : BaseOrder {
                public string CustomerName { get; init; } = "";
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should generate extractors for both BaseOrder and ChildOrder (inherits [StreamId])
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("BaseOrder");
    await Assert.That(generatedSource!).Contains("ChildOrder");

    // Should find Id property via inheritance chain traversal using WHIZ010
    var diagnostics = result.Diagnostics.Where(d => d.Id == "WHIZ010").ToArray();
    await Assert.That(diagnostics).Count().IsEqualTo(2); // Both BaseOrder and ChildOrder
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StreamIdGenerator_ClassWithNullableGuid_GeneratesExtractorAsync() {
    // Arrange - Tests class with nullable Guid
    var source = """
            using Whizbang.Core;

            namespace TestNamespace {
              public class OrderEvent : IEvent {
                [StreamId]
                public System.Guid? OrderId { get; set; }
                public string CustomerName { get; set; } = "";
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should generate extractor for class with nullable Guid
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("OrderEvent");
    await Assert.That(generatedSource).Contains("OrderId");

    // Should report as discovered with WHIZ010
    var diagnostics = result.Diagnostics.Where(d => d.Id == "WHIZ010").ToArray();
    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StreamIdGenerator_ClassEvent_GeneratesExtractorAsync() {
    // Arrange - Tests class (not record) event
    var source = """
            using Whizbang.Core;

            namespace TestNamespace {
              public class SimpleEvent : IEvent {
                [StreamId]
                public System.Guid Id { get; init; }
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // Assert - Should generate extractor for class event
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("SimpleEvent");

    // Should report as discovered with WHIZ010
    var diagnostics = result.Diagnostics.Where(d => d.Id == "WHIZ010").ToArray();
    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }
}
