using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for AggregateIdGenerator - ensures zero-reflection aggregate ID extraction.
/// Following TDD: These tests are written BEFORE the generator implementation.
/// All tests should FAIL initially (RED phase), then pass after implementation (GREEN phase).
/// </summary>
public class AggregateIdGeneratorTests {
  [Test]
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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

    // Verify the extractor signature (internal - wrapped by public DI implementation)
    await Assert.That(generatedSource!).Contains("internal static Guid? ExtractAggregateId(object message, Type messageType)");
  }

  [Test]
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
    await Assert.That(info.GetMessage(CultureInfo.InvariantCulture)).Contains("CreateOrder");
    await Assert.That(info.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderId");
  }

  [Test]
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithTypeInGlobalNamespace_HandlesCorrectlyAsync() {
    // Arrange - Type with no namespace (tests GetSimpleName with no dots)
    var source = """
            using System;
            using Whizbang.Core;

            public record CreateOrder {
              [AggregateId]
              public Guid OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should generate extractor
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("CreateOrder");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithStruct_SkipsAsync() {
    // Arrange - Struct with [AggregateId] (tests IsTypeWithAttributes return false path)
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public struct CreateOrderStruct {
              [AggregateId]
              public Guid OrderId { get; set; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should not generate extractor for struct
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("return null"); // Empty registry
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithInterface_SkipsAsync() {
    // Arrange - Interface (tests IsTypeWithAttributes return false path)
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public interface ICommand {
              [AggregateId]
              Guid OrderId { get; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should not generate extractor for interface
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("return null"); // Empty registry
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithDeepInheritanceChain_DiscoversAllLevelsAsync() {
    // Arrange - Tests while loop with multiple iterations (baseType.BaseType traversal)
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record GrandParentCommand {
              [AggregateId]
              public Guid RootId { get; init; }
            }

            public record ParentCommand : GrandParentCommand {
              public string Data { get; init; } = string.Empty;
            }

            public record ChildCommand : ParentCommand, ICommand {
              public string MoreData { get; init; } = string.Empty;
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should discover [AggregateId] from grandparent
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("ChildCommand");
    await Assert.That(generatedSource).Contains("RootId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithStringProperty_ReportsInvalidTypeAsync() {
    // Arrange - Tests hasInvalidType branch when property is not Guid or Guid?
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record InvalidCommand : ICommand {
              [AggregateId]
              public string OrderId { get; init; } = string.Empty;
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should report invalid type diagnostic
    var diagnostics = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
    var invalidTypeDiagnostic = diagnostics.FirstOrDefault(d => d.Id == "WHIZ005");
    await Assert.That(invalidTypeDiagnostic).IsNotNull();
    await Assert.That(invalidTypeDiagnostic!.GetMessage(CultureInfo.InvariantCulture)).Contains("must be of type Guid or Guid?");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithIntProperty_ReportsInvalidTypeAsync() {
    // Arrange - Tests hasInvalidType branch with different non-Guid type
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record InvalidCommand : ICommand {
              [AggregateId]
              public int OrderId { get; init; }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should report invalid type diagnostic
    var diagnostics = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
    var invalidTypeDiagnostic = diagnostics.FirstOrDefault(d => d.Id == "WHIZ005");
    await Assert.That(invalidTypeDiagnostic).IsNotNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AggregateIdGenerator_SimpleInheritanceChain_TraversesToSystemObjectAsync() {
    // Arrange - Tests line 74-81: while loop termination at baseType.SpecialType == SpecialType.System_Object
    var source = """
            using Whizbang.Core;

            namespace TestNamespace {
              public record BaseOrder {
                [AggregateId]
                public System.Guid Id { get; init; }
              }

              public record ChildOrder : BaseOrder {
                public string CustomerName { get; init; } = "";
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should generate extractors for both BaseOrder and ChildOrder (inherits [AggregateId])
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("BaseOrder");
    await Assert.That(generatedSource!).Contains("ChildOrder");

    // Should find Id property via inheritance chain traversal, terminating at System.Object
    var diagnostics = result.Diagnostics.Where(d => d.Id == "WHIZ004").ToArray();
    await Assert.That(diagnostics).Count().IsEqualTo(2); // Both BaseOrder and ChildOrder
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AggregateIdGenerator_MultipleAggregateIdsInInheritanceChain_ReportsWarningAsync() {
    // Arrange - Tests line 74-81: Multiple [AggregateId] attributes across inheritance chain
    var source = """
            using Whizbang.Core;

            namespace TestNamespace {
              public record GrandParent {
                [AggregateId]
                public System.Guid GrandParentId { get; init; }
              }

              public record Parent : GrandParent {
                [AggregateId]  // This should trigger warning - multiple aggregate IDs
                public System.Guid ParentId { get; init; }
              }

              public record Child : Parent {
                public string Name { get; init; } = "";
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should report warning for multiple [AggregateId] attributes
    var diagnostics = result.Diagnostics.Where(d => d.Id == "WHIZ006").ToArray();
    await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(2); // Parent and Child both have 2 aggregate IDs
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AggregateIdGenerator_ClassWithNullableGuid_GeneratesExtractorAsync() {
    // Arrange - Tests line 43 (ClassDeclarationSyntax), line 61 (class branch), line 95 (nullable Guid)
    var source = """
            using Whizbang.Core;

            namespace TestNamespace {
              public class OrderCommand {
                [AggregateId]
                public System.Guid? OrderId { get; set; }
                public string CustomerName { get; set; } = "";
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should generate extractor for class with nullable Guid
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("OrderCommand");
    await Assert.That(generatedSource).Contains("OrderId");

    // Should report as discovered
    var diagnostics = result.Diagnostics.Where(d => d.Id == "WHIZ004").ToArray();
    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AggregateIdGenerator_ClassWithNoBaseType_GeneratesExtractorAsync() {
    // Arrange - Tests line 75 (while loop when baseType == null for value type bases)
    var source = """
            using Whizbang.Core;

            namespace TestNamespace {
              public class SimpleCommand {
                [AggregateId]
                public System.Guid Id { get; init; }
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<AggregateIdGenerator>(source);

    // Assert - Should generate extractor even with minimal inheritance
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "AggregateIdExtractors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("SimpleCommand");

    // Should report as discovered
    var diagnostics = result.Diagnostics.Where(d => d.Id == "WHIZ004").ToArray();
    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }
}
