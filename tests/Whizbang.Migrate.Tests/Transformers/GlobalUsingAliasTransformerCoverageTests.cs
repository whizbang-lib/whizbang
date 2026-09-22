using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Coverage-round tests for <see cref="GlobalUsingAliasTransformer"/> branches not exercised by
/// <see cref="GlobalUsingAliasTransformerTests"/>: a global alias whose target is not a plain
/// name at all (so detection has nothing to pattern-match against), and a global alias that
/// coexists with a real Marten/Wolverine rewrite but does not itself target either package.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/GlobalUsingAliasTransformer.cs:104,177</tests>
public class GlobalUsingAliasTransformerCoverageTests {

  // Since C# 12, a using-alias can target any type, not just a name -- including a tuple type,
  // which has no NameSyntax to report. _isTargetType(null) must treat that as "not a target"
  // rather than throwing or matching by accident. If this regressed to crash on a null type
  // name, one non-name alias anywhere in a file with a genuine Marten/Wolverine alias would take
  // down the whole transform for that file instead of just leaving the harmless alias alone.
  [Test]
  public async Task TransformAsync_TupleTypeAliasHasNoName_IsLeftUnchangedAsync() {
    var transformer = new GlobalUsingAliasTransformer();
    const string source = """
      global using Store = Marten.IDocumentStore;
      global using PairAlias = (int, int);
      """;

    var result = await transformer.TransformAsync(source, "GlobalUsings.cs");

    await Assert.That(result.TransformedCode).Contains("global using PairAlias = (int, int);")
      .Because("an alias with no name to inspect is not a Marten/Wolverine type and must survive byte-for-byte");
    await Assert.That(result.TransformedCode).Contains("Whizbang.Core.Messaging.IEventStore")
      .Because("the genuine Marten alias alongside it must still be rewritten");
  }

  // A global alias to an unrelated named type (not Marten/Wolverine) must fall through the
  // detection loop untouched even while a sibling alias in the same file IS rewritten. If the
  // per-using loop stopped distinguishing "not a target" from "target with no known mapping", an
  // unrelated alias could be dropped or warned about even though this transformer has no
  // business touching it.
  [Test]
  public async Task TransformAsync_UnrelatedNamedAliasAlongsideARewrite_SurvivesUnchangedAsync() {
    var transformer = new GlobalUsingAliasTransformer();
    const string source = """
      global using Store = Marten.IDocumentStore;
      global using TextAlias = System.String;
      """;

    var result = await transformer.TransformAsync(source, "GlobalUsings.cs");

    await Assert.That(result.TransformedCode).Contains("global using TextAlias = System.String;")
      .Because("an alias to an unrelated named type is not this transformer's concern");
    await Assert.That(result.TransformedCode).Contains("Whizbang.Core.Messaging.IEventStore")
      .Because("the genuine Marten alias alongside it must still be rewritten");
    await Assert.That(result.Warnings.Any(w => w.Contains("TextAlias", StringComparison.Ordinal))).IsFalse()
      .Because("an unrelated alias must not be flagged as if it were an unrecognized Marten/Wolverine type");
  }
}
