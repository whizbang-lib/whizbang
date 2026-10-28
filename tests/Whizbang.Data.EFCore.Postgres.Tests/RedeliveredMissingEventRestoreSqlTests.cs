// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A redelivery that stream integrity asked for stores an event that inbox deduplication remembers but the event store
/// lost (#1843), against the real <c>store_inbox_messages</c> and emit chain. Deduplication alone dropped such an event
/// on every redelivery, so the bucket never healed and repair asked for it forever. For repair traffic the store now
/// checks deduplication against the event store: an event it does not hold (and the inbox does not hold either) is
/// stored again, state only. A duplicate of an event it does hold is still dropped, ordinary traffic keeps trusting
/// deduplication, and an event on a stream whose earlier history was truncated here is refused and reported.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/252_RepairRestoresWhatDeduplicationRemembers.sql</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/CompositeInboxFanout.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#remembered-but-missing</docs>
[Category("Integration")]
[Category("Shard5")]
public class RedeliveredMissingEventRestoreSqlTests : EFCoreTestBase {
  private const string MESSAGE_TYPE = "Upstream.Contracts.OrderPlaced, Upstream.Contracts";
  private const string REFUSED_TRUNCATED = "repair refused: stream prefix truncated";

  [Test]
  public async Task ARepairedEvent_DeduplicationRemembers_TheStoreLost_IsRestored_AndTheBucketHealsAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerInstanceAsync(conn, instance);
    var kept = await _storeEventAsync(conn, stream, version: 1, origin, originSequence: 1);
    var lost = TrackedGuid.New().Value;
    await _rememberAsync(conn, kept);
    await _rememberAsync(conn, lost);

