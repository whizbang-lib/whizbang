using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// A column that moves to another table keeps its type, its nullability and its default.
/// </summary>
/// <remarks>
/// <para>
/// Moving a column is a copy of its declaration, and a declaration retyped by hand loses things
/// quietly. The cutover lost two. <c>failure_reason</c> went from
/// <c>INTEGER NOT NULL DEFAULT 99</c> to a bare <c>INTEGER</c>, and because
/// <c>store_inbox_messages</c> and both <c>recover_dead_letter</c> paths insert without naming it,
/// every message stored after the cutover got NULL where it used to get 99 -- while the backfill
/// copied real values for every row that already existed, so the table looked right and only new
/// rows were wrong. <c>status</c> went from <c>DEFAULT 1</c> to <c>DEFAULT 0</c>, which nothing
/// observes today only because every insert names it, and status is a bit field where 0 and 1 are
/// not near-misses.
/// </para>
/// <para>
/// Neither is the kind of thing a behavioral test finds. A NULL is not a quieter 99: both
/// <c>failure_reason = 99</c> and <c>failure_reason &lt;&gt; 99</c> exclude it, so the rows do not
/// fail, they go missing from counts and groupings, and only for rows written after the cutover.
/// </para>
/// <para>
/// <strong>The reference is the outbox, not a list written here.</strong> <c>wh_outbox</c> carries
/// the same work-state columns and was not split, so it is a live and maintained statement of what
/// each one's contract is. Comparing against it means this test has no expected values to drift out
/// of date, and a future change to the outbox's contract shows up here as a disagreement to resolve
/// rather than passing silently. A hardcoded table of types and defaults is the gate that passes
/// after the thing it guards has moved on, which is the failure this suite keeps finding elsewhere.
/// </para>
/// </remarks>
[Category("Shard3")]
public class MovedColumnsKeepTheirContractTests : EFCoreTestBase {
  /// <summary>Columns the two tables share that are NOT expected to agree, with the reason.</summary>
  /// <remarks>
  /// Kept empty unless a real difference is justified. An entry here is a claim that two tables
  /// holding the same work state should describe it differently, which needs saying out loud.
  /// </remarks>
  private static readonly Dictionary<string, string> TOLERATED = new(StringComparer.Ordinal);

  [Test]
  public async Task EveryWorkStateColumnSharedWithTheOutboxHasTheSameContractAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var outbox = await _contractsAsync(conn, "wh_outbox");
    var state = await _contractsAsync(conn, "wh_inbox_state");

    // Anti-vacuity: if either read comes back empty the comparison below is true of nothing, which
    // is the shape this suite exists to refuse. The shared set must also be more than incidental.
    await Assert.That(outbox).IsNotEmpty().Because("wh_outbox must exist for it to be the reference");
    await Assert.That(state).IsNotEmpty().Because("wh_inbox_state must exist after the cutover");

    var shared = outbox.Keys.Intersect(state.Keys, StringComparer.Ordinal)
                            .Where(c => !TOLERATED.ContainsKey(c))
                            .OrderBy(c => c, StringComparer.Ordinal)
                            .ToList();
    await Assert.That(shared.Count).IsGreaterThanOrEqualTo(5)
      .Because("the two tables share the work-state columns; finding almost none means the query "
        + "stopped matching and the comparison proves nothing. Found: " + string.Join(", ", shared));

    var disagreements = shared
      .Where(c => !string.Equals(outbox[c], state[c], StringComparison.Ordinal))
      .Select(c => $"{c}: wh_outbox has [{outbox[c]}] but wh_inbox_state has [{state[c]}]")
      .ToList();

    await Assert.That(disagreements).IsEmpty()
      .Because("a column that moved keeps its type, nullability and default. A default dropped in "
        + "the move is silent: the backfill carries real values for existing rows, so only rows "
        + "written afterwards are wrong, and a NULL is excluded by both = and <> against the value "
        + "it replaced. " + string.Join(" | ", disagreements));
  }

  /// <summary>Column name to a single string holding type, nullability and default.</summary>
  private static async Task<Dictionary<string, string>> _contractsAsync(
      NpgsqlConnection conn, string table) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT column_name,
             data_type
               || ' nullable=' || is_nullable
               || ' default=' || coalesce(column_default, '(none)')
      FROM information_schema.columns
      WHERE table_name = @t
        AND table_schema = current_schema()
      """;
    cmd.Parameters.AddWithValue("t", table);
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      result[reader.GetString(0)] = reader.GetString(1);
    }
    return result;
  }
}
