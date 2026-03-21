using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for VectorDependencyAnalyzer WHIZ070.
/// Verifies detection of [VectorField] usage without Pgvector.EntityFrameworkCore reference.
/// Organized by test category for 100% line and branch coverage.
/// </summary>
/// <tests>tests/Whizbang.Generators.Tests/VectorDependencyAnalyzerTests.cs</tests>
[Category("Analyzers")]
public class VectorDependencyAnalyzerTests {
  // ========================================
  // Category 1: WHIZ070 Emission Tests
  // ========================================

  /// <summary>
  /// Test 1: [VectorField] without package reference emits WHIZ070.
  /// Covers: Main detection logic
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_VectorFieldWithoutPackage_ReportsWHIZ070Async() {
    // Arrange
    const string source = """
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class EmbeddingDto {
              [VectorField(1536)]
              public float[]? ContentEmbedding { get; init; }
            }
            """;

    // Act
    var diagnostics = await VectorAnalyzerTestHelper.GetDiagnosticsWithoutPgvectorAsync<VectorDependencyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ070")).Count().IsEqualTo(1);
    await Assert.That(diagnostics.First(d => d.Id == "WHIZ070").Severity).IsEqualTo(DiagnosticSeverity.Error);
  }

  /// <summary>
  /// Test 2: Multiple [VectorField] properties without package reference emits WHIZ070 for each.
  /// Covers: Multiple properties detection
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MultipleVectorFieldsWithoutPackage_ReportsMultipleWHIZ070Async() {
    // Arrange
    const string source = """
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class MultiEmbeddingDto {
              [VectorField(1536)]
              public float[]? ContentEmbedding { get; init; }

              [VectorField(768)]
              public float[]? TitleEmbedding { get; init; }
            }
            """;

    // Act
    var diagnostics = await VectorAnalyzerTestHelper.GetDiagnosticsWithoutPgvectorAsync<VectorDependencyAnalyzer>(source);

    // Assert - Should report for each property
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ070")).Count().IsEqualTo(2);
  }

  /// <summary>
  /// Test 3: Diagnostic message contains property name.
  /// Covers: Message format validation
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_VectorFieldDiagnostic_ContainsPropertyNameAsync() {
    // Arrange
    const string source = """
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class EmbeddingDto {
              [VectorField(1536)]
              public float[]? MyEmbeddingProperty { get; init; }
            }
            """;

    // Act
    var diagnostics = await VectorAnalyzerTestHelper.GetDiagnosticsWithoutPgvectorAsync<VectorDependencyAnalyzer>(source);

    // Assert - Message should contain the property name
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ070");
    await Assert.That(diagnostic.GetMessage(null)).Contains("MyEmbeddingProperty");
  }

  /// <summary>
  /// Test 4: Diagnostic message mentions the required package.
  /// Covers: Helpful error message
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_VectorFieldDiagnostic_MentionsPackageNameAsync() {
    // Arrange
    const string source = """
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class EmbeddingDto {
              [VectorField(1536)]
              public float[]? ContentEmbedding { get; init; }
            }
            """;

    // Act
    var diagnostics = await VectorAnalyzerTestHelper.GetDiagnosticsWithoutPgvectorAsync<VectorDependencyAnalyzer>(source);

    // Assert - Message should mention the package
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ070");
    await Assert.That(diagnostic.GetMessage(null)).Contains("Pgvector.EntityFrameworkCore");
  }

  /// <summary>
  /// Test 5: Diagnostic location is on the attribute.
  /// Covers: Location accuracy
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_VectorFieldDiagnostic_LocationIsOnAttributeAsync() {
    // Arrange
    const string source = """
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class EmbeddingDto {
              [VectorField(1536)]
              public float[]? ContentEmbedding { get; init; }
            }
            """;

    // Act
    var diagnostics = await VectorAnalyzerTestHelper.GetDiagnosticsWithoutPgvectorAsync<VectorDependencyAnalyzer>(source);

    // Assert - Location should be on line 6 (the attribute line)
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ070");
    await Assert.That(diagnostic.Location.GetLineSpan().StartLinePosition.Line).IsEqualTo(5); // 0-indexed
  }

  // ========================================
  // Category 2: No Diagnostic Cases
  // ========================================

