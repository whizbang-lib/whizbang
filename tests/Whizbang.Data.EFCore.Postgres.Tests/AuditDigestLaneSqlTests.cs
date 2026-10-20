// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Audit records stay out of every digest (#1492). A subscriber receives an origin's audit records as the
/// children of an audit composite, classifies them as events and stores them under the origin's id, so the
/// inbox emit chain folded them into the origin's digest lane. The origin never stores the records it
/// publishes (they are transport-only there), so its lane had no matching rows: the subscriber held buckets
/// the origin could never account for, and an origin that did store an audit record (the event store
/// decorator did before #1485, the system event emitter still does) answered with rows the subscriber
/// never received. Audit is a function of the audited event, so it needs no integrity accounting: both
/// sides now leave it out of every fold, and reconciliation has nothing to compare for it.
/// </summary>
/// <docs>resilience/stream-integrity#what-is-digested</docs>
[Category("Shard2")]
public class AuditDigestLaneSqlTests : EFCoreTestBase {
  private static readonly DateTimeOffset _fixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private const string TENANT = "tenant-audit-digest";
  private const string DOMAIN_TYPE = "Probe.Contracts.DomainEventHappened, Probe.Contracts";
  private static readonly string _auditType = TypeNameFormatter.AssemblyQualifiedName(typeof(EventAudited));

  [Test]
  public async Task InboxStoredAuditRecord_LeavesTheOriginsDigestLaneUntouched_WhileADomainEventFoldsAsync() {
    await using var dbContext = CreateDbContext();
    var origin = (Guid)TrackedGuid.New();
    var audit = _auditChild(origin, sequence: 1);
    var domain = _domainChild(origin, sequence: 2);

    var conn = await _receiveAsync(dbContext, audit, domain);

    var lanes = await _bucketTypesAsync(conn, origin);
    await Assert.That(lanes).IsEquivalentTo([await _normalizedAsync(conn, DOMAIN_TYPE)])
      .Because("the domain event folds into the origin's lane as before; the audit record never does");
  }

  [Test]
  public async Task Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var origin = (Guid)TrackedGuid.New();

    // The origin's own stored audit record: the shape the event store decorator wrote before #1485 and the
    // system event emitter still writes. Same schema, so the local lane stands in for the origin service.
    await coordinator.StoreOutboxMessagesAsync([_storedAuditRecord()], partitionCount: 10000);
    // The subscriber's received audit record, from the origin's composite.
    var received = _auditChild(origin, sequence: 1);
    var conn = await _receiveAsync(dbContext, received);

    string[] types = [_auditType];
    var originAnswer = await coordinator.ComputeTypeDigestsAsync(null, types, TimeSpan.Zero);
    var originStreams = await coordinator.ComputeStreamDigestsAsync(null, types, TimeSpan.Zero);
    var subscriberFold = await coordinator.ComputeTypeDigestsAsync(origin, types, TimeSpan.Zero);
    var subscriberTable = await coordinator.GetTypeDigestsAsync(origin, types);
    var subscriberWindow = await coordinator.ComputeTypeDigestsWindowedAsync(origin, types, 0, null, TimeSpan.Zero);
    var subscriberStreams = await coordinator.ComputeStreamDigestsAsync(origin, types, TimeSpan.Zero);
    var subscriberStreamWindow = await coordinator.ComputeStreamDigestsWindowedAsync(
      origin, types, 0, null, resumeAfterStreamId: null, maxDigests: 100, TimeSpan.Zero);
    var subscriberChunk = await coordinator.ComputeStreamDigestsForChunkAsync(
      origin, [received.StreamId!.Value], null, null, TimeSpan.Zero);

