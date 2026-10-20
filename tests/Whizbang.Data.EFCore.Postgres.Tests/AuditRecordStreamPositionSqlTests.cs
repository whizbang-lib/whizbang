// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// An audit record names the position its event was stored at (#1485). The outbox builder runs before the
/// event has a version (the store assigns it), so it writes 0 and <c>store_outbox_messages</c> fills the
/// real version in once the audited event is in <c>wh_event_store</c>: in the same write when the two travel
/// together (the scoped and immediate strategies), and in the audit record's own write when the event was
/// stored by an earlier one (the stream-affinity batcher flushes per stream).
/// </summary>
/// <docs>fundamentals/events/system-events#audit</docs>
[Category("Shard2")]
public class AuditRecordStreamPositionSqlTests : EFCoreTestBase {
  private const string TOPIC = "audit-position-topic";
  private const string TENANT = "tenant-audit-position";

  [Test]
  public async Task Store_EventAndItsAuditTogether_RecordCarriesTheEventsVersionAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var streamId = (Guid)TrackedGuid.New();

    // Two earlier events, so the audited one is not at version 1 (a constant 1 would pass otherwise).
    await coordinator.StoreOutboxMessagesAsync(
      [_event(streamId), _event(streamId)], partitionCount: 10000);
    var audited = _event(streamId);
    var audit = _audit(audited);

    await coordinator.StoreOutboxMessagesAsync([audited, audit], partitionCount: 10000);

    var conn = await _openAsync(dbContext);
    var version = await _versionAsync(conn, audited.MessageId);
    await Assert.That(version).IsEqualTo(3L);
    await Assert.That(await _recordedPositionAsync(conn, audit.MessageId)).IsEqualTo(version)
      .Because("the record must name the position its event was stored at, not the builder's placeholder");
  }

  [Test]
  public async Task Store_AuditAfterItsEventInALaterWrite_RecordCarriesTheEventsVersionAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var streamId = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_event(streamId)], partitionCount: 10000);
    var audited = _event(streamId);
    await coordinator.StoreOutboxMessagesAsync([audited], partitionCount: 10000);

    var audit = _audit(audited);
    await coordinator.StoreOutboxMessagesAsync([audit], partitionCount: 10000);

    var conn = await _openAsync(dbContext);
    await Assert.That(await _recordedPositionAsync(conn, audit.MessageId)).IsEqualTo(2L)
      .Because("a record stored after its event finds the event's version in the store it is written to");
  }

  [Test]
  public async Task Store_RecordOnTheTenantLedger_IsStoredOnTheLedgerStreamAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var audited = _event((Guid)TrackedGuid.New());
    var first = _audit(audited);
    var second = _audit(_event((Guid)TrackedGuid.New()));

    await coordinator.StoreOutboxMessagesAsync([audited, first, second], partitionCount: 10000);

    var conn = await _openAsync(dbContext);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT DISTINCT stream_id FROM wh_outbox WHERE message_id = ANY(@ids)";
    cmd.Parameters.AddWithValue("ids", new[] { first.MessageId, second.MessageId });
    var streams = new List<Guid>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        streams.Add(reader.GetGuid(0));
      }
    }
    await Assert.That(streams).IsEquivalentTo([AuditLedger.StreamFor(TENANT)])
      .Because("one tenant's audit records are one ledger stream in the outbox");
  }

  private static OutboxMessage _event(Guid streamId) =>
    CreateTestOutboxMessage((Guid)TrackedGuid.New(), TOPIC, streamId, isEvent: true) with {
      Scope = new PerspectiveScope { TenantId = TENANT }
    };

  private static OutboxMessage _audit(OutboxMessage audited) =>
    AuditOutboxMessageBuilder.TryBuildAuditMessage(audited, new SystemEventOptions().EnableEventAudit())
      ?? throw new InvalidOperationException("the probe event must be audited");

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<long> _versionAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT version FROM wh_event_store WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", eventId);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<long> _recordedPositionAsync(NpgsqlConnection conn, Guid auditMessageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT (event_data::jsonb #>> '{p,OriginalStreamPosition}')::bigint FROM wh_outbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", auditMessageId);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }
}
