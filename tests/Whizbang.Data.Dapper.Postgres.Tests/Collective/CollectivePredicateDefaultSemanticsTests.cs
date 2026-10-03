#pragma warning disable CA1707

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Unit tests (no database) for equality and <c>Contains</c> over a member that declares a default (#1044).
/// A document written before the member existed has no key for it. The in-memory replay deserializes that row and
/// the member holds its default, so the predicate sees the default; the collective path reads the raw document, so
/// without help it sees SQL <c>NULL</c> and the row drops out of the cohort. The two must agree, which is the same
/// invariant the ordering comparisons already keep via <c>NOT (COALESCE(…, FALSE))</c>.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class CollectivePredicateDefaultSemanticsTests {
  private const string TABLE = "wh_per_defaulted";

  static CollectivePredicateDefaultSemanticsTests() {
    // What the perspective generator emits for a member whose declaration carries an initializer. Members with no
    // declared default are deliberately left unregistered, so the tests below can hold the change to its scope.
    PerspectiveMemberDefaultRegistry.Register(typeof(DefaultedModel), nameof(DefaultedModel.Status), "Draft");
  }

  [SuppressIndexAdvisory("test fixture; compiled to SQL text, never queried")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "The members are read by the expression trees under test.")]
  private sealed class DefaultedModel {
    public string Status { get; init; } = "Draft";
    public string? Note { get; init; }
  }

  private static CollectivePredicateSqlCompiler<DefaultedModel>.CompiledWhereClause _compile(
      Expression<Func<PerspectiveRow<DefaultedModel>, bool>> filter) =>
    CollectivePredicateSqlCompiler<DefaultedModel>.Compile(filter, outerTableName: TABLE);

  [Test]
  public async Task Compile_InequalityOnAMemberWithADeclaredDefault_ReadsAnAbsentKeyAsThatDefaultAsync() {
    var result = _compile(r => r.Data.Status != "Archived");

    await Assert.That(result.SqlFragment)
      .IsEqualTo("COALESCE(data->>'Status', @where_status_else) <> @where_status")
      .Because("SQL NULL <> 'Archived' is NULL, so the row drops out, while the replay sees the default 'Draft' and "
        + "keeps it. Reading the absent key as the declared default is what makes the two agree.");
    await Assert.That(result.Parameters["where_status_else"]).IsEqualTo("Draft");
  }

  [Test]
  public async Task Compile_EqualityAgainstTheDefaultItself_SelectsRowsWhoseKeyIsAbsentAsync() {
    var result = _compile(r => r.Data.Status == "Draft");

    await Assert.That(result.SqlFragment)
      .IsEqualTo("COALESCE(data->>'Status', @where_status_else) = @where_status")
      .Because("This is the case IS NOT DISTINCT FROM gets wrong: it treats an absent key as distinct from every "
        + "value, so a row that the replay reports as Draft would not be selected. Only the default agrees.");
    await Assert.That(result.Parameters["where_status_else"]).IsEqualTo("Draft");
  }

  [Test]
  public async Task Compile_ContainsOverAMemberWithADeclaredDefault_ReadsAnAbsentKeyAsThatDefaultAsync() {
    string[] eligible = ["Draft", "Approved"];
    var result = _compile(r => eligible.Contains(r.Data.Status));

    await Assert.That(result.SqlFragment)
      .IsEqualTo("COALESCE(data->>'Status', @where_status_else) IN (@where_status_0, @where_status_1)")
      .Because("NULL IN (…) is NULL, never true, so a membership test silently skips every row written before the "
        + "member existed — the oldest rows, which is the opposite of what the author intends.");
    await Assert.That(result.Parameters["where_status_else"]).IsEqualTo("Draft");
  }

  [Test]
  public async Task Compile_InequalityOnAMemberWithNoDeclaredDefault_LeavesTheComparisonBareAsync() {
    var result = _compile(r => r.Data.Note != "x");

    await Assert.That(result.SqlFragment)
      .IsEqualTo("data->>'Note' <> @where_note")
      .Because("A member with no declared default has nothing an absent key could read as, and a nullable member's "
        + "null is a value the author can already test for. Coalescing here would change a predicate that is "
        + "correct today, so the fix stays scoped to members that declare one.");
  }
}
