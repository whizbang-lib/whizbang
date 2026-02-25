using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <see cref="VectorFieldPackageReferenceAnalyzer"/>.
/// Verifies that missing Pgvector packages are detected when [VectorField] is used.
/// </summary>
/// <docs>diagnostics/WHIZ070</docs>
/// <docs>diagnostics/WHIZ071</docs>
[Category("Unit")]
public class VectorFieldPackageReferenceAnalyzerTests {
  /// <summary>
  /// Verifies that no diagnostic is reported when both packages are referenced.
  /// </summary>
  [Test]
  public async Task VectorField_WithBothPackages_NoDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }

                    [VectorField(1536)]
                    public float[]? Embedding { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act - with both packages referenced
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<VectorFieldPackageReferenceAnalyzer>(
        source,
        includePgvector: true,
        includePgvectorEfCore: true);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that WHIZ070 is reported when Pgvector.EntityFrameworkCore is missing.
  /// </summary>
  [Test]
  public async Task VectorField_MissingPgvectorEFCore_ReportsWHIZ070Async() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }

                    [VectorField(1536)]
                    public float[]? Embedding { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act - with Pgvector but not Pgvector.EntityFrameworkCore
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<VectorFieldPackageReferenceAnalyzer>(
        source,
        includePgvector: true,
        includePgvectorEfCore: false);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ070");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Pgvector.EntityFrameworkCore");
  }

  /// <summary>
  /// Verifies that WHIZ071 is reported when base Pgvector package is missing.
  /// </summary>
  [Test]
  public async Task VectorField_MissingPgvector_ReportsWHIZ071Async() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }

                    [VectorField(1536)]
                    public float[]? Embedding { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act - with Pgvector.EntityFrameworkCore but not base Pgvector
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<VectorFieldPackageReferenceAnalyzer>(
        source,
        includePgvector: false,
        includePgvectorEfCore: true);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ071");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Pgvector");
  }

  /// <summary>
  /// Verifies that both WHIZ070 and WHIZ071 are reported when both packages are missing.
  /// </summary>
  [Test]
  public async Task VectorField_MissingBothPackages_ReportsBothDiagnosticsAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }

                    [VectorField(1536)]
                    public float[]? Embedding { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act - with neither package referenced
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<VectorFieldPackageReferenceAnalyzer>(
        source,
        includePgvector: false,
        includePgvectorEfCore: false);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(2);
    var ids = diagnostics.Select(d => d.Id).OrderBy(id => id).ToList();
    await Assert.That(ids).Contains("WHIZ070");
    await Assert.That(ids).Contains("WHIZ071");
  }

  /// <summary>
  /// Verifies that no diagnostic is reported when no [VectorField] is used.
  /// </summary>
  [Test]
  public async Task NoVectorField_MissingPackages_NoDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }
                    public string Name { get; set; } = string.Empty;
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act - no vector fields, so packages not required
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<VectorFieldPackageReferenceAnalyzer>(
        source,
        includePgvector: false,
        includePgvectorEfCore: false);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that [SuppressVectorPackageCheck] suppresses the diagnostic.
  /// </summary>
  [Test]
  public async Task VectorField_WithSuppressAttribute_NoDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Perspectives;

            [assembly: SuppressVectorPackageCheck]

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }

                    [VectorField(1536)]
                    public float[]? Embedding { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act - suppress attribute should skip the check
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<VectorFieldPackageReferenceAnalyzer>(
        source,
        includePgvector: false,
        includePgvectorEfCore: false);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that multiple perspectives with vector fields report a single diagnostic per package.
  /// </summary>
  [Test]
  public async Task MultiplePerspectives_WithVectorFields_ReportsOncePerPackageAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
                public class Model1 {
                    public Guid Id { get; set; }
                    [VectorField(1536)]
                    public float[]? Embedding1 { get; set; }
                }

                public class Model2 {
                    public Guid Id { get; set; }
                    [VectorField(768)]
                    public float[]? Embedding2 { get; set; }
                }

                public record Event1(Guid Id);
                public record Event2(Guid Id);

                public class Perspective1 : IPerspectiveFor<Model1, Event1> {
                    public Model1 Apply(Model1? model, Event1 evt) => model ?? new();
                }

                public class Perspective2 : IPerspectiveFor<Model2, Event2> {
                    public Model2 Apply(Model2? model, Event2 evt) => model ?? new();
                }
            }
            """;

    // Act - multiple perspectives but should only report once per missing package
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<VectorFieldPackageReferenceAnalyzer>(
        source,
        includePgvector: false,
        includePgvectorEfCore: false);

    // Assert - should report 2 diagnostics (WHIZ070 + WHIZ071), not 4
    await Assert.That(diagnostics).Count().IsEqualTo(2);
  }

  /// <summary>
  /// Verifies that non-perspective classes with [VectorField] are ignored.
  /// </summary>
  [Test]
  public async Task NonPerspectiveClass_WithVectorField_NoDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
                // Not a perspective, just a regular class
                public class RegularModel {
                    public Guid Id { get; set; }

                    [VectorField(1536)]
                    public float[]? Embedding { get; set; }
                }
            }
            """;

    // Act - not a perspective, so no diagnostic
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<VectorFieldPackageReferenceAnalyzer>(
        source,
        includePgvector: false,
        includePgvectorEfCore: false);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }
}
