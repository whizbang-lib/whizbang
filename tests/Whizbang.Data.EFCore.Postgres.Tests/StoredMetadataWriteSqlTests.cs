// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Every write path (the outbox and inbox emit chains and <c>wh_append_event</c>, which both direct appends call) keeps
/// the slim form in the row with its route id and, under the default Linked form, stages the full form; a listed type,
/// debug mode and the Full form keep the row full; the Slim form stages nothing (#1725, migration 285).
/// </summary>
/// <docs>data/event-store#stored-metadata</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/285_StoredMetadataIsSlimAndStaged.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StoredMetadataWriteSqlTests : EFCoreTestBase {
  private const string ORDER_PLACED = "MyApp.Orders.OrderPlaced, MyApp";
  private static readonly DateTimeOffset _t0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
  private static readonly string[] _slimKeys = ["Hops", "MessageId", "ephemeral_expires_at", "rid"];
  private static readonly string[] _slimHopKeys = ["ca", "co", "sc", "st", "tp", "ts"];

  // ===== The default: slim row, route id, staged full form, on every path =====

  [Test]
  public async Task OutboxChain_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync() {
    await using var conn = await _openAsync();
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };

    var id = (await _emitOutboxAsync(conn, _t0, ttlSeconds: 60, (ORDER_PLACED, hops)))[0];

    await _assertSlimAndStagedAsync(conn, id, ORDER_PLACED, hops, expectExpiry: true);
  }

  [Test]
  public async Task InboxChain_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync() {
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Billing) };
    var stream = (Guid)TrackedGuid.New();
    await using var dbContext = CreateDbContext();
    var row = _inboxEvent(stream, hops);

    await using var conn = await _receiveAsync(dbContext, _t0, row);

    await _assertSlimAndStagedAsync(conn, row.MessageId, ORDER_PLACED, hops, expectExpiry: false);
  }

  [Test]
  public async Task DirectAppend_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync() {
    await using var conn = await _openAsync();
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };
    var id = (Guid)TrackedGuid.New();

    await _appendDirectAsync(conn, id, ORDER_PLACED, $$"""{"MessageId":"{{id}}","Hops":{{StoredMetadataCorpus.HopsJson(hops)}}}""");

    await _assertSlimAndStagedAsync(conn, id, ORDER_PLACED, hops, expectExpiry: false);
  }

  [Test]
  public async Task DirectAppend_KeysTheSlimFormDoesNotReduce_AreKeptAsWrittenAsync() {
    await using var conn = await _openAsync();
    var hops = StoredMetadataCorpus.HopsJson([StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);
    var plain = (Guid)TrackedGuid.New();
    var dapperForm = (Guid)TrackedGuid.New();

    await _appendDirectAsync(conn, plain, ORDER_PLACED, """{"k":"v","dc":{"m":1},"rin":[]}""");
    await _appendDirectAsync(conn, dapperForm, ORDER_PLACED,
      $$"""{"message_id":"{{dapperForm}}","correlation_id":"c","causation_id":"","pri":7,"hops":{{hops}}}""");

    using var row = JsonDocument.Parse(await _bodyMetaAsync(conn, plain));
    await Assert.That(row.RootElement.GetProperty("k").GetString()).IsEqualTo("v").Because("a key the slim form does not reduce is kept as written");
    await Assert.That(row.RootElement.TryGetProperty("dc", out _)).IsFalse().Because("the dispatch context is dropped, as ToSlim drops it");
    await Assert.That(row.RootElement.TryGetProperty("rin", out _)).IsFalse().Because("so are receptor invocations");
    await Assert.That(row.RootElement.TryGetProperty("Hops", out _)).IsFalse().Because("no hop list is added where none was written");

    using var dapper = JsonDocument.Parse(await _bodyMetaAsync(conn, dapperForm));
    await Assert.That(dapper.RootElement.GetProperty("message_id").GetString()).IsEqualTo(dapperForm.ToString())
      .Because("the Dapper store reads its own keys back, so they are kept");
    await Assert.That(dapper.RootElement.GetProperty("pri").GetInt32()).IsEqualTo(7);
    var slimHop = dapper.RootElement.GetProperty("hops").EnumerateArray().Single();
    await Assert.That(slimHop.TryGetProperty("si", out _)).IsFalse().Because("the Dapper form's hop list, hops, is reduced too");
    var (_, _, stagedPlainMetadata) = await _stagedAsync(conn, plain) ?? throw new InvalidOperationException("the plain form was not staged");
    using var staged = JsonDocument.Parse(stagedPlainMetadata);
    await Assert.That(staged.RootElement.GetProperty("dc").GetProperty("m").GetInt32()).IsEqualTo(1)
      .Because("the staged form is the metadata exactly as it was written");
  }

  [Test]
  public async Task EFCoreEventStore_Append_StoresTheSlimRow_AndStagesTheFullFormAsync() {
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(TrackedGuid.New()),
      Payload = JsonSerializer.SerializeToElement(new { amount = 5 }),
      Hops = hops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    await using (var context = CreateDbContext()) {
      var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
      await store.AppendAsync((Guid)TrackedGuid.New(), envelope);
    }

    await using var conn = await _openAsync();
    await _assertSlimAndStagedAsync(conn, envelope.MessageId.Value, TypeNameFormatter.Format(typeof(JsonElement)), hops, expectExpiry: false,
      exactKeys: false);
  }

  // ===== What keeps a row full =====

  [Test]
  public async Task FullListedType_KeepsTheFullRow_ByTypeAndByNamespaceAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, "metadata_full_for", "MyApp.Payments\n  MyApp.Orders.OrderPlaced  \n\n");
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };

    var ids = await _emitOutboxAsync(conn, _t0, ttlSeconds: null,
      (ORDER_PLACED, hops),
      ("MyApp.Payments.Captured, MyApp", hops),
      ("MyApp.Payments.Refunds.Issued, MyApp", hops),
      ("MyApp.Orders.OrderPlaced+Line, MyApp", hops),
      ("MyApp.Orders.OrderPlacedLater, MyApp", hops),
      ("MyApp.PaymentsArchive.Moved, MyApp", hops));

    await Assert.That(await _isFullAsync(conn, ids[0])).IsTrue().Because("an exact type name is kept full");
    await Assert.That(await _isFullAsync(conn, ids[1])).IsTrue().Because("a namespace keeps every type in it full");
    await Assert.That(await _isFullAsync(conn, ids[2])).IsTrue().Because("and every type in a namespace below it");
    await Assert.That(await _isFullAsync(conn, ids[3])).IsTrue().Because("a listed type keeps its nested types full");
    await Assert.That(await _isFullAsync(conn, ids[4])).IsFalse().Because("a name that merely starts with a listed type is another type");
    await Assert.That(await _isFullAsync(conn, ids[5])).IsFalse().Because("a namespace that merely starts with a listed one is another namespace");
    await Assert.That((await _stagedAsync(conn, ids[0])).HasValue).IsTrue().Because("a full row is still staged under Linked, so its route is distilled");
  }

  [Test]
  public async Task DebugMode_KeepsEveryRowFullAsync() {
    await using var conn = await _openAsync();
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };
    await _settingAsync(conn, "debug_mode", "false");
    var control = (await _emitOutboxAsync(conn, _t0, ttlSeconds: null, (ORDER_PLACED, hops)))[0];

    await _settingAsync(conn, "debug_mode", "true");
    var debug = (await _emitOutboxAsync(conn, _t0, ttlSeconds: null, (ORDER_PLACED, hops)))[0];

    await Assert.That(await _isFullAsync(conn, control)).IsFalse().Because("control: with debug mode off the row is slim");
    await Assert.That(await _isFullAsync(conn, debug)).IsTrue().Because("debug mode keeps every row full");
    using var row = JsonDocument.Parse(await _bodyMetaAsync(conn, debug));
    await Assert.That(row.RootElement.GetProperty("rid").GetGuid()).IsEqualTo(RouteNormalizer.RouteId(ORDER_PLACED, hops))
      .Because("a full row carries its route id too");
  }

  [Test]
  public async Task WriteFormFull_KeepsFullRows_AndStagesNothingAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, "metadata_write_form", "Full");
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };

    var outbox = (await _emitOutboxAsync(conn, _t0, ttlSeconds: null, (ORDER_PLACED, hops)))[0];
    var direct = (Guid)TrackedGuid.New();
    await _appendDirectAsync(conn, direct, ORDER_PLACED, $$"""{"MessageId":"{{direct}}","Hops":{{StoredMetadataCorpus.HopsJson(hops)}}}""");

    await Assert.That(await _isFullAsync(conn, outbox)).IsTrue();
    await Assert.That(await _isFullAsync(conn, direct)).IsTrue();
    await Assert.That(await _stagedCountAsync(conn)).IsEqualTo(0L).Because("the Full form stages nothing: the row holds it");
  }

  [Test]
  public async Task WriteFormSlim_StoresSlimRows_AndStagesNothingAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, "metadata_write_form", "Slim");
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };

    var outbox = (await _emitOutboxAsync(conn, _t0, ttlSeconds: null, (ORDER_PLACED, hops)))[0];
    var direct = (Guid)TrackedGuid.New();
    await _appendDirectAsync(conn, direct, ORDER_PLACED, $$"""{"MessageId":"{{direct}}","Hops":{{StoredMetadataCorpus.HopsJson(hops)}}}""");

    await Assert.That(await _isFullAsync(conn, outbox)).IsFalse();
    await Assert.That(await _isFullAsync(conn, direct)).IsFalse();
    await Assert.That(await _stagedCountAsync(conn)).IsEqualTo(0L).Because("the Slim form keeps no full form anywhere");
  }

  [Test]
  public async Task MissingSettings_BehaveAsTheDefaultAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, "DELETE FROM wh_settings WHERE setting_key IN ('metadata_write_form', 'metadata_full_for', 'debug_mode')");
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };

    var id = (await _emitOutboxAsync(conn, _t0, ttlSeconds: null, (ORDER_PLACED, hops)))[0];

    await Assert.That(await _isFullAsync(conn, id)).IsFalse().Because("no row reads as Linked: the row is slim");
    await Assert.That((await _stagedAsync(conn, id)).HasValue).IsTrue().Because("and the full form is staged");
  }

  [Test]
  public async Task UnreadableSettings_BehaveAsTheDefault_AndNeverFailTheWriteAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, "metadata_write_form", "sideways");
    await _settingAsync(conn, "debug_mode", "maybe");
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };

    var unreadable = (await _emitOutboxAsync(conn, _t0, ttlSeconds: null, (ORDER_PLACED, hops)))[0];
    await _settingAsync(conn, "metadata_write_form", " full ");
    var lowerCase = (await _emitOutboxAsync(conn, _t0, ttlSeconds: null, (ORDER_PLACED, hops)))[0];

    await Assert.That(await _isFullAsync(conn, unreadable)).IsFalse().Because("an unknown form reads as Linked and a non-boolean debug flag as off");
    await Assert.That((await _stagedAsync(conn, unreadable)).HasValue).IsTrue();
    await Assert.That(await _isFullAsync(conn, lowerCase)).IsTrue().Because("the form is read without case or surrounding spaces");
  }

  // ===== Helpers =====

  private static async Task _assertSlimAndStagedAsync(NpgsqlConnection conn, Guid id, string eventType, List<MessageHop> hops, bool expectExpiry,
      bool exactKeys = true) {
    var routeId = RouteNormalizer.RouteId(eventType, hops);
    using var row = JsonDocument.Parse(await _bodyMetaAsync(conn, id));
    var keys = row.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
    if (exactKeys) {
      await Assert.That(keys).IsEquivalentTo(expectExpiry ? _slimKeys : _slimKeys.Where(k => k != "ephemeral_expires_at"))
        .Because("the row keeps the message id, the slim hop, the ephemeral expiry when there is one, and the route id");
    } else {
      await Assert.That(keys.Except(["ett"], StringComparer.Ordinal)).IsEquivalentTo(_slimKeys.Where(k => k != "ephemeral_expires_at"))
        .Because("a driver's own form keeps its keys (an ephemeral TTL among them), never the dispatch context or receptor invocations");
    }
    var hopKeys = row.RootElement.GetProperty("Hops").EnumerateArray().Single().EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
    await Assert.That(hopKeys).IsEquivalentTo(_slimHopKeys).Because("one hop with what readers take from a stored event");
    await Assert.That(row.RootElement.GetProperty("rid").GetGuid()).IsEqualTo(routeId).Because("the route id is stamped at write time");

    var slim = StoredMetadataCorpus.Read(row.RootElement.GetRawText());
    await Assert.That(slim.Hops.Single().TraceParent).IsEqualTo(hops[0].TraceParent).Because("perspective spans keep the event's trace");
    await Assert.That(slim.Hops.Single().CorrelationId).IsEqualTo(hops[0].CorrelationId);

    var stagedRow = await _stagedAsync(conn, id);
    await Assert.That(stagedRow.HasValue).IsTrue().Because("the Linked form stages the full form");
    var (stagedRouteId, stagedEventType, stagedMetadata) = stagedRow ?? throw new InvalidOperationException("asserted present above");
    await Assert.That(stagedRouteId).IsEqualTo(routeId);
    await Assert.That(stagedEventType).IsEqualTo(RouteNormalizer.TypeNamePart(eventType));
    await Assert.That(await _jsonEqualAsync(conn, stagedMetadata, "Hops", StoredMetadataCorpus.HopsJson(hops))).IsTrue()
      .Because("the staged form keeps every hop as written");
  }

  private static async Task<bool> _jsonEqualAsync(NpgsqlConnection conn, string document, string key, string expected) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT (@doc::jsonb -> @key) = @expected::jsonb";
    cmd.Parameters.AddWithValue("doc", NpgsqlDbType.Text, document);
    cmd.Parameters.AddWithValue(nameof(key), key);
    cmd.Parameters.AddWithValue(nameof(expected), NpgsqlDbType.Text, expected);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<bool> _isFullAsync(NpgsqlConnection conn, Guid id) {
    using var row = JsonDocument.Parse(await _bodyMetaAsync(conn, id));
    var hop = row.RootElement.GetProperty("Hops").EnumerateArray().First();
    return hop.TryGetProperty("si", out _);
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<string> _bodyMetaAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT metadata::text FROM wh_event_body WHERE event_id = @id";
    cmd.Parameters.AddWithValue(nameof(id), id);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<(Guid RouteId, string EventType, string Metadata)?> _stagedAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT route_id, event_type, metadata::text FROM wh_event_metadata_staging WHERE event_id = @id";
    cmd.Parameters.AddWithValue(nameof(id), id);
    await using var reader = await cmd.ExecuteReaderAsync();
    return await reader.ReadAsync() ? (reader.GetGuid(0), reader.GetString(1), reader.GetString(2)) : null;
  }

  private static async Task<long> _stagedCountAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_event_metadata_staging";
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static Task _settingAsync(NpgsqlConnection conn, string key, string value) =>
    _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES (@k, @v, 'string')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """, ("k", key), ("v", value));

  /// <summary>Stores the events through the outbox emit chain at <paramref name="now"/>; returns their ids in order.</summary>
  private static async Task<Guid[]> _emitOutboxAsync(NpgsqlConnection conn, DateTimeOffset now, int? ttlSeconds,
      params (string Type, List<MessageHop> Hops)[] events) {
    var instanceId = (Guid)TrackedGuid.New();
    var ids = new List<Guid>();
    foreach (var (type, hops) in events) {
      var id = (Guid)TrackedGuid.New();
      ids.Add(id);
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'metadata-topic', @type, 'env',
           jsonb_build_object('id', @id::text, 'p', jsonb_build_object('n', 1), 'h', @hops::jsonb),
           CASE WHEN @ttl::int IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('ett', @ttl::int) END,
           jsonb_build_object('t', 'tenant-a'),
           1, 0, @inst, @lease, 0, @sid, true, 0, @now)
        """,
        ("id", id), ("type", type), ("hops", StoredMetadataCorpus.HopsJson(hops)), ("ttl", (object?)ttlSeconds ?? DBNull.Value),
        ("inst", instanceId), ("lease", now.AddMinutes(5)), ("sid", (Guid)TrackedGuid.New()), ("now", now));
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids.ToArray());
    emit.Parameters.AddWithValue("inst", instanceId);
    emit.Parameters.AddWithValue("lease", now.AddMinutes(5));
    emit.Parameters.AddWithValue(nameof(now), now);
    _ = await emit.ExecuteScalarAsync();
    return [.. ids];
  }

  private static Task _appendDirectAsync(NpgsqlConnection conn, Guid id, string eventType, string metadataJson) =>
    _executeAsync(conn, """
      SELECT wh_append_event(@id, @sid, @sid, split_part(@type, ',', 1), @type, '{"amount": 12}'::jsonb, @meta::jsonb, NULL, @at, 0)
      """,
      ("id", id), ("sid", (Guid)TrackedGuid.New()), ("type", eventType), ("meta", metadataJson), ("at", _t0));

  private static async Task<NpgsqlConnection> _receiveAsync(WorkCoordinationDbContext dbContext, DateTimeOffset now, params InboxMessage[] rows) {
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.StoreInboxMessagesAsync(rows, partitionCount: 100);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var instanceId = (Guid)TrackedGuid.New();
    await _executeAsync(conn, "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = @lease WHERE message_id = ANY(@ids)",
      ("inst", instanceId), ("lease", now.AddMinutes(5)), ("ids", rows.Select(r => r.MessageId).ToArray()));
    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, @lease, @now, 4)",
      ("inst", instanceId), ("lease", now.AddMinutes(5)), ("now", now));
    return conn;
  }

  private static InboxMessage _inboxEvent(Guid stream, List<MessageHop> hops) {
    var id = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(id),
      Payload = JsonSerializer.SerializeToElement(new { happened = true }),
      Hops = hops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new InboxMessage {
      MessageId = id,
      HandlerName = "MetadataHandler",
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = ORDER_PLACED,
      StreamId = stream,
      IsEvent = true,
      Flags = EventFlags.NoRebroadcast,
      Scope = new PerspectiveScope { TenantId = "tenant-a" },
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = hops },
    };
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
