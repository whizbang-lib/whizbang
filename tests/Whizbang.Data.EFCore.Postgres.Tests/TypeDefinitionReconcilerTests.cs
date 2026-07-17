using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Configuration;
using Whizbang.Core.Fingerprint;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the startup <see cref="TypeDefinitionReconciler"/> (fingerprint F-4): it
/// registers each type's current definition, detects drift against the stored fingerprint, records a
/// lineage edge, and — for settings drift toward Ephemeral, when enabled — reclassifies the type's
/// historical events. Detect-by-default, act-by-opt-in. Uses a real EFCoreWorkCoordinator against Postgres
/// with a hand-built catalog so the whole path (reconciler → coordinator SQL → fingerprint + event tables)
/// runs end-to-end.
/// </summary>
/// <docs>fundamentals/events/type-definition-fingerprint</docs>
public class TypeDefinitionReconcilerTests : EFCoreTestBase {
  private const string EventType = "Whizbang.Tests.ReconcilerDriftEvent";

  private sealed class FakeCatalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private static string _hex(char c) => new(c, 64);   // valid 64-char hex stand-in for a content hash

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static TypeDefinitionReconciler _reconciler(IWorkCoordinator coordinator, IMessageTypeCatalog catalog, bool act) =>
    new(coordinator,
        Options.Create(new EphemeralOptions { ReconcileHistoricalOnStartup = act }),
        NullLogger<TypeDefinitionReconciler>.Instance,
        catalog);

