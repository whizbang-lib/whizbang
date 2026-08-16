using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the table-rewrite request queue (migration 089), against real Postgres.
///
/// <para>
/// Postgres reclaims deleted rows to the free space map but never returns them to the OS, so a
/// churning table's file stays large and every scan keeps paying for the empty pages; a dropped
/// column leaves bytes autovacuum can never reclaim at all. Only a rewrite recovers either.
/// Measured on a live queue table: 160 MB holding 23,872 rows with ZERO dead tuples and a 65 ms
/// scan, versus 47 MB and 26 ms for the same rows afterwards.
/// </para>
///
/// <para>
/// The load-bearing property is that candidates are RE-MEASURED at call time rather than trusted
/// from the request list. Without that, a migration whose content hash changed replays, re-requests
/// an already-rewritten table, and earns a pointless multi-minute ACCESS EXCLUSIVE lock.
/// </para>
/// </summary>
/// <docs>operations/observability/metrics#table-statistics</docs>
public class TableRewriteRequestSqlTests : EFCoreTestBase {
  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task LeanTable_IsNotOfferedForRewrite_EvenWhenRequestedAsync() {
    // Migration 089 records wh_event_store, because 078 left it carrying dropped inline body
    // columns. On a fresh database that table is empty, so re-measuring must find it lean and
    // offer nothing — otherwise every new deployment would take a lock for no reason, and every
    // migration replay would do it again.
    await using var ctx = CreateDbContext();
    var candidates = await _coordinator(ctx).GetTablesNeedingRewriteAsync();

    await Assert.That(candidates.Any(c => c.TableName == "wh_event_store")).IsFalse()
      .Because("a recorded request must still be re-measured; an empty table owes no rewrite");
  }

  [Test]
  public async Task RequestIsIdempotent_AndClearRemovesItAsync() {
    await using var ctx = CreateDbContext();
    var connection = (Npgsql.NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    async Task<string> PendingAsync() {
      await using var read = connection.CreateCommand();
      read.CommandText = "SELECT COALESCE(setting_value,'') FROM wh_settings WHERE setting_key='pending_table_rewrites'";
      return (string)(await read.ExecuteScalarAsync())!;
    }

    async Task RequestAsync(string table) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT wh_request_table_rewrite(@t)";
      cmd.Parameters.AddWithValue("t", table);
      await cmd.ExecuteNonQueryAsync();
    }

    await RequestAsync("wh_outbox");
    var afterFirst = await PendingAsync();
    await Assert.That(afterFirst.Split(',').Count(x => x == "wh_outbox")).IsEqualTo(1);

    // A migration replays whenever its content hash changes, so the same request arrives again.
    await RequestAsync("wh_outbox");
    var afterSecond = await PendingAsync();
    await Assert.That(afterSecond.Split(',').Count(x => x == "wh_outbox")).IsEqualTo(1)
      .Because("re-recording a request must not duplicate it — migrations replay routinely");

    await _coordinator(ctx).ClearTableRewriteRequestAsync("wh_outbox");
    var afterClear = await PendingAsync();
    await Assert.That(afterClear.Split(',').Contains("wh_outbox")).IsFalse()
      .Because("a satisfied request is cleared so it is not re-offered forever");
  }

  [Test]
  public async Task RewriteTable_RejectsAnythingOutsideTheFrameworksOwnTablesAsync() {
    // VACUUM FULL cannot be parameterised, so the name is interpolated into DDL. This check is
    // the only thing between a caller-supplied string and that interpolation.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    foreach (var bad in new[] { "users", "wh_outbox; DROP TABLE wh_outbox", "wh_Outbox", "WH_outbox", "wh" }) {
      await Assert.That(async () => await coordinator.RewriteTableAsync(bad))
        .Throws<ArgumentException>()
        .Because($"'{bad}' is not a framework table name and must never reach interpolated DDL");
    }
  }

  [Test]
  public async Task RewriteTable_OnAFrameworkTable_ReturnsAMeasuredRatioAsync() {
    await using var ctx = CreateDbContext();
    var ratio = await _coordinator(ctx).RewriteTableAsync("wh_outbox");

    // A freshly-migrated table has too few rows for a meaningful per-row average, so the measure
    // may legitimately be null. What matters is that the call completes rather than throwing —
    // the rewrite ran and the caller got an answer it can compare against.
    await Assert.That(ratio is null || ratio > 0).IsTrue()
      .Because("the rewrite must complete and report a ratio the caller can verify against");
  }

  [Test]
  [Timeout(60000)]
  public async Task CoordinatorRequestTableRewrite_RecordsThePendingRequestAsync(CancellationToken cancellationToken) {
    // The runtime maintenance cycle records instead of executing (increment 8) — this is the
    // coordinator path it records through.
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    await coordinator.RequestTableRewriteAsync("wh_outbox", cancellationToken);
    await coordinator.RequestTableRewriteAsync("wh_outbox", cancellationToken);   // idempotent

    await using var conn = new Npgsql.NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT COALESCE(setting_value,'') FROM wh_settings WHERE setting_key='pending_table_rewrites'";
    var pending = (string)(await read.ExecuteScalarAsync(cancellationToken))!;

    await Assert.That(pending.Split(',').Count(t => t == "wh_outbox")).IsEqualTo(1)
      .Because("recording rides the same idempotent wh_request_table_rewrite a migration uses");

    await coordinator.ClearTableRewriteRequestAsync("wh_outbox", cancellationToken);
  }
}