  /// <summary>
  /// Test 6: [VectorField] WITH package reference emits no diagnostic.
  /// Covers: Package present path
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_VectorFieldWithPackage_NoWHIZ070Async() {
    // Arrange
    const string source = """
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class EmbeddingDto {
              [VectorField(1536)]
              public float[]? ContentEmbedding { get; init; }
            }
            """;

    // Act
    var diagnostics = await VectorAnalyzerTestHelper.GetDiagnosticsWithPgvectorAsync<VectorDependencyAnalyzer>(source);

    // Assert - No WHIZ070 when package is present
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ070")).IsEmpty();
  }

  /// <summary>
  /// Test 7: No [VectorField] attributes emits no diagnostic.
  /// Covers: No vector fields path
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_NoVectorFields_NoWHIZ070Async() {
    // Arrange
    const string source = """
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class RegularDto {
              public string Name { get; init; }
              public int Count { get; init; }
            }
            """;

    // Act
    var diagnostics = await VectorAnalyzerTestHelper.GetDiagnosticsWithoutPgvectorAsync<VectorDependencyAnalyzer>(source);

    // Assert - No WHIZ070 when no vector fields
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ070")).IsEmpty();
  }

  /// <summary>
  /// Test 8: [PhysicalField] (not [VectorField]) emits no diagnostic.
  /// Covers: Only VectorField triggers check
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_PhysicalFieldOnly_NoWHIZ070Async() {
    // Arrange
    const string source = """
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class IndexedDto {
              [PhysicalField]
              public string Name { get; init; }
            }
            """;

    // Act
    var diagnostics = await VectorAnalyzerTestHelper.GetDiagnosticsWithoutPgvectorAsync<VectorDependencyAnalyzer>(source);

    // Assert - PhysicalField doesn't require Pgvector
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ070")).IsEmpty();
  }

  // ========================================
  // Category 3: Edge Cases
  // ========================================

