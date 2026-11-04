// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Type registry, lifecycle and cursor operations on the Dapper driver, with the contract the EF Core driver keeps: the
/// type-definition catalog, its lineage and its reconcile claim, the ephemeral drift count and snapshot targets, the
/// cursor batch and rewind reads, the local service id, and the lifecycle completion marker with the orphan
/// reconciliation it closes (#2051).
/// </summary>
/// <remarks>
/// <para>
/// This coordinator inherited the interface's inert defaults for every one of them, so on this driver the
/// type-definition reconciler saw an empty catalog, a reclassification reported no drift, no ephemeral body was ever
/// snapshotted ahead of its reap, a cursor flagged for a rewind was never rewound, the service read as having no id, and
/// no orphaned lifecycle was ever replayed. Each member now runs the statement in <c>CoordinatorCatalogSql</c> or
/// <c>OrphanedLifecycleEventsSql</c>, which the EF Core coordinator runs too.
/// </para>
/// <para>
/// Twins on the EF Core driver: <c>TypeDefinitionReconcilerTests</c>, <c>EphemeralReclassifyCoordinatorTests</c>,
/// <c>EphemeralSnapshotTargetSqlTests</c>, <c>GetPerspectiveCursorsCommitSequenceTests</c>,
/// <c>EFCoreWorkCoordinatorLifecycleAndJanitorTests</c>. Design choice documented at data/drivers#dapper-coordinator-parity.
/// </para>
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.FleetAndCatalog.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorCatalogSql.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/OrphanedLifecycleEventsSql.cs</code-under-test>
[Category("Integration")]
public class DapperLifecycleAndCatalogTests : PostgresTestBase {
  private const string EVENT_TYPE = "Whizbang.Tests.DapperLifecycleEvent";
  private const string PERSPECTIVE = "Whizbang.Tests.DapperLifecycleProjection";

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Deliberate: the interface is what the workers call through, so an unimplemented member reaches its default here as it does there.")]
  private IWorkCoordinator _coordinator() =>
    new DapperWorkCoordinator(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task TypeDefinitions_RegisterReadRelateAndClaimTheReconcileAsync() {
    var coordinator = _coordinator();

    var first = await coordinator.RegisterTypeDefinitionAsync(EVENT_TYPE, "aa", "bb", 1);
    var same = await coordinator.RegisterTypeDefinitionAsync(EVENT_TYPE, "aa", "bb", 1);
    var changed = await coordinator.RegisterTypeDefinitionAsync(EVENT_TYPE, "aa", "cc", 2);
    await coordinator.RecordDefinitionLineageAsync(first.DefinitionId, changed.DefinitionId, DefinitionRelationship.SchemaUpgradedTo, "migration-1");
    var catalog = await coordinator.GetTypeDefinitionsAsync();

    await Assert.That(first.IsNew).IsTrue();
    await Assert.That(same.IsNew).IsFalse().Because("the same fingerprints resolve to the definition already registered");
    await Assert.That(same.DefinitionId).IsEqualTo(first.DefinitionId);
    await Assert.That(changed.IsNew).IsTrue();
    await Assert.That(changed.PreviousDefinitionId).IsEqualTo(first.DefinitionId);
    await Assert.That(catalog.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(catalog.Single(d => d.DefinitionId == changed.DefinitionId).SchemaHashHex).IsEqualTo("cc");
    await Assert.That(await _scalarAsync<long>(
        "SELECT count(*) FROM wh_definition_lineage WHERE from_definition_id = @from AND to_definition_id = @to AND migration_ref = 'migration-1'",
        new { from = first.DefinitionId, to = changed.DefinitionId }))
      .IsEqualTo(1L);
    await Assert.That(await coordinator.TryClaimTypeDefinitionReconcileAsync(TimeSpan.FromHours(1))).IsTrue();
    await Assert.That(await coordinator.TryClaimTypeDefinitionReconcileAsync(TimeSpan.FromHours(1))).IsFalse();
  }

  [Test]
  public async Task CountSourcedEventsForTypes_CountsOnlyTheNotYetEphemeralHistoryAsync() {
    var coordinator = _coordinator();
    await _commitAsync(Guid.NewGuid(), Guid.NewGuid(), EVENT_TYPE, flags: 0);
    await _commitAsync(Guid.NewGuid(), Guid.NewGuid(), EVENT_TYPE, flags: 0);
    await _commitAsync(Guid.NewGuid(), Guid.NewGuid(), EVENT_TYPE, flags: 8);

    await Assert.That(await coordinator.CountSourcedEventsForTypesAsync([EVENT_TYPE])).IsEqualTo(2L);
    await Assert.That(await coordinator.CountSourcedEventsForTypesAsync([])).IsEqualTo(0L);
  }

  [Test]
  public async Task EphemeralPairsNeedingSnapshot_NamesAConsumedBodyPastItsGraceThatNoSnapshotCoversAsync() {
    var coordinator = _coordinator();
    var stream = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    await _commitAsync(eventId, stream, EVENT_TYPE, flags: 8);
    await _executeAsync("DELETE FROM wh_perspective_events WHERE stream_id = @stream", new { stream });
    await _executeAsync("UPDATE wh_event_store SET created_at = NOW() - INTERVAL '1 hour' WHERE event_id = @eventId", new { eventId });
    await _associateAsync();
    await _cursorAsync(stream, eventId, status: 0);

    var targets = await coordinator.GetEphemeralPairsNeedingSnapshotAsync();

    await Assert.That(targets).IsEquivalentTo([new EphemeralSnapshotTarget(stream, PERSPECTIVE, eventId)])
      .Because("the body is ephemeral, consumed, past its grace, and no snapshot covers its version");
  }

  [Test]
  public async Task Cursors_AreReadInABatch_AndThoseFlaggedForARewindAreListedAsync() {
    var coordinator = _coordinator();
    var stream = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    await _commitAsync(eventId, stream, EVENT_TYPE, flags: 0);
    await _cursorAsync(stream, eventId, status: 32, trigger: eventId);

    var none = await coordinator.GetPerspectiveCursorsBatchAsync([]);
    var batch = await coordinator.GetPerspectiveCursorsBatchAsync([stream]);
    var rewinds = await coordinator.GetCursorsRequiringRewindAsync();

    await Assert.That(none).IsEmpty();
    await Assert.That(batch.Count).IsEqualTo(1);
    await Assert.That(batch[0].PerspectiveName).IsEqualTo(PERSPECTIVE);
    await Assert.That(batch[0].LastEventId).IsEqualTo(eventId);
    await Assert.That(batch[0].LastCommitSequence.HasValue).IsTrue()
      .Because("the commit sequence of the cursor's last event warms the cursor cache's inversion check");
    await Assert.That(rewinds).IsEquivalentTo([new RewindCursorInfo(stream, PERSPECTIVE, eventId, eventId)]);
  }

  [Test]
  public async Task GetLocalServiceId_ReadsTheSeededServiceIdentityAsync() {
    var id = await _coordinator().GetLocalServiceIdAsync();

    await Assert.That(id).IsNotEqualTo(Guid.Empty);
    await Assert.That(id).IsEqualTo(await _scalarAsync<Guid>("SELECT service_id FROM wh_service_config"));
  }

  [Test]
  public async Task OrphanedLifecycles_AreFoundUntilTheirCompletionIsRecordedAsync() {
    var coordinator = _coordinator();
    var stream = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    await _commitAsync(eventId, stream, EVENT_TYPE, flags: 0);
    await _cursorAsync(stream, eventId, status: 0);
    var expected = new Dictionary<string, IReadOnlyList<string>> { [EVENT_TYPE] = [PERSPECTIVE] };

    var nothingExpected = await coordinator.GetOrphanedLifecycleEventsAsync([], TimeSpan.FromHours(1));
    var orphaned = await coordinator.GetOrphanedLifecycleEventsAsync(expected, TimeSpan.FromHours(1));
    await coordinator.RecordLifecycleCompletionAsync(eventId);
    await coordinator.RecordLifecycleCompletionAsync(eventId);
    var afterCompletion = await coordinator.GetOrphanedLifecycleEventsAsync(expected, TimeSpan.FromHours(1));

    await Assert.That(nothingExpected).IsEmpty();
    await Assert.That(orphaned.Count).IsEqualTo(1)
      .Because("every expected perspective has a cursor past the event and no completion was recorded");
    await Assert.That(orphaned[0].EventId).IsEqualTo(eventId);
    await Assert.That(orphaned[0].StreamId).IsEqualTo(stream);
    await Assert.That(((Whizbang.Core.Observability.MessageEnvelope<JsonElement>)orphaned[0].Envelope).Payload.GetProperty("OrderId").GetInt32())
      .IsEqualTo(42).Because("the replayed envelope carries the stored payload");
    await Assert.That(afterCompletion).IsEmpty().Because("a recorded completion closes the lifecycle");
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_lifecycle_completions WHERE event_id = @eventId", new { eventId }))
      .IsEqualTo(1L).Because("recording a completion twice keeps one marker");
  }

  [Test]
  public async Task OrphanedLifecycles_AFailedQuery_AnswersNothingRatherThanStoppingTheHostAsync() {
    var coordinator = _coordinator();
    await _executeAsync("ALTER TABLE wh_lifecycle_completions RENAME TO wh_lifecycle_completions_gone");

    var orphaned = await coordinator.GetOrphanedLifecycleEventsAsync(
      new Dictionary<string, IReadOnlyList<string>> { [EVENT_TYPE] = [PERSPECTIVE] }, TimeSpan.FromHours(1));

    await Assert.That(orphaned).IsEmpty();
  }

  private Task _commitAsync(Guid eventId, Guid streamId, string eventType, int flags) =>
    _executeAsync("SELECT commit_handler_result(@Req::jsonb)", new {
      Req = DapperStreamPrefixTruncateTests.Commit(eventId, streamId, eventType).Replace("\"Flags\": 0", $"\"Flags\": {flags}", StringComparison.Ordinal)
    });

  private Task _associateAsync() =>
    _executeAsync(@"
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @EVENT_TYPE, 'perspective', @PERSPECTIVE, 'test-service', @EVENT_TYPE, NOW(), NOW())
      ON CONFLICT DO NOTHING", new { EVENT_TYPE, PERSPECTIVE });

  private Task _cursorAsync(Guid stream, Guid lastEvent, int status, Guid? trigger = null) =>
    _executeAsync(@"
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at, rewind_trigger_event_id)
      VALUES (@stream, @PERSPECTIVE, @lastEvent, @status, NOW(), @trigger::uuid)", new { stream, PERSPECTIVE, lastEvent, status, trigger });

  private async Task _executeAsync(string sql, object? parameters = null) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.ExecuteAsync(sql, parameters);
  }

  private async Task<T> _scalarAsync<T>(string sql, object? parameters = null) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
  }
}
