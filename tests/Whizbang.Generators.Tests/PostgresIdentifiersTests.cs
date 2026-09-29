extern alias shared;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using JsonIndexCast = shared::Whizbang.Generators.Shared.Models.JsonIndexCast;
using JsonIndexInfo = shared::Whizbang.Generators.Shared.Models.JsonIndexInfo;
using JsonIndexSql = shared::Whizbang.Generators.Shared.Models.JsonIndexSql;
using PostgresIdentifiers = shared::Whizbang.Generators.Shared.Utilities.PostgresIdentifiers;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Every index name the schema derives fits PostgreSQL's identifier limit and stays unique.
/// </summary>
/// <remarks>
/// PostgreSQL truncates an identifier longer than 63 bytes rather than refusing it. The exact and the
/// case-insensitive index of a long property then became one name, and <c>CREATE INDEX IF NOT
/// EXISTS</c> silently never created the second; a long table name did the same to its
/// <c>created_at</c> and <c>updated_at</c> indexes.
/// </remarks>
/// <code-under-test>src/Whizbang.Generators.Shared/Utilities/PostgresIdentifiers.cs</code-under-test>
/// <docs>fundamentals/perspectives/perspective-indexes</docs>
public class PostgresIdentifiersTests {
  private const string TABLE = "\"public\".wh_per_org_chart";

  /// <summary>A name that fits is not touched, so no existing index is renamed.</summary>
  [Test]
  public async Task ANameThatFitsIsUnchangedAsync() {
    var name = new string('a', 63);

    await Assert.That(PostgresIdentifiers.WithinLimit(name)).IsEqualTo(name);
  }

  /// <summary>An over-long name is shortened to the limit with a stable digest of the whole name.</summary>
  [Test]
  public async Task AnOverLongNameFitsAndIsStableAsync() {
    var name = "idx_org_chart_" + new string('x', 60) + "_json";

    var shortened = PostgresIdentifiers.WithinLimit(name);

    await Assert.That(shortened.Length).IsEqualTo(63);
    await Assert.That(shortened).StartsWith(name[..54]);
    await Assert.That(shortened).IsEqualTo(PostgresIdentifiers.WithinLimit(name))
      .Because("the schema pass recognizes its own index by name on every start");
  }

  /// <summary>
  /// The exact and the folded index of a long property get two names, which is the collision the
  /// scheme exists to prevent.
  /// </summary>
  [Test]
  public async Task TheExactAndFoldedIndexesOfALongPropertyDoNotCollideAsync() {
    const string key = "ReportingStructureReportsToJobName";
    var exact = JsonIndexSql.CreateStatements(
      new JsonIndexInfo(key, key, JsonIndexCast.None, Ordered: true, Substring: false, CaseInsensitive: false),
      TABLE, "org_chart_position_assignment_view").Single();
    var folded = JsonIndexSql.CreateStatements(
      new JsonIndexInfo(key, key, JsonIndexCast.None, Ordered: true, Substring: false, CaseInsensitive: true),
      TABLE, "org_chart_position_assignment_view").Single();

    var exactName = _nameOf(exact);
    var foldedName = _nameOf(folded);

    await Assert.That(exactName.Length).IsLessThanOrEqualTo(63);
    await Assert.That(foldedName.Length).IsLessThanOrEqualTo(63);
    await Assert.That(exactName).IsNotEqualTo(foldedName)
      .Because($"truncated to 63 both were one identifier and the second was never created: {exactName} / {foldedName}");
    await Assert.That(exactName[..63]).IsNotEqualTo(foldedName[..63]);
  }

  /// <summary>A long trigram name is kept within the limit as well.</summary>
  [Test]
  public async Task ALongTrigramNameFitsAsync() {
    const string key = "ReportingStructureReportsToJobName";
    var statement = JsonIndexSql.CreateStatements(
      new JsonIndexInfo(key, key, JsonIndexCast.None, Ordered: false, Substring: true, CaseInsensitive: false, Search: true),
      TABLE, "org_chart_position_assignment_view").ToList();

    await Assert.That(statement.Select(_nameOf).All(static n => n.Length <= 63)).IsTrue();
  }

  private static string _nameOf(string statement) =>
    statement["CREATE INDEX IF NOT EXISTS ".Length..statement.IndexOf(" ON ", StringComparison.Ordinal)];
}
