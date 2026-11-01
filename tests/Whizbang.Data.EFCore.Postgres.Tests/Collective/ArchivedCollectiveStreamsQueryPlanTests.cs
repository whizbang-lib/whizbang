// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Collective;
using Whizbang.Testing;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// The ledger read a collective replay makes to find the tenant's collective streams archived whole (#1932), and the plan
/// the database runs it with.
/// </summary>
/// <remarks>
/// A rebuild replays every stream of a perspective, and each replay asks the stream lifecycle ledger for the tenant's
/// archived streams of the collective types. The ledger holds one row per stream, so without an index each replay reads
/// all of it. The archived-stream index (migration 272) is keyed by stream type and then tenant and holds only archived
/// rows, so the read is one index range per type, and an append, which never changes a row's state, type or tenant,
/// keeps its update heap-only.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Integration")]
[Category("Shard4")]
[NotInParallel("EFCorePostgresTests")]
public class ArchivedCollectiveStreamsQueryPlanTests : EFCoreTestBase {
  private const string INDEX = "idx_stream_lifecycle_archived_type";
  private const string TENANT = "tenant-07";

  private static readonly string[] _collectiveTypes = [
    "Tests.Collectives.LedgerAdjusted0",
    "Tests.Collectives.LedgerAdjusted1",
    "Tests.Collectives.LedgerAdjusted2",
  ];

  /// <summary>
  /// On a ledger large enough that the planner prefers an index for a selective predicate, the read for a tenant and the
  /// read for the streams of no tenant both use the archived-stream index and do not scan the ledger.
  /// </summary>
  [Test]
  public async Task ArchivedCollectiveStreams_OnALargeLedger_ReadTheArchivedStreamIndex_WithAndWithoutATenantAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using (var seed = conn.CreateCommand()) {
      seed.CommandText = """
        INSERT INTO wh_stream_lifecycle
          (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, state)
        SELECT gen_random_uuid(),
               CASE WHEN n % 31 = 0 THEN 'Tests.Collectives.LedgerAdjusted' || (n % 3)
                    ELSE 'Tests.Collectives.SomethingHappened' || (n % 37) END,
               CASE WHEN n % 50 = 0 THEN NULL ELSE 'tenant-' || lpad((n % 49)::text, 2, '0') END,
               NOW() - INTERVAL '1 day', date_trunc('hour', NOW()), 3,
               CASE WHEN n % 7 = 0 THEN 'Archived' ELSE 'Active' END
        FROM generate_series(1, 60000) n;
        ANALYZE wh_stream_lifecycle;
        """;
      seed.CommandTimeout = 300;
      await seed.ExecuteNonQueryAsync();
    }

    var forTenant = await QueryPlan.CaptureAsync(conn, ArchivedCollectiveStreams.Sql(schema: null, TENANT),
      new Dictionary<string, object?> { ["types"] = _collectiveTypes, ["tenant"] = TENANT });
    var forNoTenant = await QueryPlan.CaptureAsync(conn, ArchivedCollectiveStreams.Sql(schema: null, tenant: null),
      new Dictionary<string, object?> { ["types"] = _collectiveTypes });

    await Assert.That(forTenant.ActualRows).IsGreaterThan(0L)
      .Because("the seeded tenant has archived collective streams, so a plan that found none is not the read under test");
    await Assert.That(forNoTenant.ActualRows).IsGreaterThan(0L)
      .Because("streams of no tenant hold archived collectives too");
    forTenant.MustNotSequentiallyScan("wh_stream_lifecycle").MustUseIndex(INDEX);
    forNoTenant.MustNotSequentiallyScan("wh_stream_lifecycle").MustUseIndex(INDEX);
  }
}