  private static MessageTypeCatalogEntry _ephemeralEntry(string settingsHash, string schemaHash) =>
    new(typeof(object), EventType, "event", null) {
      Ephemeral = new EphemeralInfo(Destruction.WhenConsumed, TransientStorage.InMemory),
      SettingsHash = settingsHash,
      SchemaHash = schemaHash,
    };

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _commitSourcedAsync(NpgsqlConnection connection, Guid eventId, Guid streamId) {
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}",
        "service_name": "test", "host_name": "test-host", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out-topic",
          "MessageType": "{{EventType}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": 0
        }]
      }
      """;
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", request);
    _ = await call.ExecuteScalarAsync();
  }

  private static async Task<int> _flagsAsync(NpgsqlConnection connection, Guid eventId) {
    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT flags FROM wh_event_store WHERE event_id = @id";
    v.Parameters.AddWithValue("id", eventId);
    return (int)(await v.ExecuteScalarAsync())!;
  }

  private static async Task<long> _lineageCountAsync(NpgsqlConnection connection, int relationship) {
    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT count(*) FROM wh_definition_lineage WHERE relationship = @r";
    v.Parameters.AddWithValue("r", (short)relationship);
    return (long)(await v.ExecuteScalarAsync())!;
  }

  [Test]
  public async Task Reconcile_SettingsDriftToEphemeral_ActEnabled_ReclassifiesAndRecordsLineageAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    // The type's PRIOR (Sourced-era) definition is already stored, and it has historical Sourced events.
    await coordinator.RegisterTypeDefinitionAsync(EventType, _hex('a'), _hex('c'), 0);
    var e1 = Guid.NewGuid();
    var e2 = Guid.NewGuid();
    await _commitSourcedAsync(connection, e1, Guid.NewGuid());
    await _commitSourcedAsync(connection, e2, Guid.NewGuid());

    // Code now declares the type Ephemeral: same schema, changed settings hash.
    var catalog = new FakeCatalog(_ephemeralEntry(settingsHash: _hex('b'), schemaHash: _hex('c')));
    var summary = await _reconciler(coordinator, catalog, act: true).ReconcileAsync();

    await Assert.That(summary.DriftDetected).IsEqualTo(1).Because("The type's definition changed since it was last stored.");
    await Assert.That(summary.TypesReclassified).IsEqualTo(1).Because("Settings drift toward Ephemeral, with act enabled, reclassifies its history.");
    await Assert.That(await _flagsAsync(connection, e1) & 8).IsEqualTo(8).Because("Historical event 1 is now stamped ephemeral.");
    await Assert.That(await _flagsAsync(connection, e2) & 8).IsEqualTo(8).Because("Historical event 2 is now stamped ephemeral.");
    await Assert.That(await _lineageCountAsync(connection, (int)DefinitionRelationship.ReclassifiedTo)).IsEqualTo(1L)
      .Because("A ReclassifiedTo lineage edge records how the definition superseded the prior one.");
  }

  [Test]
  public async Task Reconcile_SettingsDrift_DetectOnly_RecordsLineageButDoesNotReclassifyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    await coordinator.RegisterTypeDefinitionAsync(EventType, _hex('a'), _hex('c'), 0);
    var e1 = Guid.NewGuid();
    await _commitSourcedAsync(connection, e1, Guid.NewGuid());

    var catalog = new FakeCatalog(_ephemeralEntry(settingsHash: _hex('b'), schemaHash: _hex('c')));
    var summary = await _reconciler(coordinator, catalog, act: false).ReconcileAsync();

    await Assert.That(summary.DriftDetected).IsEqualTo(1).Because("Drift is always detected.");
    await Assert.That(summary.TypesReclassified).IsEqualTo(0).Because("Detect-only default does not act on the drift.");
    await Assert.That(await _flagsAsync(connection, e1) & 8).IsEqualTo(0).Because("The historical event stays Sourced — nothing was reclassified.");
    await Assert.That(await _lineageCountAsync(connection, (int)DefinitionRelationship.ReclassifiedTo)).IsEqualTo(1L)
      .Because("The lineage edge + drift report are recorded even in detect-only mode.");
  }

  [Test]
  public async Task Reconcile_SyncsPerTypeRewindGraceOverrideAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var entry = new MessageTypeCatalogEntry(typeof(object), "Whizbang.Tests.GraceSyncEvent", "event", null) {
      Ephemeral = new EphemeralInfo(Destruction.WhenConsumed, TransientStorage.InMemory, RewindGraceSeconds: 42),
      SettingsHash = new string('a', 64),
      SchemaHash = new string('c', 64),
    };
    await _reconciler(coordinator, new FakeCatalog(entry), act: false).ReconcileAsync();

    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT grace_seconds FROM wh_ephemeral_type_grace WHERE event_type = normalize_event_type('Whizbang.Tests.GraceSyncEvent')";
    await Assert.That((int)(await v.ExecuteScalarAsync())!).IsEqualTo(42)
      .Because("The startup reconciler syncs each type's [Ephemeral(RewindGraceSeconds)] override into the grace table.");
  }

  [Test]
  public async Task Reconcile_SyncsPerTypeTtlOverrideAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var entry = new MessageTypeCatalogEntry(typeof(object), "Whizbang.Tests.TtlSyncEvent", "event", null) {
      Ephemeral = new EphemeralInfo(Destruction.AfterTtl, TransientStorage.TtlRow, RewindGraceSeconds: -1, TtlSeconds: 7200),
      SettingsHash = new string('a', 64),
      SchemaHash = new string('c', 64),
    };
    await _reconciler(coordinator, new FakeCatalog(entry), act: false).ReconcileAsync();

    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT ttl_seconds FROM wh_ephemeral_type_ttl WHERE event_type = normalize_event_type('Whizbang.Tests.TtlSyncEvent')";
    await Assert.That((int)(await v.ExecuteScalarAsync())!).IsEqualTo(7200)
      .Because("The startup reconciler syncs each type's [Ephemeral(TtlSeconds)] override into the TTL lookup.");
  }

  [Test]
  public async Task Reconcile_WhenConsumedType_NotSyncedIntoTtlLookupAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    // A WhenConsumed event carries TtlSeconds = -1 (no TTL) — it must NOT get a TTL-lookup row (absence of a
    // row is precisely what distinguishes consumption-gated from age-gated).
    var entry = new MessageTypeCatalogEntry(typeof(object), "Whizbang.Tests.NoTtlEvent", "event", null) {
      Ephemeral = new EphemeralInfo(Destruction.WhenConsumed, TransientStorage.PersistedRow),
      SettingsHash = new string('a', 64),
      SchemaHash = new string('c', 64),
    };
    await _reconciler(coordinator, new FakeCatalog(entry), act: false).ReconcileAsync();

    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT count(*) FROM wh_ephemeral_type_ttl WHERE event_type = normalize_event_type('Whizbang.Tests.NoTtlEvent')";
    await Assert.That((long)(await v.ExecuteScalarAsync())!).IsEqualTo(0L)
      .Because("A WhenConsumed event has no TTL, so it is never synced into the age-based TTL lookup.");
  }

  [Test]
  public async Task Reconcile_NoChange_SameDefinition_NoDriftAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    // The stored definition matches what the code produces now.
    await coordinator.RegisterTypeDefinitionAsync(EventType, _hex('b'), _hex('c'), 0);
    var catalog = new FakeCatalog(_ephemeralEntry(settingsHash: _hex('b'), schemaHash: _hex('c')));

    var summary = await _reconciler(coordinator, catalog, act: true).ReconcileAsync();
    await Assert.That(summary.DriftDetected).IsEqualTo(0).Because("A definition that already matches is not drift.");
    await Assert.That(summary.TypesReclassified).IsEqualTo(0).Because("Nothing to reconcile.");
  }
}