  /// <summary>
  /// Test 9: [VectorField] on private property still emits diagnostic.
  /// Covers: All accessibility levels
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_VectorFieldOnPrivateProperty_ReportsWHIZ070Async() {
    // Arrange
    const string source = """
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class EmbeddingDto {
              [VectorField(1536)]
              private float[]? _embedding { get; init; }
            }
            """;

    // Act
    var diagnostics = await VectorAnalyzerTestHelper.GetDiagnosticsWithoutPgvectorAsync<VectorDependencyAnalyzer>(source);

    // Assert - Private properties also need the package
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ070")).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Test 10: [VectorField] in nested class emits diagnostic.
  /// Covers: Nested type detection
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_VectorFieldInNestedClass_ReportsWHIZ070Async() {
    // Arrange
    const string source = """
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class OuterClass {
              public class InnerEmbeddingDto {
                [VectorField(1536)]
                public float[]? Embedding { get; init; }
              }
            }
            """;

    // Act
    var diagnostics = await VectorAnalyzerTestHelper.GetDiagnosticsWithoutPgvectorAsync<VectorDependencyAnalyzer>(source);

    // Assert - Nested classes are also checked
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ070")).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Test 11: [VectorField] with custom settings still emits diagnostic.
  /// Covers: Attribute with named parameters
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_VectorFieldWithCustomSettings_ReportsWHIZ070Async() {
    // Arrange
    const string source = """
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class EmbeddingDto {
              [VectorField(768, DistanceMetric = VectorDistanceMetric.Cosine, IndexType = VectorIndexType.HNSW)]
              public float[]? Embedding { get; init; }
            }
            """;

    // Act
    var diagnostics = await VectorAnalyzerTestHelper.GetDiagnosticsWithoutPgvectorAsync<VectorDependencyAnalyzer>(source);

    // Assert - Custom settings don't bypass the check
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ070")).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Test 12: [VectorField] on record property emits diagnostic.
  /// Covers: Record type detection
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_VectorFieldOnRecord_ReportsWHIZ070Async() {
    // Arrange
    const string source = """
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public record EmbeddingRecord(
              [property: VectorField(1536)]
              float[]? Embedding
            );
            """;

    // Act
    var diagnostics = await VectorAnalyzerTestHelper.GetDiagnosticsWithoutPgvectorAsync<VectorDependencyAnalyzer>(source);

    // Assert - Records with [property:] attribute syntax are checked
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ070")).Count().IsEqualTo(1);
  }

  // ========================================
  // Category 4: Supported Diagnostics
  // ========================================

  /// <summary>
  /// Test 13: Analyzer supports WHIZ070 diagnostic.
  /// Covers: SupportedDiagnostics property
  /// </summary>
  [Test]
  public async Task Analyzer_SupportsDiagnostic_WHIZ070Async() {
    // Arrange
    var analyzer = new VectorDependencyAnalyzer();

    // Assert
    await Assert.That(analyzer.SupportedDiagnostics).Contains(DiagnosticDescriptors.VectorFieldMissingPackage);
  }
}

/// <summary>
/// Test helper for VectorDependencyAnalyzer that can simulate presence/absence of Pgvector.EntityFrameworkCore.
/// </summary>
public static class VectorAnalyzerTestHelper {
  /// <summary>
  /// Runs analyzer WITHOUT Pgvector.EntityFrameworkCore reference.
  /// </summary>
  [RequiresAssemblyFiles()]
  public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsWithoutPgvectorAsync<TAnalyzer>(string source)
      where TAnalyzer : DiagnosticAnalyzer, new() {
    return await _getDiagnosticsAsync<TAnalyzer>(source, includePgvector: false);
  }

  /// <summary>
  /// Runs analyzer WITH Pgvector.EntityFrameworkCore reference.
  /// Uses a synthetic assembly reference since actual package has version conflicts.
  /// </summary>
  [RequiresAssemblyFiles()]
  public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsWithPgvectorAsync<TAnalyzer>(string source)
      where TAnalyzer : DiagnosticAnalyzer, new() {
    return await _getDiagnosticsAsync<TAnalyzer>(source, includePgvector: true);
  }

  [RequiresAssemblyFiles()]
  private static async Task<ImmutableArray<Diagnostic>> _getDiagnosticsAsync<TAnalyzer>(string source, bool includePgvector)
      where TAnalyzer : DiagnosticAnalyzer, new() {
    // Parse the source code
    var syntaxTree = CSharpSyntaxTree.ParseText(source);

    // Get references to assemblies we need
    var references = new List<MetadataReference>();

    // Add reference to System.Runtime and other basic assemblies
    var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Collections.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.ComponentModel.Primitives.dll")));

    // Add reference to Whizbang.Core (for VectorFieldAttribute)
    try {
      var coreAssembly = System.Reflection.Assembly.Load("Whizbang.Core");
      references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
    } catch {
      // If assembly can't be loaded, try to find it in current directory
      var coreAssemblyPath = Path.Combine(
          Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
          "Whizbang.Core.dll"
      );
      if (File.Exists(coreAssemblyPath)) {
        references.Add(MetadataReference.CreateFromFile(coreAssemblyPath));
      }
    }

    // Optionally add Pgvector.EntityFrameworkCore reference
    // We create a synthetic assembly since actual package has EF Core version conflicts
    if (includePgvector) {
      var pgvectorReference = _createSyntheticPgvectorReference();
      references.Add(pgvectorReference);
    }

    // Create compilation
    var compilation = CSharpCompilation.Create(
        assemblyName: "TestAssembly",
        syntaxTrees: new[] { syntaxTree },
        references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );

    // Create analyzer instance
    var analyzer = new TAnalyzer();

    // Create compilation with analyzers
    var compilationWithAnalyzers = compilation.WithAnalyzers(
        [analyzer]);

    // Get analyzer diagnostics only (exclude compiler diagnostics)
    var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

    return diagnostics;
  }

  /// <summary>
  /// Creates a synthetic assembly reference that mimics Pgvector.EntityFrameworkCore.
  /// This avoids version conflicts with EF Core 10 while allowing us to test
  /// the analyzer's "package present" detection logic.
  /// </summary>
  private static PortableExecutableReference _createSyntheticPgvectorReference() {
    // Create minimal source that compiles to an assembly named "Pgvector.EntityFrameworkCore"
    const string pgvectorSource = """
        namespace Pgvector.EntityFrameworkCore {
            public static class PgvectorDbContextOptionsExtensions { }
        }
        """;

    var syntaxTree = CSharpSyntaxTree.ParseText(pgvectorSource);
    var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    var compilation = CSharpCompilation.Create(
        assemblyName: "Pgvector.EntityFrameworkCore",
        syntaxTrees: new[] { syntaxTree },
        references: new[] {
          MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
          MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll"))
        },
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );

    using var stream = new MemoryStream();
    var result = compilation.Emit(stream);
    if (!result.Success) {
      throw new InvalidOperationException(
          "Failed to create synthetic Pgvector assembly: " +
          string.Join(", ", result.Diagnostics.Select(d => d.GetMessage(null))));
    }

    stream.Position = 0;
    return MetadataReference.CreateFromStream(stream);
  }
}
