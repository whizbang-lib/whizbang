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

    // A member that is BOTH promoted to a column and carries a default. The column is filled when it is added
    // (#1021), so the document default must not be layered on top of it.
    PerspectivePhysicalFieldRegistry.Register(
      typeof(DefaultedModel), nameof(DefaultedModel.Rank), "rank", FieldStorageMode.Split);
    PerspectiveMemberDefaultRegistry.Register(typeof(DefaultedModel), nameof(DefaultedModel.Rank), 0L);
  }

  [SuppressIndexAdvisory("test fixture; compiled to SQL text, never queried")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "The members are read by the expression trees under test.")]
  private sealed class DefaultedModel {
    public string Status { get; init; } = "Draft";
    public string? Note { get; init; }
    public long Rank { get; init; }
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

  // ── Testing for null: an absent key and an explicit JSON null are the same thing ──────────

  [Test]
  public async Task Compile_EqualityAgainstNullOnANullableMember_AsksWhetherTheKeyReadsAsNullAsync() {
    var result = _compile(r => r.Data.Note == null);

    await Assert.That(result.SqlFragment)
      .IsEqualTo("data->>'Note' IS NULL")
      .Because("Binding null as a parameter gives `x = NULL`, which is NULL and never true, so no collective filter "
        + "could test for null at all. ->> reads an absent key and an explicit JSON null alike, which is the same "
        + "collapse deserialization makes, so IS NULL is both the fix and the intended semantics.");
  }

  [Test]
  public async Task Compile_InequalityAgainstNullOnANullableMember_AsksWhetherTheKeyHasAValueAsync() {
    var result = _compile(r => r.Data.Note != null);

    await Assert.That(result.SqlFragment)
      .IsEqualTo("data->>'Note' IS NOT NULL")
      .Because("The mirror of the above: `x <> NULL` is NULL for every row, so the predicate selected nothing.");
  }

  [Test]
  public async Task Compile_EqualityAgainstNullOnAMemberWithADeclaredDefault_CannotHoldAsync() {
    var result = _compile(r => r.Data.Status == null);

    await Assert.That(result.SqlFragment)
      .IsEqualTo("COALESCE(data->>'Status', @where_status_else) IS NULL")
      .Because("A member that declares a default has no null to find: the absent key reads as the default, so the "
        + "test is false for every row — which is exactly what the replay reports, since the member cannot be null. "
        + "The two rules compose without either one special-casing the other.");
  }

  // ── Where the declared default must NOT be applied ───────────────────────────────────────

  [Test]
  public async Task Compile_AComparisonOnAPromotedColumn_DoesNotLayerTheDocumentDefaultOnItAsync() {
    var result = _compile(r => r.Data.Rank == 3L);

    await Assert.That(result.SqlFragment).DoesNotContain("COALESCE")
      .Because("A promoted column is filled from the document when it is added (#1021), so it has a real value "
        + "rather than an absent key. Coalescing it would describe a state the fill is there to prevent.");
  }

  [Test]
  public async Task Compile_AComparisonOnTheRowId_DoesNotCoalesceAsync() {
    var id = Guid.Parse("0199ffff-0000-7000-8000-00000000beef");
    var result = _compile(r => r.Id == id);

    await Assert.That(result.SqlFragment).DoesNotContain("COALESCE")
      .Because("The row id is a real uuid column that every row has; there is no absent key to read as anything.");
  }

  [Test]
  public async Task Compile_AMembershipTestOverTheRowIdIncludingNull_BindsTheNullAsync() {
    var id = Guid.Parse("0199ffff-0000-7000-8000-00000000cafe");
    Guid?[] candidates = [null, id];
    var result = _compile(r => candidates.Contains(r.Id));

    await Assert.That(result.SqlFragment).Contains("IN (")
      .Because("A null inside an IN list over the row id is the one route that still binds null against a uuid "
        + "column — the equality path now answers null with IS NULL and never binds it.");
    await Assert.That(result.Parameters.Values.Any(v => v is null)).IsTrue()
      .Because("The null element has to reach the parameter list as a real null, not as the text \"null\".");
  }
}
