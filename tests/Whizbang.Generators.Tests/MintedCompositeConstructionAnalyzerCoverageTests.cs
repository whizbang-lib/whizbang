using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="MintedCompositeConstructionAnalyzer"/> (WHIZ150),
/// complementing <c>tests/Whizbang.Generators.Tests/Analyzers/MintedCompositeConstructionAnalyzerTests.cs</c>.
/// These target the "constructing code lives in the Whizbang.Core assembly" exemption (which needs a
/// compilation actually named <c>Whizbang.Core</c> — <see cref="AnalyzerTestHelper"/> hardcodes
/// <c>"TestAssembly"</c>, so a local <see cref="CSharpCompilation"/> is built here instead, per the
/// pattern established in an earlier coverage round) and the "helper lambda nested inside a builder
/// lambda" walk described in <c>_isInsideRegisteredBuilder</c>'s doc comment.
/// </summary>
/// <remarks>
/// Two of the round's targets in this file are NOT covered here, because they match the
/// "Roslyn-contract guard" shape already established as unreachable in earlier rounds:
/// <list type="bullet">
/// <item><c>_isInMintingNamespace</c>'s <c>ns is null =&gt; return false</c> (line 115). Every symbol's
/// <c>ContainingNamespace</c> is a real namespace symbol (the global namespace at minimum) — it is
/// never actually null for a symbol reached via <c>OperationAnalysisContext.ContainingSymbol</c>. This
/// is the same "<c>ContainingNamespace == null</c>" pattern already catalogued as unreachable.</item>
/// <item><c>_isBuilderSeam</c>'s <c>containingType is null =&gt; return false</c> (line 155). A
/// resolved <see cref="IPropertyReferenceOperation"/>'s <c>Property.ContainingType</c> is never null
/// for an actual C# property — a property cannot be declared outside a containing type, and an
/// unresolved/dynamic member access would not produce an <see cref="IPropertyReferenceOperation"/> in
/// the first place. No source was found that reaches this branch.</item>
/// </list>
/// </remarks>
public class MintedCompositeConstructionAnalyzerCoverageTests {
  // Stand-in CompositeEventBase declared in-source (the analyzer matches it by fully-qualified
  // display-string comparison, not by referencing the real Whizbang.Core assembly), so a compilation
  // with a custom assembly name needs no project reference at all.
  private const string EXEMPTION_TWO_SOURCE = """
    namespace Whizbang.Core.Minting {
      public abstract class CompositeEventBase { }
    }

    namespace Whizbang.Core.Messaging {
      public sealed class RedeliveryComposite : Whizbang.Core.Minting.CompositeEventBase { }

      public static class DefaultRedeliveryFactory {
        public static object Make() => new RedeliveryComposite();
      }
    }
    """;

  private static async Task<ImmutableArray<Diagnostic>> _diagnosticsForAssemblyAsync(string assemblyName, string source) {
    var syntaxTree = CSharpSyntaxTree.ParseText(source);
    var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    var references = new List<MetadataReference> {
      MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
      MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
    };
    var compilation = CSharpCompilation.Create(
      assemblyName: assemblyName,
      syntaxTrees: [syntaxTree],
      references: references,
      options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    var analyzer = new MintedCompositeConstructionAnalyzer();
    var withAnalyzers = compilation.WithAnalyzers([analyzer]);
    return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
  }

  // Code constructing a minted composite whose containing ASSEMBLY is literally "Whizbang.Core" is
  // exempt (MintedCompositeConstructionAnalyzer.cs:85-86) even outside the Minting namespace itself —
  // the framework's own registered producers live throughout Whizbang.Core, not only inside
  // Whizbang.Core.Minting. If this exemption regressed, the framework's own default factories would
  // trip its own analyzer.
  [Test]
  public async Task ConstructionInsideCoreAssembly_OutsideMintingNamespace_IsSilentAsync() {
    var diagnostics = await _diagnosticsForAssemblyAsync("Whizbang.Core", EXEMPTION_TWO_SOURCE);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ150")).IsEmpty()
      .Because("the framework's own registered producers inside the Whizbang.Core assembly ARE the sanctioned factory path, even outside the Minting namespace itself");
  }

  // The same shape compiled under a DIFFERENT assembly name must still be flagged — proving the
  // exemption is scoped to the Whizbang.Core assembly specifically, not to "outside the Minting
  // namespace" generally.
  [Test]
  public async Task ConstructionInsideADifferentAssembly_OutsideMintingNamespace_ReportsWhiz150Async() {
    var diagnostics = await _diagnosticsForAssemblyAsync("ConsumerApp", EXEMPTION_TWO_SOURCE);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ150").Count()).IsEqualTo(1)
      .Because("only the Whizbang.Core assembly itself gets this exemption — a consumer assembly with the identical shape must still be flagged");
  }

  // A helper lambda declared and invoked INSIDE a BuildComposite builder lambda still counts as
  // "inside the registered builder" (MintedCompositeConstructionAnalyzer.cs:130-147): the outer walk
  // must climb past the helper lambda's own (non-matching) ancestry — hitting the "stop climbing"
  // break — and keep searching outer enclosing anonymous functions until it finds the BuildComposite
  // assignment. If this walk only checked the immediate enclosing lambda, a common refactor
  // (extracting composite construction into a local function/lambda for readability) would start
  // triggering WHIZ150 inside code that is still entirely within the sanctioned factory seam.
  [Test]
  public async Task HelperLambdaNestedInsideBuilderLambda_StaysExemptAsync() {
    const string source = """
      using System;
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Minting;

      namespace ConsumerApp;

      public sealed class DigestComposite : CompositeEventBase;

      public class DigestShipper {
        public object Plan(ICompositeFactory factory, System.Collections.Generic.List<IMessage> events) {
          return factory.Create(new CompositeMintRequest<IMessage> {
            Constituents = events,
            GroupKey = CompositeGroupKey.FromKey<IMessage>(_ => "digest"),
            BuildComposite = batch => {
              Func<DigestComposite> makeComposite = () => new DigestComposite { Inner = [.. batch.Constituents] };
              return makeComposite();
            },
          });
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MintedCompositeConstructionAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ150")).IsEmpty()
      .Because("a helper lambda nested inside a registered BuildComposite builder is still lexically inside the sanctioned factory seam");
  }
}
