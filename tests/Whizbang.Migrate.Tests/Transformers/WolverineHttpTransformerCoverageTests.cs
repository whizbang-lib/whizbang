using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Coverage tests for <see cref="WolverineHttpTransformer"/> paths the primary test suite doesn't
/// reach: completing the attribute-detection scan past a non-matching attribute, recognizing a
/// Wolverine HTTP attribute written with a fully-qualified name, an alias-qualified attribute name
/// the recognizer does not unwrap, and the route extractor's fallback for a non-literal argument
/// expression.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/WolverineHttpTransformer.cs:*</tests>
public class WolverineHttpTransformerCoverageTests {

  // If the attribute-detection loop inside _hasWolverineHttpPatterns doesn't run to completion
  // past a non-matching attribute, a file whose only attribute happens not to be Wolverine HTTP
  // could, on a subtly different fixture, mask a real signal elsewhere in the same scan -- the
  // file would keep an unresolvable Wolverine reference with no warning that anything was missed.
  [Test]
  public async Task TransformAsync_NonWolverineAttributeAndNoUsing_CompletesTheScanAndLeavesFileUnchangedAsync() {
    var transformer = new WolverineHttpTransformer();
    const string source = """
      using System;

      public class OrderService {
        [Obsolete("legacy")]
        public string Get(string id) => id;
      }
      """;

    var result = await transformer.TransformAsync(source, "OrderService.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(source)
      .Because("an attribute that is not Wolverine HTTP, with no Wolverine using either, must not trigger a rewrite");
    await Assert.That(result.Changes).IsEmpty();
    await Assert.That(result.Warnings).IsEmpty();
  }

  // The name extractor's QualifiedNameSyntax arm strips a namespace-qualified attribute reference
  // down to its bare name before matching it against the known Wolverine HTTP attributes. If that
  // arm regressed, an endpoint referencing the attribute by its fully-qualified name would sail
  // through undetected -- the attribute would survive as dead code once the Wolverine package is
  // removed, breaking the build with no warning pointing at why.
  [Test]
  public async Task TransformAsync_FullyQualifiedAttributeName_IsRecognizedAndRemovedAsync() {
    var transformer = new WolverineHttpTransformer();
    const string source = """
      using Wolverine.Http;

      public class Endpoints {
        [Wolverine.Http.WolverineGet("/y")]
        public string Handle() => "x";
      }
      """;

    var result = await transformer.TransformAsync(source, "Endpoints.cs");

    await Assert.That(result.TransformedCode).DoesNotContain("WolverineGet")
      .Because("the qualified attribute must be recognized and removed just like its bare-name form");
    await Assert.That(result.TransformedCode).Contains("GET(\"/y\")");
    var warning = result.Warnings.FirstOrDefault(w => w.Contains("MANUAL CONVERSION", StringComparison.Ordinal));
    await Assert.That(warning).IsNotNull()
      .Because("a recognized-but-unconvertible route still needs the manual-conversion trail");
  }

  // Everything the recognizer doesn't specifically unwrap falls through _getAttributeName's final
  // arm, which returns the raw text of the name node. An alias-qualified name never matches a
  // plain attribute name in the known set, so this exercises the one case where the transformer's
  // detection genuinely misses a real Wolverine attribute -- documented here as a
  // survives-unchanged outcome, not a bug this round is fixing.
  [Test]
  public async Task TransformAsync_AliasQualifiedAttributeName_IsNotRecognizedAndSurvivesAsync() {
    var transformer = new WolverineHttpTransformer();
    const string source = """
      using Wolverine.Http;

      public class Endpoints {
        [global::WolverineGet("/x")]
        public string Handle() => "x";
      }
      """;

    var result = await transformer.TransformAsync(source, "Endpoints.cs");

    await Assert.That(result.TransformedCode).Contains("global::WolverineGet(\"/x\")")
      .Because("an alias-qualified attribute name is not one of the shapes _getAttributeName unwraps, "
             + "so it must be left exactly as written rather than silently dropped");
    await Assert.That(result.Warnings).IsEmpty()
      .Because("nothing was recognized as convertible, so no manual-conversion warning should be raised for it");
    await Assert.That(result.TransformedCode).Contains("using FastEndpoints;")
      .Because("the using-directive swap is triggered independently of attribute recognition");
  }

  // The route extractor falls back to the raw expression text (quote-trimmed) whenever the first
  // argument isn't a plain string literal -- a const reference, a static field, an interpolation.
  // If that fallback regressed, a route driven by anything other than a literal would either throw
  // mid-migration or silently drop the route information the manual-conversion trail depends on.
  [Test]
  public async Task TransformAsync_NonLiteralRouteArgument_UsesTheRawExpressionTextAsync() {
    var transformer = new WolverineHttpTransformer();
    const string source = """
      using Wolverine.Http;

      public class Endpoints {
        private const string Route = "/dynamic";

        [WolverineGet(Route)]
        public string Handle() => "x";
      }
      """;

    var result = await transformer.TransformAsync(source, "Endpoints.cs");

    await Assert.That(result.TransformedCode).Contains("GET(\"Route\")")
      .Because("with no string literal to read, the extractor falls back to the argument's own source text");
    var warning = result.Warnings.FirstOrDefault(w => w.Contains("MANUAL CONVERSION", StringComparison.Ordinal));
    await Assert.That(warning).IsNotNull();
    await Assert.That(warning!).Contains("Route")
      .Because("the fallback text must reach the warning, even though it is not a real route path");
  }
}
