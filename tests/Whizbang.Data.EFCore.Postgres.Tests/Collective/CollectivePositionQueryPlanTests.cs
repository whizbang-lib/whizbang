// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Collective;
using Whizbang.Testing;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// The event-store read a collective replay makes to find where each collective of a tenant sits, and the plan the
/// database runs it with.
/// </summary>
/// <remarks>
/// <para>
/// During a rebuild or a rewind, <see cref="CollectiveReplayApplier"/> asks the event store for every event of the
/// model's collective types in the stream's tenant: <c>event_type = ANY(...) AND scope ->> 't' = ...</c>. The event
/// store carried no index over either column, so each replay read the whole store to return a few rows. Measured on
/// a deployed service: 361 calls at 1.6 s each, the fifteenth-largest consumer of query time on its database.
/// </para>
/// <para>
/// Two things have to hold for the index to serve it, and each is pinned here. The JSON key has to reach the server
/// as the literal <c>'t'</c>: an expression index matches only the same expression, so a key sent as a bind parameter
/// could never use it however the index was written. And the index has to exist, keyed by event type and then by
/// tenant, so the set of types becomes one index range per type.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Integration")]
[Category("Shard4")]
[NotInParallel("EFCorePostgresTests")]
public class CollectivePositionQueryPlanTests : EFCoreTestBase {
  private const string INDEX = "idx_event_store_type_tenant";
  private const string TENANT = "tenant-07";

  private static readonly List<string> _collectiveTypes = [
    "Tests.Collectives.LedgerAdjusted0, Tests.Collectives",
    "Tests.Collectives.LedgerAdjusted1, Tests.Collectives",
    "Tests.Collectives.LedgerAdjusted2, Tests.Collectives",
  ];

  private string _positionsSql() {
    using var context = CreateDbContext();
    var query = CollectiveReplayApplier.CollectivePositions(
      new EFCoreFilterableEventStoreQuery(context).Query, _collectiveTypes, TENANT);
    return string.Join('\n', query.ToQueryString().Split('\n').Where(line => !line.StartsWith("--", StringComparison.Ordinal)));
  }

  /// <summary>The tenant key is sent as the literal the expression index is written with, never as a parameter.</summary>
  [Test]
  public async Task CollectivePositions_SendsTheTenantKeyAsALiteralAsync() {
    var sql = _positionsSql();

    await Assert.That(sql).Contains("scope ->> 't'", StringComparison.Ordinal)
      .Because("an expression index on (scope ->> 't') matches only that expression with that literal key; a key "
        + "bound as a parameter would leave the read a scan of every row of the requested types");
    await Assert.That(sql).Contains("event_type = ANY (@typeNames)", StringComparison.Ordinal);
  }

  /// <summary>
  /// On a store large enough that the planner prefers an index for a selective predicate, the read uses the type and
  /// tenant index and does not scan the store.
  /// </summary>
  [Test]
  public async Task CollectivePositions_OnALargeStore_ReadsTheTypeAndTenantIndexAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using (var seed = conn.CreateCommand()) {
      seed.CommandText = """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
           commit_sequence, flags, created_at)
        SELECT gen_random_uuid(), s.stream_id, s.stream_id, 'Tests.Collectives.LedgerAggregate',
               CASE WHEN g.seq % 97 = 0 THEN 'Tests.Collectives.LedgerAdjusted' || (g.seq % 3) || ', Tests.Collectives'
                    ELSE 'Tests.Collectives.SomethingHappened' || (g.seq % 37) || ', Tests.Collectives' END,
               jsonb_build_object('t', 'tenant-' || lpad((s.n % 50)::text, 2, '0'), 'u', 'user-' || (g.seq % 500)),
               v.version, g.seq, 0, NOW() - INTERVAL '1 day'
        FROM (SELECT n, gen_random_uuid() AS stream_id FROM generate_series(1, 2000) n) s
        CROSS JOIN LATERAL generate_series(1, 20) v(version)
        CROSS JOIN LATERAL (SELECT (v.version - 1) * 2000 + s.n AS seq) g;
        ANALYZE wh_event_store;
        """;
      seed.CommandTimeout = 300;
      await seed.ExecuteNonQueryAsync();
    }

    var plan = await QueryPlan.CaptureAsync(conn, _positionsSql(), new Dictionary<string, object?> {
      ["typeNames"] = _collectiveTypes.ToArray(),
      ["tenantId"] = TENANT,
    });

    await Assert.That(plan.ActualRows).IsGreaterThan(0L)
      .Because("the seeded tenant holds collectives, so a plan that found none is not the read under test");
    plan.MustNotSequentiallyScan("wh_event_store")
        .MustUseIndex(INDEX);
  }
}
