using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for PerspectiveModelArrayAnalyzer WHIZ200.
/// Verifies detection of array properties in perspective models.
/// </summary>
[Category("Analyzers")]
public class PerspectiveModelArrayAnalyzerTests {
  // Stub attributes for test compilation - placed between usings and test code
  private const string STUB_ATTRIBUTES = """

    namespace Whizbang.Core.Perspectives {
      [System.AttributeUsage(System.AttributeTargets.Class)]
      public sealed class PerspectiveAttribute : System.Attribute { }

      [System.AttributeUsage(System.AttributeTargets.Property)]
      public sealed class StreamIdAttribute : System.Attribute { }
    }

    namespace Whizbang.Core.Lenses {
      [System.AttributeUsage(System.AttributeTargets.Property)]
      public sealed class VectorFieldAttribute : System.Attribute {
        public VectorFieldAttribute(int dimensions) { }
      }
    }

    """;

  // Helper to create source with correct order: usings, stubs, test code
  private static string _createSource(string usings, string code) =>
      usings + STUB_ATTRIBUTES + code;

  // ========================================
  // WHIZ200: Array Property Detection Tests
  // ========================================

  /// <summary>
  /// Test that array property in [Perspective] model is detected and reports WHIZ200 warning.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ArrayInPerspectiveAttribute_ReportsWHIZ200WarningAsync() {
    // Arrange
    var source = _createSource(
        """
        using System;
        using Whizbang.Core.Perspectives;
        """,
        """
        namespace TestApp {
          [Perspective]
          public class OrderModel {
            public Guid Id { get; set; }
            public string[] Tags { get; set; }
          }
        }
        """);

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ200")).Count().IsEqualTo(1);
    await Assert.That(diagnostics.First(d => d.Id == "WHIZ200").Severity).IsEqualTo(DiagnosticSeverity.Warning);
  }

  /// <summary>
  /// Test that array property in model with [StreamId] is detected and reports WHIZ200 warning.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ArrayInStreamIdModel_ReportsWHIZ200WarningAsync() {
    // Arrange
    var source = _createSource(
        """
        using System;
        using Whizbang.Core.Perspectives;
        """,
        """
        namespace TestApp {
          public class CustomerModel {
            [StreamId]
            public Guid Id { get; set; }
            public int[] OrderIds { get; set; }
          }
        }
        """);

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ200")).Count().IsEqualTo(1);
  }

  // ========================================
  // VectorField Exclusion Tests
  // ========================================

  /// <summary>
  /// Test that [VectorField] float[] property does NOT trigger WHIZ200.
  /// Vector embeddings are valid float[] properties.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_VectorFieldFloatArray_NoWarningAsync() {
    // Arrange
    var source = _createSource(
        """
        using System;
        using Whizbang.Core.Perspectives;
        using Whizbang.Core.Lenses;
        """,
        """
        namespace TestApp {
          [Perspective]
          public class DocumentModel {
            public Guid Id { get; set; }
            public string Title { get; set; }

            [VectorField(1536)]
            public float[] Embeddings { get; set; }
          }
        }
        """);

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(source);

    // Assert - No WHIZ200 warnings (VectorField is excluded)
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ200")).IsEmpty();
  }

  /// <summary>
  /// Test that model with both VectorField and regular array reports only for the regular array.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MixedVectorAndRegularArray_ReportsOnlyRegularArrayAsync() {
    // Arrange
    var source = _createSource(
        """
        using System;
        using Whizbang.Core.Perspectives;
        using Whizbang.Core.Lenses;
        """,
        """
        namespace TestApp {
          [Perspective]
          public class ChatMessageModel {
            public Guid Id { get; set; }
            public string[] Tags { get; set; }

            [VectorField(1536)]
            public float[] Embeddings { get; set; }
          }
        }
        """);

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(source);

    // Assert - Only one WHIZ200 for Tags, not for Embeddings
    var whiz200Diagnostics = diagnostics.Where(d => d.Id == "WHIZ200").ToArray();
    await Assert.That(whiz200Diagnostics).Count().IsEqualTo(1);

    var diagnostic = whiz200Diagnostics[0];
    var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("Tags");
    await Assert.That(message).DoesNotContain("Embeddings");
  }

  // ========================================
  // Negative Tests - No Warning Expected
  // ========================================

  /// <summary>
  /// Test that List&lt;T&gt; property does not trigger WHIZ200.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ListProperty_NoWarningAsync() {
    // Arrange
    var source = _createSource(
        """
        using System;
        using System.Collections.Generic;
        using Whizbang.Core.Perspectives;
        """,
        """
        namespace TestApp {
          [Perspective]
          public class ProductModel {
            public Guid Id { get; set; }
            public List<string> Tags { get; set; }
          }
        }
        """);

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(source);

    // Assert - No WHIZ200 warnings for List<T>
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ200")).IsEmpty();
  }

  /// <summary>
  /// Test that regular class without perspective markers does not trigger WHIZ200.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_RegularClassWithArray_NoWarningAsync() {
    // Arrange - no stubs needed since we're testing non-perspective classes
    var source = """
        using System;

        namespace TestApp {
          public class RegularClass {
            public Guid Id { get; set; }
            public string[] Items { get; set; }
          }
        }
        """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(source);

    // Assert - No WHIZ200 warnings for non-perspective classes
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ200")).IsEmpty();
  }

  /// <summary>
  /// Test that record with array but no perspective markers does not trigger WHIZ200.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_RegularRecordWithArray_NoWarningAsync() {
    // Arrange - no stubs needed since we're testing non-perspective records
    var source = """
        using System;

        namespace TestApp {
          public record DataTransferObject(
            Guid Id,
            string[] Values
          );
        }
        """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(source);

    // Assert - No WHIZ200 warnings for non-perspective records
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ200")).IsEmpty();
  }

  // ========================================
  // Multiple Arrays Tests
  // ========================================

  /// <summary>
  /// Test that multiple array properties each report separate WHIZ200 warnings.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MultipleArrays_ReportsMultipleWarningsAsync() {
    // Arrange
    var source = _createSource(
        """
        using System;
        using Whizbang.Core.Perspectives;
        """,
        """
        namespace TestApp {
          [Perspective]
          public class InventoryModel {
            public Guid Id { get; set; }
            public string[] Categories { get; set; }
            public int[] Quantities { get; set; }
            public decimal[] Prices { get; set; }
          }
        }
        """);

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(source);

    // Assert - Three WHIZ200 warnings for three arrays
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ200")).Count().IsEqualTo(3);
  }

  // ========================================
  // Record Perspective Model Tests
  // ========================================

  /// <summary>
  /// Test that record perspective model with array triggers WHIZ200.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_RecordPerspectiveWithArray_ReportsWHIZ200WarningAsync() {
    // Arrange
    var source = _createSource(
        """
        using System;
        using Whizbang.Core.Perspectives;
        """,
        """
        namespace TestApp {
          [Perspective]
          public record UserModel {
            public Guid Id { get; init; }
            public string[] Roles { get; init; }
          }
        }
        """);

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ200")).Count().IsEqualTo(1);
  }

  // ========================================
  // Suppression Tests
  // ========================================

  /// <summary>
  /// Test that analyzer can be suppressed with pragma.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_WithPragmaSuppress_NoVisibleWarningAsync() {
    // Arrange
    var source = _createSource(
        """
        using System;
        using Whizbang.Core.Perspectives;
        """,
        """
        namespace TestApp {
          [Perspective]
          public class SuppressedModel {
            public Guid Id { get; set; }

          #pragma warning disable WHIZ200
            public string[] Tags { get; set; }
          #pragma warning restore WHIZ200
          }
        }
        """);

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(source);

    // Assert - No visible WHIZ200 errors (suppressed)
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ200" && !d.IsSuppressed)).IsEmpty();
  }

  // ========================================
  // Message Format Tests
  // ========================================

  /// <summary>
  /// Test that diagnostic message includes property name, type name, and element type.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_DiagnosticMessage_ContainsCorrectInformationAsync() {
    // Arrange
    var source = _createSource(
        """
        using System;
        using Whizbang.Core.Perspectives;
        """,
        """
        namespace TestApp {
          [Perspective]
          public class TestModel {
            public Guid Id { get; set; }
            public string[] Items { get; set; }
          }
        }
        """);

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(source);

    // Assert
    var diagnostic = diagnostics.FirstOrDefault(d => d.Id == "WHIZ200");
    await Assert.That(diagnostic).IsNotNull();

    var message = diagnostic!.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("Items");      // Property name
    await Assert.That(message).Contains("TestModel");  // Type name
    await Assert.That(message).Contains("string[]");   // Array type
    await Assert.That(message).Contains("List<string>"); // Suggested fix
  }
}
