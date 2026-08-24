using System.Globalization;
using Dapper;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Locks the deterministic lock-acquisition order of <c>commit_handler_batch</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>jsonb_array_elements</c> preserves the CALLER's array order, so without an explicit sort the
/// batch takes its row locks in whatever order the flusher happened to assemble it — which differs
/// between instances and between batches on one instance. Two concurrent batches whose handler sets
/// overlap then acquire the same rows in opposite orders and block each other. Observed in
/// production as a circular wait (two backends each reported as blocked by the other on
/// <c>wait_event=transactionid</c>) with per-commit cost degrading to ~100-190ms, which makes it
/// arithmetically impossible to commit a large held set inside one lease.
/// </para>
/// <para>
/// HOW THIS IS OBSERVED WITHOUT A CONCURRENCY HARNESS. Two other approaches were rejected first.
/// Pre-locking one row on each of two connections and calling the function in opposite orders
/// deadlocks REGARDLESS of the fix, because those pre-locks are taken outside the function — it
/// proves nothing. Running many concurrent batches and asserting no deadlock is statistical and
/// timing-dependent, i.e. precisely the flaky shape this suite avoids. What works instead: Tier 1
/// returns its success rows from a SEPARATE unordered scan, but the Tier 2 fallback emits
/// <c>RETURN QUERY</c> from INSIDE the loop — so on that path, emission order IS processing order.
/// Forcing Tier 2 and feeding a deliberately shuffled batch therefore observes the real function's
/// ordering directly, deterministically, with no timing involved.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
public class CommitHandlerBatchLockOrderSqlTests : PostgresTestBase {

  /// <summary>Deliberately shuffled: the batch is submitted in DESCENDING message-id order.</summary>
  private static readonly int[] _submissionOrder = [5, 3, 1, 4, 2];

  /// <summary>
  /// Ids are zero-padded so their TEXT ordering — which is what the function's ORDER BY compares —
  /// matches their numeric ordering.
  /// </summary>
  private static string _id(int n, string prefix) =>
    $"{prefix}-0000-0000-0000-{n.ToString("D12", CultureInfo.InvariantCulture)}";

  private static string _messageId(int n) => _id(n, "00000000");
  private static string _handlerId(int n) => _id(n, "11111111");

  [Test]
  public async Task CommitHandlerBatch_ProcessesInMessageIdOrder_RegardlessOfSubmissionOrderAsync() {
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // One element carries a deliberately un-castable instance_id. commit_handler_result casts it to
    // UUID, so that element raises, the optimistic bulk tier aborts, and the whole batch replays
    // through the Tier 2 savepoint loop — the path whose emission order reveals processing order.
    // It still gets sorted like any other element; it simply reports success=false when it fails.
    var elements = _submissionOrder.Select(n => {
      var instanceId = n == 4 ? "not-a-uuid" : "22222222-0000-0000-0000-000000000000";
      // NOTE the space before the final brace. In a $$""" raw string `}}` closes an interpolation,
      // so the two adjacent closing braces this JSON needs would be parsed as interpolation syntax.
      // JSON ignores the whitespace.
      return $$"""
        {"handler_id":"{{_handlerId(n)}}","instance_id":"{{instanceId}}",
         "inbox_completion":{"MessageId":"{{_messageId(n)}}","Status":2} }
        """;
    });
    var payload = "[" + string.Join(",", elements) + "]";

    // Joined into a single string ON PURPOSE. A collection-equivalence assertion can be
    // order-INSENSITIVE, which would pass on a correctly-ordered and a caller-ordered result alike —
    // vacuous for a test whose entire subject is order.
    var returned = string.Join(",", (await connection.QueryAsync<Guid>(
      "SELECT handler_id FROM commit_handler_batch(@Payload::jsonb)",
      new { Payload = payload })));

    var expected = string.Join(",", _submissionOrder.OrderBy(n => n).Select(n => Guid.Parse(_handlerId(n))));

    await Assert.That(returned).IsEqualTo(expected)
      .Because("the batch must take its row locks in a total order derived from the data, not in the "
             + "order the caller happened to assemble it. Submitted as "
             + string.Join(",", _submissionOrder) + " it must still be processed as "
             + string.Join(",", _submissionOrder.OrderBy(n => n)) + ", so that two concurrent "
             + "batches with overlapping handler sets can never acquire the same rows in opposite "
             + "orders and deadlock each other");
  }

  [Test]
  public async Task CommitHandlerBatch_SortsElementsWithNoInboxCompletion_DeterministicallyAsync() {
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Elements carrying no inbox_completion sort on the handler_id tiebreaker. Without it their
    // relative order would be whatever the caller supplied, which is the same hazard in miniature.
    var elements = _submissionOrder.Select(n => {
      var instanceId = n == 4 ? "not-a-uuid" : "22222222-0000-0000-0000-000000000000";
      return $$"""{"handler_id":"{{_handlerId(n)}}","instance_id":"{{instanceId}}"}""";
    });
    var payload = "[" + string.Join(",", elements) + "]";

    var returned = string.Join(",", (await connection.QueryAsync<Guid>(
      "SELECT handler_id FROM commit_handler_batch(@Payload::jsonb)",
      new { Payload = payload })));

    var expected = string.Join(",", _submissionOrder.OrderBy(n => n).Select(n => Guid.Parse(_handlerId(n))));

    await Assert.That(returned).IsEqualTo(expected)
      .Because("a missing inbox_completion must not fall back to caller order — the handler_id "
             + "tiebreaker has to give those elements a total order too, or batches made up of "
             + "them stay free to deadlock");
  }
}