    // Every fold side by side, so a failure names each one that still holds the record. An epoch seal folds
    // the same predicate as the live folds.
    var auditTypeNormalized = await _normalizedAsync(conn, _auditType);
    var folded = new {
      OriginLaneBuckets = (await _bucketTypesAsync(conn, Guid.Empty)).Count,
      OriginTypeAnswer = originAnswer.Count,
      OriginStreamAnswer = originStreams.Count,
      SubscriberTypeFold = subscriberFold.Count,
      SubscriberTypeTable = subscriberTable.Count,
      SubscriberTypeWindow = subscriberWindow!.Digests.Count,
      SubscriberStreamFold = subscriberStreams.Count,
      SubscriberStreamWindow = subscriberStreamWindow!.Digests.Count,
      SubscriberChunk = subscriberChunk!.Count,
      SubscriberEpochFold = (await _epochBucketTypesAsync(conn, origin)).Count(t => t == auditTypeNormalized),
    };
    await Assert.That(folded).IsEqualTo(new {
      OriginLaneBuckets = 0,
      OriginTypeAnswer = 0,
      OriginStreamAnswer = 0,
      SubscriberTypeFold = 0,
      SubscriberTypeTable = 0,
      SubscriberTypeWindow = 0,
      SubscriberStreamFold = 0,
      SubscriberStreamWindow = 0,
      SubscriberChunk = 0,
      SubscriberEpochFold = 0,
    }).Because("the origin answers nothing for audit and the subscriber holds nothing for it in the origin's lane, "
      + "so reconciliation has nothing to compare: no missing rows, no extra rows");
  }

  [Test]
  public async Task VerifyDigestTable_RemovesAnAuditBucketInsteadOfHealingItAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var origin = (Guid)TrackedGuid.New();
    var audit = _auditChild(origin, sequence: 1);
    var conn = await _receiveAsync(dbContext, audit);

    // A bucket an earlier release folded for this record, last touched well outside the settle window
    // (both times on the database clock).
    await using (var seed = conn.CreateCommand()) {
      seed.CommandText = """
        INSERT INTO wh_stream_digests AS d
          (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
        VALUES (@origin, @tenant, normalize_event_type(@type), @stream, 7, 9, 1, NOW() - INTERVAL '1 hour')
        ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id)
          DO UPDATE SET updated_at = NOW() - INTERVAL '1 hour'
        """;
      seed.Parameters.AddWithValue(nameof(origin), origin);
      seed.Parameters.AddWithValue("tenant", TENANT);
      seed.Parameters.AddWithValue("type", _auditType);
      seed.Parameters.AddWithValue("stream", audit.StreamId!.Value);
      await seed.ExecuteNonQueryAsync();
    }

    await coordinator.VerifyDigestTableAsync(TimeSpan.Zero);

    await Assert.That(await _bucketTypesAsync(conn, origin)).IsEmpty()
      .Because("the sweep recomputes without audit records, so a bucket an earlier release folded is removed, not kept");
  }

  // ---- the subscriber's receive path ----

  private static InboxMessage _auditChild(Guid origin, long sequence) {
    var record = new EventAudited {
      Id = (Guid)TrackedGuid.New(),
      OriginalEventId = (Guid)TrackedGuid.New(),
      OriginalEventType = "Probe.Contracts.DomainEventHappened",
      OriginalStreamId = ((Guid)TrackedGuid.New()).ToString(),
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { probe = true }),
      Timestamp = _fixedInstant,
      TenantId = TENANT,
    };
    var payload = JsonSerializer.SerializeToElement(record, JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(EventAudited)));
    return _child(origin, sequence, _auditType, record.Id, payload);
  }

  private static InboxMessage _domainChild(Guid origin, long sequence) =>
    _child(origin, sequence, DOMAIN_TYPE, (Guid)TrackedGuid.New(), JsonSerializer.SerializeToElement(new { happened = true }));

  // A composite fan-out child as CompositeInboxFanout builds it: an event, on its record's own stream,
  // named as the origin's, confined to this service.
  private static InboxMessage _child(Guid origin, long sequence, string messageType, Guid streamId, JsonElement payload) {
    var id = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(id),
      Payload = payload,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      SourceServiceId = origin,
      SourceCommitSequence = sequence,
    };
    return new InboxMessage {
      MessageId = id,
      HandlerName = "ProbeHandler",
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = messageType,
      StreamId = streamId,
      IsEvent = true,
      Flags = EventFlags.NoRebroadcast,
      Scope = new PerspectiveScope { TenantId = TENANT },
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
      SourceServiceId = origin,
      SourceCommitSequence = sequence,
    };
  }

  // An audit record event-stored at the origin itself.
  private static OutboxMessage _storedAuditRecord() =>
    CreateTestOutboxMessage((Guid)TrackedGuid.New(), AuditingEventStoreDecorator.AUDIT_TOPIC_DESTINATION, (Guid)TrackedGuid.New(), isEvent: true) with {
      MessageType = _auditType,
      Scope = new PerspectiveScope { TenantId = TENANT },
    };

  private static async Task<NpgsqlConnection> _receiveAsync(WorkCoordinationDbContext dbContext, params InboxMessage[] rows) {
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.StoreInboxMessagesAsync(rows, partitionCount: 100);

    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    // The inbox emit chain stores the rows this instance holds, as the work batch runs it.
    var instanceId = (Guid)TrackedGuid.New();
    await using (var lease = conn.CreateCommand()) {
      lease.CommandText = "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)";
      lease.Parameters.AddWithValue("inst", instanceId);
      lease.Parameters.AddWithValue("ids", rows.Select(r => r.MessageId).ToArray());
      await lease.ExecuteNonQueryAsync();
    }
    await using (var emit = conn.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      emit.Parameters.AddWithValue("inst", instanceId);
      _ = await emit.ExecuteScalarAsync();
    }
    await using (var stored = conn.CreateCommand()) {
      stored.CommandText = "SELECT count(*) FROM wh_event_store WHERE event_id = ANY(@ids)";
      stored.Parameters.AddWithValue("ids", rows.Select(r => r.MessageId).ToArray());
      if ((long)(await stored.ExecuteScalarAsync())! != rows.Length) {
        throw new InvalidOperationException("the received rows were not event-stored; the probe proves nothing");
      }
    }
    return conn;
  }

  private static async Task<List<string>> _bucketTypesAsync(NpgsqlConnection conn, Guid origin) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_type FROM wh_stream_digests WHERE origin_service_id = @origin ORDER BY 1";
    cmd.Parameters.AddWithValue(nameof(origin), origin);
    var types = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      types.Add(reader.GetString(0));
    }
    return types;
  }

  private static async Task<List<string>> _epochBucketTypesAsync(NpgsqlConnection conn, Guid origin) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT b_type FROM _wh_epoch_buckets(@origin, 0, 100000)";
    cmd.Parameters.AddWithValue(nameof(origin), origin);
    var types = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      types.Add(reader.GetString(0));
    }
    return types;
  }

  private static async Task<string> _normalizedAsync(NpgsqlConnection conn, string typeName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT normalize_event_type(@t)";
    cmd.Parameters.AddWithValue("t", typeName);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }
}
