// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The retention and storage sweeps on the Dapper driver, with the contract the EF Core driver keeps: the offload-claim
/// ledger and its sweep claim, the settled apply-path fold and its claim, the ancient ephemeral pointer prune, the
/// ephemeral reclassification, the per-type grace sync, the lifecycle-completion cleanup and the exhausted orphan reap
/// (#2051).
/// </summary>
/// <remarks>
/// <para>
/// This coordinator inherited the interface's inert defaults for every one of them, so on this driver a retention
/// setting such as the pointer prune or the lifecycle-completion window was accepted and removed nothing. Each member now
/// runs the statement the EF Core coordinator runs, from <c>CoordinatorRetentionSql</c>, qualified with this
/// coordinator's schema.
/// </para>
/// <para>
/// Each test has a twin on the EF Core driver (<c>OffloadClaimLedgerSqlTests</c>, <c>ApplyPathFoldSqlTests</c>,
/// <c>EphemeralPointerPruneSqlTests</c>, <c>EphemeralReclassifyCoordinatorTests</c>,
/// <c>EFCoreWorkCoordinatorLifecycleAndJanitorTests</c>). Design choice documented at data/drivers#dapper-coordinator-parity.
/// </para>
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.RetentionSweeps.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorRetentionSql.cs</code-under-test>
[Category("Integration")]
public class DapperRetentionSweepTests : PostgresTestBase {
  // Typed as the interface, as the sweeps hold it: a member left at the interface default is what they would call.
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Deliberate: the interface is what the workers call through, so an unimplemented member reaches its default here as it does there.")]
  private IWorkCoordinator _coordinator() =>
    new DapperWorkCoordinator(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task OffloadClaims_RecordExpireAndRemove_KeepTheLedgerAsync() {
    var coordinator = _coordinator();

    await coordinator.RecordOffloadClaimAsync("ledger/old", "blob-a");
    await coordinator.RecordOffloadClaimAsync("ledger/old", "blob-a");
    await coordinator.RecordOffloadClaimAsync("ledger/fresh", "blob-a");
    await _executeAsync("UPDATE wh_offload_claims SET uploaded_at = NOW() - INTERVAL '40 days' WHERE storage_key = 'ledger/old'");

    var expired = await coordinator.GetExpiredOffloadClaimsAsync(TimeSpan.FromDays(30), batchSize: 100);

    await Assert.That(expired.Select(c => c.StorageKey)).IsEquivalentTo(["ledger/old"])
      .Because("only a claim uploaded past the window is offered to the sweep, and recording it twice keeps one row");
    await Assert.That(expired[0].ProviderName).IsEqualTo("blob-a");

    await coordinator.RemoveOffloadClaimsAsync(["ledger/old"]);
    await coordinator.RemoveOffloadClaimsAsync([]);

    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_offload_claims")).IsEqualTo(1L)
      .Because("a removed key's row goes and every other row stays for the next sweep");
  }

  [Test]
  public async Task OffloadSweepClaim_SecondClaimInsideTheWindow_LosesAsync() {
    var coordinator = _coordinator();

    await Assert.That(await coordinator.TryClaimOffloadSweepAsync(TimeSpan.FromHours(1))).IsTrue();
    await Assert.That(await coordinator.TryClaimOffloadSweepAsync(TimeSpan.FromHours(1))).IsFalse()
      .Because("one replica per window wins the watermark, as on EF Core");
  }

  [Test]
  public async Task SettledFold_FoldsIdleStreamsOnly_AndItsClaimIsOncePerWindowAsync() {
    var coordinator = _coordinator();
    var settled = Guid.NewGuid();
    var live = Guid.NewGuid();
    await _seedStreamAsync(settled, "SettledA", "SettledB");
    await _seedStreamAsync(live, "LiveA", "LiveB");
    await _executeAsync("UPDATE wh_event_store SET created_at = NOW() - INTERVAL '2 hours' WHERE stream_id = @settled", new { settled });

    var folded = await coordinator.FoldSettledApplyPathsAsync(TimeSpan.FromHours(1), 1000);

    await Assert.That(folded).IsGreaterThanOrEqualTo(1);
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_apply_fold_watermarks WHERE stream_id = @settled", new { settled }))
      .IsEqualTo(1L);
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_apply_fold_watermarks WHERE stream_id = @live", new { live }))
      .IsEqualTo(0L).Because("a stream still receiving events is not settled");

    await Assert.That(await coordinator.TryClaimSettledFoldSweepAsync(TimeSpan.FromHours(24))).IsTrue();
    await Assert.That(await coordinator.TryClaimSettledFoldSweepAsync(TimeSpan.FromHours(24))).IsFalse();
  }

  [Test]
  public async Task PruneAncientEphemeralPointers_DisabledByDefault_ThenPrunesAllButTheNewestAsync() {
    var coordinator = _coordinator();

    var disabled = await coordinator.PruneAncientEphemeralPointersAsync();
    await Assert.That(disabled.Status).IsEqualTo("disabled");
    await Assert.That(disabled.RowsPruned).IsEqualTo(0L);

    var stream = Guid.NewGuid();
    var older = Guid.NewGuid();
    var newer = Guid.NewGuid();
    await _commitAsync(older, stream, "Whizbang.Tests.DapperPrune", flags: 8);
    await _commitAsync(newer, stream, "Whizbang.Tests.DapperPrune", flags: 8);
    await _executeAsync("DELETE FROM wh_event_body WHERE event_id = ANY(@ids)", new { ids = new[] { older, newer } });
    await _executeAsync("UPDATE wh_event_store SET created_at = NOW() - INTERVAL '200 days' WHERE stream_id = @stream", new { stream });
    await _executeAsync("UPDATE wh_settings SET setting_value = 'true' WHERE setting_key = 'ephemeral_deep_maintenance_enabled'");

    var pruned = await coordinator.PruneAncientEphemeralPointersAsync();

    await Assert.That(pruned.Status).IsEqualTo("ok");
    await Assert.That(pruned.RowsPruned).IsEqualTo(1L)
      .Because("the older reaped pointer goes and the newest stays as the stream's tombstone, as on EF Core");
  }

  [Test]
  public async Task ReclassifyEventsEphemeral_ReclassifiesTheHistoryAsync() {
    var coordinator = _coordinator();
    const string eventType = "Whizbang.Tests.DapperReclassify";
    var eventId = Guid.NewGuid();
    await _commitAsync(eventId, Guid.NewGuid(), eventType, flags: 0);

    var empty = await coordinator.ReclassifyEventsEphemeralAsync([]);
    var result = await coordinator.ReclassifyEventsEphemeralAsync([eventType]);

    await Assert.That(empty).IsEqualTo(EphemeralReclassificationResult.Empty);
    await Assert.That(result.EventsReclassified).IsEqualTo(1L);
    await Assert.That(result.StreamsReclassified).IsEqualTo(1L);
    await Assert.That(result.StreamsBlocked).IsEqualTo(0L);
    await Assert.That(await _scalarAsync<int>("SELECT flags & 8 FROM wh_event_store WHERE event_id = @eventId", new { eventId }))
      .IsEqualTo(8).Because("the historical event is now stamped ephemeral");
  }

  [Test]
  public async Task SyncEphemeralTypeGrace_ReplacesTheDeclaredSetAsync() {
    var coordinator = _coordinator();

    await coordinator.SyncEphemeralTypeGraceAsync([new EphemeralTypeGrace("Whizbang.Tests.GraceA", 30), new EphemeralTypeGrace("Whizbang.Tests.GraceB", 60)]);
    await coordinator.SyncEphemeralTypeGraceAsync([new EphemeralTypeGrace("Whizbang.Tests.GraceB", 90)]);

    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_ephemeral_type_grace")).IsEqualTo(1L)
      .Because("a type no longer declared loses its override");
    await Assert.That(await _scalarAsync<int>("SELECT grace_seconds FROM wh_ephemeral_type_grace")).IsEqualTo(90);
  }

  [Test]
  public async Task CleanupLifecycleCompletions_DeletesOnlyMarkersPastTheRetentionAsync() {
    var coordinator = _coordinator();
    await _executeAsync(
      "INSERT INTO wh_lifecycle_completions (event_id, instance_id, completed_at) VALUES (gen_random_uuid(), gen_random_uuid(), NOW() - INTERVAL '10 days'), (gen_random_uuid(), gen_random_uuid(), NOW())");

    var deleted = await coordinator.CleanupLifecycleCompletionsAsync(TimeSpan.FromDays(7));

    await Assert.That(deleted).IsEqualTo(1);
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_lifecycle_completions")).IsEqualTo(1L)
      .Because("a marker inside the retention window is kept");
  }

  [Test]
  public async Task ReapExhaustedOrphanedPerspectiveRows_DeletesOnlyExhaustedRowsWhoseEventIsGoneAsync() {
    var coordinator = _coordinator();
    var instance = Guid.NewGuid();
    var stream = Guid.NewGuid();
    await _executeAsync(@"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry, partition_number, status, attempts, created_at)
      VALUES (gen_random_uuid(), @stream, 'Exhausted', gen_random_uuid(), @instance, NULL, 0, 0, 5, NOW()),
             (gen_random_uuid(), @stream, 'Fresh', gen_random_uuid(), @instance, NULL, 0, 0, 1, NOW())", new { stream, instance });

    var none = await coordinator.ReapExhaustedOrphanedPerspectiveRowsAsync(instance, [], maxAttempts: 5);
    var reaped = await coordinator.ReapExhaustedOrphanedPerspectiveRowsAsync(instance, [stream], maxAttempts: 5);

    await Assert.That(none).IsEqualTo(0);
    await Assert.That(reaped).IsEqualTo(1)
      .Because("only the row attempted the maximum number of times, whose event is absent, is disposed of");
    await Assert.That(await _scalarAsync<string>("SELECT perspective_name FROM wh_perspective_events WHERE stream_id = @stream", new { stream }))
      .IsEqualTo("Fresh");
  }

  private async Task _commitAsync(Guid eventId, Guid streamId, string eventType, int flags) =>
    await _executeAsync("SELECT commit_handler_result(@Req::jsonb)", new {
      Req = DapperStreamPrefixTruncateTests.Commit(eventId, streamId, eventType).Replace("\"Flags\": 0", $"\"Flags\": {flags}", StringComparison.Ordinal)
    });

  private async Task _seedStreamAsync(Guid stream, params string[] eventTypes) {
    for (var version = 1; version <= eventTypes.Length; version++) {
      await _executeAsync(
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at) " +
        "VALUES (gen_random_uuid(), @stream, @stream, 'TestAggregate', @type, @version, NOW())",
        new { stream, type = eventTypes[version - 1], version });
    }
  }

  private async Task _executeAsync(string sql, object? parameters = null) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.ExecuteAsync(sql, parameters);
  }

  private async Task<T> _scalarAsync<T>(string sql, object? parameters = null) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
  }
}
