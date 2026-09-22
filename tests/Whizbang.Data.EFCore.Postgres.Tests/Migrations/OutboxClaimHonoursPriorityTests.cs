using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The claim must pick outbox work in priority order, as it already does for inbox work.
/// </summary>
/// <remarks>
/// <para>
/// A message carries a priority from the moment it is dispatched: the producer hook declares it,
/// the store writes it to the row, and migration 151 puts it on the wire so the receiver sees it.
/// Every queue in the path then honours it -- the inbox claim reads it through band lanes, and the
/// read-model claim orders by it -- except this one, which selected outbox work by arrival alone
/// and returned NULL where the priority belonged.
/// </para>
/// <para>
/// The consequence is the symptom that started this: a reply a person is waiting for is published
/// behind whatever bulk work was queued before it, however urgent it was declared. Prioritising
/// intake without prioritising publication fixes the half nobody sees.
/// </para>
/// <para>
/// Ordering is on the number rather than the band, which is what the priority contract says:
/// scheduling works on the bucket, and INSIDE a bucket the number orders work. Arrival remains the
/// tiebreak, so rows of equal priority keep first-in-first-out.
/// </para>
/// <para>
/// This costs no index. The branch is already narrowed to one instance's leased rows by
/// idx_outbox_outstanding_by_instance, and it already sorts -- that index carries lease_expiry, not
/// created_at -- so priority joins an existing sort key rather than adding a scan. Measured on a
/// deployed database, the plan is identical either way (Sort, cost 1.12..1.13). That matters: the
/// outbox is the hottest write path in the system during a bulk load, and an index added here is
/// paid on every insert.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/message-priority</docs>
[Category("Migrations")]
[Category("Shard4")]
public class OutboxClaimHonoursPriorityTests {

  private static string _lastWordOf(string function) {
    var sql = new Whizbang.Data.Postgres.PostgresMigrationProvider(
        typeof(Whizbang.Data.Postgres.PostgresMigrationProvider).Assembly, "__SCHEMA__")
      .GetMigrations()
      .Where(m => m.Sql.Contains($"FUNCTION __SCHEMA__.{function}", StringComparison.Ordinal))
      .OrderBy(m => m.Name, StringComparer.Ordinal)
      .Last().Sql;
    var at = sql.IndexOf($"FUNCTION __SCHEMA__.{function}", StringComparison.Ordinal);
    return sql[at..];
  }

  /// <summary>The slice of claim_work that selects outbox rows, bounded by its own FROM and LIMIT.</summary>
  private static string _outboxBranch() {
    var body = _lastWordOf("claim_work");
    var from = body.IndexOf("FROM __SCHEMA__.wh_outbox o", StringComparison.Ordinal);
    if (from < 0) {
      return string.Empty;
    }
    // Back up to the SELECT that owns this FROM, forward to the statement's LIMIT.
    var start = body.LastIndexOf("SELECT", from, StringComparison.Ordinal);
    var end = body.IndexOf("LIMIT p_max_streams", from, StringComparison.Ordinal);
    return end > start ? body[start..end] : body[start..Math.Min(from + 1200, body.Length)];
  }

  [Test]
  public async Task TheOutboxClaim_OrdersByPriority_NotArrivalAloneAsync() {
    var branch = _outboxBranch();

    await Assert.That(branch).IsNotEmpty()
      .Because("the outbox selection has to be findable in claim_work, or this rule asserts nothing.");

    await Assert.That(branch).Contains("ORDER BY o.priority")
      .Because("every other queue in the path honours the priority the producer declared -- the "
        + "inbox through its band lanes, the read models by ordering on it. Selecting outbox work "
        + "by arrival alone publishes an urgent reply behind whatever bulk work was queued first.");

    await Assert.That(branch).Contains("o.created_at")
      .Because("arrival stays the tiebreak so rows of equal priority keep first-in-first-out; "
        + "priority orders between them, it does not replace them.");
  }

  [Test]
  public async Task TheOutboxClaim_ReturnsThePriorityItSelectedOnAsync() {
    var branch = _outboxBranch();

    await Assert.That(branch).DoesNotContain("NULL::INTEGER                 AS priority")
      .Because("the branch returned NULL where the priority belonged, so nothing downstream -- the "
        + "drain, the metrics, an operator reading a work batch -- could see what it was scheduled "
        + "on. A column that exists and reads NULL is worse than absent: it looks answered.");
  }

  [Test]
  public async Task TheInboxClaim_StillHonoursPriority_SoTheRuleIsNotVacuousAsync() {
    // If the inbox ever stopped banding, the premise of the rule above is gone and it would be
    // enforcing a convention nothing else follows.
    var body = _lastWordOf("claim_work");
    await Assert.That(body).Contains("i.priority <= 99")
      .Because("the inbox side is the precedent this rule points at; it has to still be there.");
  }
}
