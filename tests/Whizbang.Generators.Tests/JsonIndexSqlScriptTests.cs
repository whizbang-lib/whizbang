extern alias shared;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using JsonIndexCast = shared::Whizbang.Generators.Shared.Models.JsonIndexCast;
using JsonIndexInfo = shared::Whizbang.Generators.Shared.Models.JsonIndexInfo;
using JsonIndexSql = shared::Whizbang.Generators.Shared.Models.JsonIndexSql;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A table's index script creates the trigram extension once, inside a block the schema pass can
/// skip as a whole.
/// </summary>
/// <remarks>
/// A managed server that does not allow-list the extension refuses <c>CREATE EXTENSION</c> with a
/// feature-not-supported error. Emitted per index and applied as ordinary DDL, that refusal failed
/// the whole perspective pass, and every start of the service paid a failed attempt before the
/// retry happened to succeed by a different path. Once per script, inside a marked block, the pass
/// can skip exactly the indexes that need it and complete.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public class JsonIndexSqlScriptTests {
  private const string TABLE = "\"public\".wh_per_thing";

  private static JsonIndexInfo _substring(string key) =>
    new(key, key, JsonIndexCast.None, Ordered: false, Substring: true, CaseInsensitive: false);

  private static JsonIndexInfo _ordered(string key) =>
    new(key, key, JsonIndexCast.None, Ordered: true, Substring: false, CaseInsensitive: false);

  /// <summary>The per-index statements no longer carry the extension.</summary>
  [Test]
  public async Task TheStatementsForOneIndexCarryNoExtensionAsync() {
    var statements = JsonIndexSql.CreateStatements(_substring("Title"), TABLE, "thing").ToList();

    await Assert.That(statements).Count().IsEqualTo(1);
    await Assert.That(statements[0]).Contains("gin_trgm_ops", StringComparison.Ordinal);
    await Assert.That(statements[0]).DoesNotContain("CREATE EXTENSION", StringComparison.Ordinal)
      .Because("the extension is created once per script, not once per index");
  }

  /// <summary>Two substring indexes share one extension statement in one block.</summary>
  [Test]
  public async Task TwoSubstringIndexesCreateTheExtensionOnceAsync() {
    var script = JsonIndexSql.Script([_ordered("Code"), _substring("Title"), _substring("Summary")], TABLE, "thing");

    var lines = script.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
    var extensionLines = lines.Count(l => l.StartsWith("CREATE EXTENSION", StringComparison.Ordinal));
    await Assert.That(extensionLines).IsEqualTo(1)
      .Because("one refused extension is one warning and one skipped block, not one per index");

    var begin = lines.IndexOf(JsonIndexSql.OPTIONAL_EXTENSION_BEGIN + JsonIndexSql.TRIGRAM_EXTENSION);
    var end = lines.IndexOf(JsonIndexSql.OPTIONAL_EXTENSION_END);
    await Assert.That(begin).IsGreaterThan(0).Because("the block opens after the plain statements");
    await Assert.That(end).IsEqualTo(lines.Count - 1).Because("the block closes the script");
    await Assert.That(lines[begin + 1]).IsEqualTo($"CREATE EXTENSION IF NOT EXISTS {JsonIndexSql.TRIGRAM_EXTENSION};");
    await Assert.That(lines.Skip(begin + 2).Take(end - begin - 2).All(l => l.Contains("gin_trgm_ops", StringComparison.Ordinal))).IsTrue()
      .Because("everything inside the block, and only that, needs the extension");
    await Assert.That(lines[0]).Contains("idx_thing_code_json", StringComparison.Ordinal)
      .Because("the plain index sits outside the block, so a refused extension never costs it");
  }

  /// <summary>A table without substring indexes has no block at all.</summary>
  [Test]
  public async Task NoSubstringIndexMeansNoBlockAsync() {
    var script = JsonIndexSql.Script([_ordered("Code")], TABLE, "thing");

    await Assert.That(script).DoesNotContain(JsonIndexSql.OPTIONAL_EXTENSION_BEGIN, StringComparison.Ordinal);
    await Assert.That(script).DoesNotContain("CREATE EXTENSION", StringComparison.Ordinal);
  }

  /// <summary>Missing arguments are caller errors.</summary>
  [Test]
  public async Task MissingArgumentsAreRefusedAsync() {
    await Assert.That(() => JsonIndexSql.AppendScript(null!, [], TABLE, "thing")).Throws<ArgumentNullException>();
    await Assert.That(() => JsonIndexSql.AppendScript(new System.Text.StringBuilder(), null!, TABLE, "thing")).Throws<ArgumentNullException>();
  }
}