    await _storeAsync(_message(kept, stream, origin, 1) with { RestoreIfMissing = true },
      _message(lost, stream, origin, 2) with { RestoreIfMissing = true });
    var presence = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [kept, lost], [1, 2]);
    await _leaseAsync(conn, instance, lost);
    await _emitInboxChainAsync(conn, instance);

    await Assert.That(await _inboxRowExistsAsync(conn, kept)).IsFalse()
      .Because("the event the store holds is still a duplicate, so the redelivered copy is dropped");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT EXISTS (SELECT 1 FROM wh_event_store WHERE event_id = $1 AND origin_service_id = $2)", lost, origin))
      .IsTrue().Because("the event deduplication remembered but the store lost is stored again, under its origin");
    await Assert.That(await _scalarAsync<int>(conn, "SELECT observation_count FROM wh_message_deduplication WHERE message_id = $1", lost))
      .IsEqualTo(2).Because("the redelivery is still counted as an observation");
    await Assert.That(presence).IsEquivalentTo([new RedeliveredBucketPresence(stream, null, await _normalizedTypeAsync(conn), Present: 1, Adopted: 0)])
      .Because("one child was present and the other is on its way to the store: nothing refused, repair is working");
    await Assert.That(await _bucketMatchesAsync(conn, origin, stream, [kept, lost])).IsTrue()
      .Because("the origin's digest bucket now folds both events, so the next comparison finds it whole and the bucket heals");
  }

  [Test]
  public async Task ARestoredEvent_IsStoredStateOnly_SoTriggerReceptorsThatRanOnItsFirstArrivalDoNotRunAgainAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var lost = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    await _rememberAsync(conn, lost);

    await _storeAsync(_message(lost, stream, origin, 2) with { RestoreIfMissing = true });

    var stored = await _scalarAsync<string>(conn, "SELECT event_data::text FROM wh_inbox WHERE message_id = $1", lost);
    var envelope = (MessageEnvelope<JsonElement>)JsonSerializer.Deserialize(
      stored, JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(MessageEnvelope<JsonElement>)))!;
    await Assert.That(envelope.StateOnly).IsTrue()
      .Because("deduplication remembers the message, so its reactions had their chance; repair restores state only");
    await Assert.That(envelope.MessageId.Value).IsEqualTo(lost);
  }

  [Test]
  public async Task ADuplicateOfAStoredEvent_IsStillDropped_EvenAsRepairTrafficAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var stored = await _storeEventAsync(conn, stream, version: 1, origin, originSequence: 1);
    await _rememberAsync(conn, stored);

    await _storeAsync(_message(stored, stream, origin, 1) with { RestoreIfMissing = true });

    await Assert.That(await _inboxRowExistsAsync(conn, stored)).IsFalse()
      .Because("deduplication is checked against the event store, and the store holds this event");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_event_store WHERE event_id = $1", stored)).IsEqualTo(1L);
  }

  [Test]
  public async Task OrdinaryTraffic_DeduplicationRemembers_TheStoreLost_IsDroppedAsBeforeAsync() {
    var origin = TrackedGuid.New().Value;
    var lost = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    await _rememberAsync(conn, lost);

    await _storeAsync(_message(lost, TrackedGuid.New().Value, origin, 2));

    await Assert.That(await _inboxRowExistsAsync(conn, lost)).IsFalse()
      .Because("only repair traffic checks deduplication against the event store; a live redelivery is a duplicate");
  }

  [Test]
  public async Task ARepairedEvent_StillPendingInTheInbox_IsNotStoredTwiceAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var pending = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    await _storeAsync(_message(pending, stream, origin, 2));

    await _storeAsync(_message(pending, stream, origin, 2) with { RestoreIfMissing = true });

    await Assert.That(await _scalarAsync<bool>(conn, "SELECT COALESCE((event_data ->> 'sto')::boolean, false) FROM wh_inbox WHERE message_id = $1", pending))
      .IsFalse().Because("the inbox already holds the event and stores it on the next claim; the first arrival stands untouched");
    await Assert.That(await _scalarAsync<int>(conn, "SELECT observation_count FROM wh_message_deduplication WHERE message_id = $1", pending))
      .IsEqualTo(2);
  }

  [Test]
  public async Task ARepairedEphemeralEvent_IsNotRestoredAsync() {
    var lost = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    await _rememberAsync(conn, lost);

    await _storeAsync(_message(lost, TrackedGuid.New().Value, TrackedGuid.New().Value, 2) with {
      RestoreIfMissing = true,
      Flags = EventFlags.Ephemeral,
    });

    await Assert.That(await _inboxRowExistsAsync(conn, lost)).IsFalse()
      .Because("an ephemeral event leaves the store by design and integrity never counts it, so its absence is no gap");
  }

  [Test]
  public async Task ARepairedEvent_OnAStreamWhoseEarlierHistoryWasTruncated_IsRefused_AndReportedAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerInstanceAsync(conn, instance);
    // Versions 1 and 2 were truncated; the survivor above the truncation point is all the stream holds.
    _ = await _storeEventAsync(conn, stream, version: 3, origin, originSequence: 3);
    var truncated = TrackedGuid.New().Value;
    await _rememberAsync(conn, truncated);

    await _storeAsync(_message(truncated, stream, origin, 1) with { RestoreIfMissing = true });
    var presence = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [truncated], [1]);
    await _leaseAsync(conn, instance, truncated);
    await _emitInboxChainAsync(conn, instance);

    await Assert.That(await _scalarAsync<bool>(conn, "SELECT EXISTS (SELECT 1 FROM wh_event_store WHERE event_id = $1)", truncated))
      .IsFalse().Because("restoring it could bring back an event the stream's truncation removed on purpose");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT error FROM wh_inbox_state WHERE message_id = $1 AND processed_at IS NOT NULL", truncated))
      .IsEqualTo(REFUSED_TRUNCATED).Because("the refusal is stored settled, with the reason, so it is durable and inspectable");
    await Assert.That(presence).IsEquivalentTo([new RedeliveredBucketPresence(stream, null, await _normalizedTypeAsync(conn), Present: 0, Adopted: 0, RefusedTruncated: 1)])
      .Because("the reconciliation hears about the refusal, so the bucket is reported once as one repair cannot change");
  }

  [Test]
  public async Task ARedeliveryBundle_FannedOutAndStored_RestoresTheEventDeduplicationRememberedAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var lost = TrackedGuid.New().Value;
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerInstanceAsync(conn, instance);
    await _rememberAsync(conn, lost);
    var bundle = new RedeliveryComposite {
      StreamId = stream,
      OriginServiceId = origin,
      InnerPayloads = [JsonElement.Parse("""{"reference":"R-1"}""")],
      InnerTypeNames = [MESSAGE_TYPE],
      InnerEventIds = [lost],
      InnerStreamIds = [stream],
      InnerCommitSequences = [5],
    };
    await using var services = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()))
      .BuildServiceProvider();

    var fanout = CompositeInboxFanout.TryExpand(bundle, _envelope(TrackedGuid.New().Value), services);
    await _storeAsync([.. fanout.Children]);
    await _leaseAsync(conn, instance, lost);
    await _emitInboxChainAsync(conn, instance);

    await Assert.That(fanout.Children.Single().RestoreIfMissing).IsTrue()
      .Because("a redelivery bundle's children are repair traffic");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT EXISTS (SELECT 1 FROM wh_event_store WHERE event_id = $1 AND origin_service_id = $2)", lost, origin))
      .IsTrue().Because("the bundle's child reaches the event store although deduplication had seen its id");
  }

  // ===== Helpers =====

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static MessageEnvelope<JsonElement> _envelope(Guid messageId) => new() {
    MessageId = MessageId.From(messageId),
    Payload = JsonElement.Parse("""{"reference":"R-1"}"""),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Outbox },
  };

  private static InboxMessage _message(Guid messageId, Guid streamId, Guid origin, long sequence) => new() {
    MessageId = messageId,
    HandlerName = "OrderPlacedHandler",
    Envelope = _envelope(messageId),
    EnvelopeType = EnvelopeTypeNameHelper.Format(MESSAGE_TYPE),
    StreamId = streamId,
    IsEvent = true,
    Flags = EventFlags.None,
    Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
    MessageType = MESSAGE_TYPE,
    SourceServiceId = origin,
    SourceCommitSequence = sequence,
    Priority = 250,
  };

  private async Task _storeAsync(params InboxMessage[] messages) =>
    await _coordinator().StoreInboxMessagesAsync(messages, partitionCount: 100);

  /// <summary>An event stored as the emit chain stores a received one, its hash folded into its origin's digest lane.</summary>
  private static async Task<Guid> _storeEventAsync(NpgsqlConnection conn, Guid stream, int version, Guid origin, long originSequence) {
    var eventId = TrackedGuid.New().Value;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      WITH stored AS (
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, flags, created_at,
           origin_service_id, origin_commit_sequence)
        VALUES ($1, $2, $2, 'Upstream', normalize_event_type($3), $4, nextval('wh_commit_seq'), 0, NOW(), $5, $6)
        RETURNING event_id, event_type
      )
      INSERT INTO wh_stream_digests (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count)
      SELECT $5, '', s.event_type, $2, hashtextextended(s.event_id::text, 0), hashtextextended(s.event_id::text, 1), 1
      FROM stored s
      """;
    cmd.Parameters.Add(new NpgsqlParameter { Value = eventId });
    cmd.Parameters.Add(new NpgsqlParameter { Value = stream });
    cmd.Parameters.Add(new NpgsqlParameter { Value = MESSAGE_TYPE });
    cmd.Parameters.Add(new NpgsqlParameter { Value = version });
    cmd.Parameters.Add(new NpgsqlParameter { Value = origin });
    cmd.Parameters.Add(new NpgsqlParameter { Value = originSequence });
    await cmd.ExecuteNonQueryAsync();
    return eventId;
  }

  /// <summary>The deduplication entry a first arrival leaves behind.</summary>
  private static async Task _rememberAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO wh_message_deduplication (message_id, first_seen_at, observation_count) VALUES ($1, NOW(), 1)";
    cmd.Parameters.Add(new NpgsqlParameter { Value = messageId });
    await cmd.ExecuteNonQueryAsync();
  }

  private static Task<string> _normalizedTypeAsync(NpgsqlConnection conn) =>
    _scalarAsync<string>(conn, "SELECT normalize_event_type($1)", MESSAGE_TYPE);

  /// <summary>Whether the origin lane's digest bucket is exactly the fold of the given events.</summary>
  private static Task<bool> _bucketMatchesAsync(NpgsqlConnection conn, Guid origin, Guid stream, Guid[] events) =>
    _scalarAsync<bool>(conn, """
      SELECT EXISTS (
        SELECT 1
        FROM wh_stream_digests d,
             (SELECT bit_xor(hashtextextended(e::text, 0)) AS lo, bit_xor(hashtextextended(e::text, 1)) AS hi, count(*)::int AS n
              FROM unnest($3::uuid[]) AS e) x
        WHERE d.origin_service_id = $1 AND d.stream_id = $2 AND d.scope_tenant = ''
          AND d.digest_lo = x.lo AND d.digest_hi = x.hi AND d.event_count = x.n)
      """, origin, stream, events);

  private static async Task<bool> _inboxRowExistsAsync(NpgsqlConnection conn, Guid messageId) =>
    await _scalarAsync<bool>(conn, "SELECT EXISTS (SELECT 1 FROM wh_inbox WHERE message_id = $1)", messageId);

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES ($1, 'repair-svc', 'repair-host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    cmd.Parameters.Add(new NpgsqlParameter { Value = instanceId });
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Leases a row to the instance, as a claim does before its emit chain runs.</summary>
  private static async Task _leaseAsync(NpgsqlConnection conn, Guid instanceId, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      UPDATE wh_inbox_state
      SET instance_id = $1, lease_expiry = NOW() + INTERVAL '5 minutes', attempts = attempts + 1
      WHERE message_id = $2
      """;
    cmd.Parameters.Add(new NpgsqlParameter { Value = instanceId });
    cmd.Parameters.Add(new NpgsqlParameter { Value = messageId });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _emitInboxChainAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT _emit_event_store_chain_for_inbox($1, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    cmd.Parameters.Add(new NpgsqlParameter { Value = instanceId });
    await cmd.ExecuteScalarAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(arg is Guid[] ids
        ? new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid }
        : new NpgsqlParameter { Value = arg });
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }
}
