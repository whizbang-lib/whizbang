using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="VectorFieldPackageReferenceAnalyzer"/> paths the existing
/// <c>VectorFieldPackageReferenceAnalyzerTests</c> never exercise: an open generic type
/// parameter standing in for TModel, an abstract perspective class (which cannot be
/// instantiated so its vector fields must not count), a static property that happens to
/// carry <c>[VectorField]</c>, and a property whose only attribute is unrelated to
/// <c>[VectorField]</c>.
/// </summary>
[Category("Unit")]
[Category("Analyzers")]
[Category("Shard2")]
public class VectorFieldPackageReferenceAnalyzerCoverageTests {
  #region Open Generic Type Parameter As TModel - Guarded, Not Crashed

  /// <summary>
  /// Verifies that a generic perspective class whose model is its own open type parameter
  /// (so the first type argument of <c>IPerspectiveFor&lt;TState, TestEvent&gt;</c> is an
  /// <c>ITypeParameterSymbol</c>, not an <c>INamedTypeSymbol</c>) is skipped rather than
  /// crashing the analyzer, and that a genuine <c>[VectorField]</c> elsewhere in the same
  /// compilation still drives the missing-package diagnostics. If this guard regressed to
  /// an unconditional cast, a consumer who writes a generic base perspective class would
  /// see the analyzer throw (AD0001) for every symbol pass, on top of - or instead of - the
  /// real WHIZ070/WHIZ071 findings.
  /// </summary>
  [Test]
  public async Task GenericPerspective_WithOpenTypeParameterModel_SkippedWithoutCrashingAsync() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }

                    [VectorField(1536)]
                    public float[]? Embedding { get; set; }
                }

                public record TestEvent(Guid Id);

                // TState is an open type parameter here - the first type argument of
                // IPerspectiveFor<TState, TestEvent> is an ITypeParameterSymbol, not an
                // INamedTypeSymbol, and must be skipped rather than crash the analyzer.
                public class GenericPerspective<TState> : IPerspectiveFor<TState, TestEvent> where TState : class {
                    public TState Apply(TState currentData, TestEvent eventData) => currentData;
                }

                public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act - both packages missing, so the real TestModel.Embedding field should still drive both diagnostics
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<VectorFieldPackageReferenceAnalyzer>(
        source,
        includePgvector: false,
        includePgvectorEfCore: false);

    // Assert - exactly the two real findings, proving the generic class was skipped cleanly
    await Assert.That(diagnostics).Count().IsEqualTo(2)
        .Because("the generic perspective's unresolved model must be skipped, leaving only the two real package findings from TestModel");
    var ids = diagnostics.Select(d => d.Id).Order().ToList();
    await Assert.That(ids).Contains("WHIZ070");
    await Assert.That(ids).Contains("WHIZ071");
  }

  #endregion

  #region Abstract Perspective Class - Vector Field Never Counted

  /// <summary>
  /// Verifies that a <c>[VectorField]</c> reached only through an abstract perspective class
  /// never counts toward the missing-package check, mirroring the abstract-class skip
  /// already covered for the polymorphic and dictionary analyzers. If this guard
  /// regressed, an abstract base class - which can never actually be instantiated as a
  /// perspective - would force every consumer to add the Pgvector packages even when no
  /// concrete perspective ever uses a vector field.
  /// </summary>
  [Test]
  public async Task AbstractPerspectiveClass_WithVectorField_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }

                    [VectorField(1536)]
                    public float[]? Embedding { get; set; }
                }

                public record TestEvent(Guid Id);

                // Abstract perspective classes can't be instantiated, so their model's
                // [VectorField] properties must never be counted toward the package check.
                public abstract class AbstractPerspective : IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act - both packages missing; if the abstract class's model were (wrongly) inspected,
    // this would report WHIZ070 and WHIZ071
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<VectorFieldPackageReferenceAnalyzer>(
        source,
        includePgvector: false,
        includePgvectorEfCore: false);

    // Assert
    await Assert.That(diagnostics).IsEmpty().Because("an abstract perspective class can never be instantiated, so its model's vector field must not require the packages");
  }

  #endregion

  #region Static Property - Never A Real Vector Column

  /// <summary>
  /// Verifies that a <c>static</c> property carrying <c>[VectorField]</c> is not treated as
  /// a real per-row vector column. If this guard regressed, a static field or constant
  /// happening to carry <c>[VectorField]</c> would force every consumer to add the Pgvector
  /// packages for a property that EF Core never persists per row in the first place.
  /// </summary>
  [Test]
  public async Task StaticPropertyWithVectorField_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }

                    [VectorField(1536)]
                    public static float[]? StaticEmbedding { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act - both packages missing; a static [VectorField] must not trip the check
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<VectorFieldPackageReferenceAnalyzer>(
        source,
        includePgvector: false,
        includePgvectorEfCore: false);

    // Assert
    await Assert.That(diagnostics).IsEmpty().Because("a static property is never a per-row column, so [VectorField] on one must not require the packages");
  }

  #endregion

  #region Property With An Unrelated Attribute - Not Mistaken For [VectorField]

  /// <summary>
  /// Verifies that a property whose only attribute is unrelated to <c>[VectorField]</c> is
  /// correctly not counted as a vector field, exercising the attribute scan running to
  /// completion without a match (as opposed to a property with no attributes at all). If
  /// this loop regressed to match on the first attribute present regardless of identity,
  /// any annotated property (e.g. <c>[Obsolete]</c>) would falsely require the Pgvector
  /// packages.
  /// </summary>
  [Test]
  public async Task PropertyWithUnrelatedAttribute_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }

                    [Obsolete("unrelated attribute - not [VectorField]")]
                    public string Description { get; set; } = string.Empty;
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act - both packages missing; an unrelated attribute must not trip the check
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<VectorFieldPackageReferenceAnalyzer>(
        source,
        includePgvector: false,
        includePgvectorEfCore: false);

    // Assert
    await Assert.That(diagnostics).IsEmpty().Because("an attribute other than [VectorField] must not be mistaken for it, however the property is otherwise decorated");
  }

  #endregion
}
