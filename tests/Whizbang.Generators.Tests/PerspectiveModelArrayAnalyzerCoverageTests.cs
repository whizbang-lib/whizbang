using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage for <see cref="PerspectiveModelArrayAnalyzer"/> paths the existing
/// <c>PerspectiveModelArrayAnalyzerTests</c> never exercise: the "...Model"-named-but-not-actually-
/// a-perspective heuristic walking its <c>[StreamId]</c> property scan all the way to exhaustion.
/// </summary>
/// <remarks>
/// One of this round's targets in this file is NOT covered here, because it matches the
/// "Roslyn-contract guard" shape already established as unreachable in earlier rounds:
/// <c>_analyzeTypeDeclaration</c>'s <c>typeSymbol is null =&gt; return</c>
/// (PerspectiveModelArrayAnalyzer.cs:60-62). This method only runs for a
/// <c>ClassDeclarationSyntax</c> or <c>RecordDeclarationSyntax</c> node that the analyzer's own
/// <c>RegisterSyntaxNodeAction</c> just matched syntactically — <c>GetDeclaredSymbol</c> always
/// resolves a real <see cref="INamedTypeSymbol"/> for a type declaration node that is actually
/// part of the tree being analyzed. No source was found (or could be constructed) that reaches
/// this branch.
/// </remarks>
/// <tests>Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs</tests>
[Category("Analyzers")]
public class PerspectiveModelArrayAnalyzerCoverageTests {
  /// <summary>
  /// "...Model" is an extremely common suffix for ordinary DTOs and view-models that have
  /// nothing to do with Whizbang perspectives (OrderSummaryModel, ResponseModel, and similar
  /// shapes appear throughout typical consumer codebases). The naming-convention heuristic
  /// (PerspectiveModelArrayAnalyzer.cs:97-114) must walk every property's full attribute list to
  /// completion and correctly conclude "not a perspective model" when none of them carries
  /// [StreamId], rather than short-circuiting early or defaulting to true. If this scan
  /// regressed, ordinary "...Model" classes that merely happen to have an unrelated attribute on
  /// a property would start being misclassified as perspective models, spamming WHIZ200 warnings
  /// on arrays this rule was never meant to flag — noise that would train developers to ignore
  /// the warning altogether.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ModelSuffixedClassWithNoStreamIdProperty_NoWarningAsync() {
    // Arrange - no stubs needed: no [Perspective] or [StreamId] appears anywhere, so this
    // exercises the naming-convention heuristic's "not a match" exhaustion path directly.
    const string source = """
        using System;

        namespace TestApp {
          public class OrderSummaryModel {
            [Obsolete("legacy")]
            public string Description { get; set; }

            public string[] Tags { get; set; }
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ200")).IsEmpty()
      .Because("OrderSummaryModel has no [Perspective] attribute and no [StreamId] property, so the naming-convention heuristic must walk every property's attributes to exhaustion and conclude it is not a perspective model, leaving the array property Tags unreported");
  }
}
