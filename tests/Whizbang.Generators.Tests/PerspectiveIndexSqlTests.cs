extern alias shared;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using PerspectiveIndexSql = shared::Whizbang.Generators.Shared.Models.PerspectiveIndexSql;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The one statement every perspective index is created through.
/// </summary>
/// <remarks>
/// The statement carries the index's DDL unchanged, so a reader of the schema script sees exactly
/// what would be built, and the function it calls decides whether to build it by comparing the
/// definition against the table's existing indexes rather than by name alone.
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-indexes</docs>
public class PerspectiveIndexSqlTests {
  private const string SCHEMA = "\"inventory\"";

  /// <summary>A create statement is handed to the comparing function, its DDL intact.</summary>
  [Test]
  public async Task ACreateStatementIsEnsuredAsync() {
    var ensured = PerspectiveIndexSql.Ensure(
      "CREATE INDEX IF NOT EXISTS idx_order_scope_tenant ON \"inventory\".wh_per_order ((scope->>'t'));", SCHEMA);

    await Assert.That(ensured).IsEqualTo(
      "SELECT \"inventory\".wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS idx_order_scope_tenant ON \"inventory\".wh_per_order ((scope->>'t'))$wbix$);");
  }

  /// <summary>A unique index is compared as a unique index, so the statement keeps the word.</summary>
  [Test]
  public async Task AUniqueCreateStatementIsEnsuredAsync() {
    var ensured = PerspectiveIndexSql.Ensure(
      "CREATE UNIQUE INDEX IF NOT EXISTS idx_order_code ON \"inventory\".wh_per_order (code)", SCHEMA);

    await Assert.That(ensured).IsEqualTo(
      "SELECT \"inventory\".wh_ensure_index($wbix$CREATE UNIQUE INDEX IF NOT EXISTS idx_order_code ON \"inventory\".wh_per_order (code)$wbix$);");
  }

  /// <summary>
  /// Anything that is not an idempotent create passes through untouched: a drop is an explicit
  /// decision, and the function only knows how to compare what it would create.
  /// </summary>
  [Test]
  [Arguments("DROP INDEX IF EXISTS \"inventory\".idx_order_day_json;")]
  [Arguments("CREATE INDEX idx_order_plain ON \"inventory\".wh_per_order (code);")]
  [Arguments("-- a comment")]
  public async Task AnythingElsePassesThroughAsync(string statement) {
    await Assert.That(PerspectiveIndexSql.Ensure(statement, SCHEMA)).IsEqualTo(statement);
  }

  /// <summary>A null statement is refused rather than rendered as an empty call.</summary>
  [Test]
  public async Task ANullStatementIsRefusedAsync() {
    await Assert.That(() => PerspectiveIndexSql.Ensure(null!, SCHEMA)).Throws<ArgumentNullException>();
  }
}
