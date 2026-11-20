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
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2323: a stored row leaves out the message id and the hop scope its own columns hold (<c>event_id</c>,
/// <c>scope</c>), and every EF Core read path rebuilds them: an event reads back the same from a row written in the
/// trimmed form as from the same row in the slim form written before it, so old and new rows coexist.
/// </summary>
/// <docs>data/event-store#row-values</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/537_StoredMetadataLeavesOutWhatTheRowHolds.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StoredMetadataRowValuesReadTests : EFCoreTestBase {
  private static readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();
  private static readonly DateTimeOffset _sent = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset _now = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
  private static readonly PerspectiveScope _scope = new() { TenantId = "tenant-2323", UserId = "user-2323" };

  [Test]
  public async Task EveryReader_ReadsATrimmedRow_AsItReadTheSameRowInTheSlimFormAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _connectionAsync(dbContext);
    var stream = (Guid)TrackedGuid.New();
    var plain = _hop(ScopeDelta.FromPerspectiveScope(_scope));
    var withRoles = _hop(ScopeDelta.CreateDelta(null, new ScopeContext {
      Scope = _scope,
      Roles = new HashSet<string>(["reader"]),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
    }));
    var ids = await _emitAsync(conn, stream, plain, withRoles);

    using (var trimmed = JsonDocument.Parse(await _metadataAsync(conn, ids[0]))) {
      await Assert.That(trimmed.RootElement.TryGetProperty("MessageId", out _)).IsFalse()
        .Because("the message id equals the row's event id, so the row leaves it out");
      await Assert.That(trimmed.RootElement.GetProperty("Hops")[0].TryGetProperty("sc", out _)).IsFalse()
        .Because("the hop scope is the row's scope column and nothing else, so the row leaves it out");
    }
    using (var kept = JsonDocument.Parse(await _metadataAsync(conn, ids[1]))) {
      await Assert.That(kept.RootElement.GetProperty("Hops")[0].TryGetProperty("sc", out _)).IsTrue()
        .Because("a hop scope carrying roles holds more than the scope column, so the row keeps it whole");
    }
    var fromTrimmed = await _readEverywayAsync(dbContext, conn, stream, ids);

    // The same events as the slim form wrote them before #2323: the staged full form, reduced by the slim function.
    await _execAsync(conn, """
      UPDATE wh_event_body b SET metadata = _wh_slim_event_metadata(s.metadata, s.route_id)
      FROM wh_event_metadata_staging s WHERE s.event_id = b.event_id AND b.event_id = ANY(@ids)
      """, ("ids", ids));
    using (var slim = JsonDocument.Parse(await _metadataAsync(conn, ids[0]))) {
      await Assert.That(slim.RootElement.GetProperty("MessageId").GetGuid()).IsEqualTo(ids[0])
        .Because("control: the row is back in the slim form, message id and all");
    }
    dbContext.ChangeTracker.Clear();
    var fromSlim = await _readEverywayAsync(dbContext, conn, stream, ids);

    await Assert.That(fromTrimmed.Keys).IsEquivalentTo(fromSlim.Keys);
    foreach (var (reader, envelopes) in fromSlim) {
      await Assert.That(fromTrimmed[reader]).IsEquivalentTo(envelopes, TUnit.Assertions.Enums.CollectionOrdering.Matching)
        .Because($"{reader} reads a trimmed row as it read the slim one");
      await Assert.That(envelopes).Count().IsEqualTo(2).Because($"{reader} read both events");
    }
    await Assert.That(fromSlim["ReadAsync(fromSequence)"][0])
      .IsEqualTo($"{ids[0]}|{_describe(_scope, [])}|{plain.CorrelationId}|{plain.CausationId}|{_sent:O}")
      .Because("the message id, scope, correlation, causation and timestamp are what the event was written with");
  }

  // ===== Helpers =====

  private static MessageHop _hop(ScopeDelta? scope) => new() {
    Type = HopType.Current,
    ServiceInstance = new ServiceInstanceInfo { ServiceName = "orders", InstanceId = (Guid)TrackedGuid.New(), HostName = "host", ProcessId = 7 },
    Timestamp = _sent,
    CorrelationId = CorrelationId.FromExternal(TrackedGuid.New()),
    CausationId = MessageId.From(TrackedGuid.New()),
    Scope = scope,
  };

  private static string _describe(PerspectiveScope scope, IEnumerable<string> roles) =>
    $"{scope.TenantId}/{scope.UserId}/{string.Join(",", roles.Order(StringComparer.Ordinal))}";

  private static string _describe(IMessageEnvelope envelope) {
    var scope = envelope.GetCurrentScope();
    var first = envelope.Hops[0];
    return $"{envelope.MessageId.Value}|{_describe(scope?.Scope ?? new PerspectiveScope(), (IEnumerable<string>?)scope?.Roles ?? [])}|"
      + $"{first.CorrelationId}|{first.CausationId}|{first.Timestamp:O}";
  }

  private static async Task<Dictionary<string, List<string>>> _readEverywayAsync(
      WorkCoordinationDbContext dbContext, NpgsqlConnection conn, Guid stream, Guid[] ids) {
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(dbContext);
    Type[] types = [typeof(OrderCreatedEvent)];
    var reads = new Dictionary<string, List<string>> {
      ["ReadAsync(fromSequence)"] = [],
      ["ReadAsync(fromEventId)"] = [],
      ["ReadPolymorphicAsync"] = [],
    };
    await foreach (var e in store.ReadAsync<OrderCreatedEvent>(stream, fromSequence: 0)) {
      reads["ReadAsync(fromSequence)"].Add(_describe(e));
    }
    await foreach (var e in store.ReadAsync<OrderCreatedEvent>(stream, fromEventId: null)) {
      reads["ReadAsync(fromEventId)"].Add(_describe(e));
    }
    await foreach (var e in store.ReadPolymorphicAsync(stream, null, types)) {
      reads["ReadPolymorphicAsync"].Add(_describe(e));
    }
    var many = await store.ReadPolymorphicManyAsync([(stream, (Guid?)null)], types);
    reads["ReadPolymorphicManyAsync"] = [.. many![stream].Select(_describe)];
    reads["GetEventsBetweenAsync"] = [.. (await store.GetEventsBetweenAsync<OrderCreatedEvent>(stream, null, ids[^1])).Select(e => _describe(e))];
    reads["GetEventsBetweenPolymorphicAsync"] = [.. (await store.GetEventsBetweenPolymorphicAsync(stream, null, ids[^1], types)).Select(_describe)];
    reads["DeserializeStreamEvents"] = [.. store.DeserializeStreamEvents(await _rawAsync(conn, stream), types).Select(_describe)];
    return reads;
  }

  private static async Task<List<StreamEventData>> _rawAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT es.event_id, es.event_type, eb.event_data::text, eb.metadata::text, es.scope::text
      FROM wh_event_store es JOIN wh_event_body eb ON eb.event_id = es.event_id
      WHERE es.stream_id = @stream ORDER BY es.version
      """;
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    var rows = new List<StreamEventData>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new StreamEventData {
        StreamId = stream,
        EventId = reader.GetGuid(0),
        EventType = reader.GetString(1),
        EventData = reader.GetString(2),
        Metadata = reader.GetString(3),
        Scope = reader.GetString(4),
        EventWorkId = (Guid)TrackedGuid.New(),
      });
    }
    return rows;
  }

  /// <summary>Stores one event per hop on <paramref name="stream"/> through the outbox emit chain, scoped by the column.</summary>
  private static async Task<Guid[]> _emitAsync(NpgsqlConnection conn, Guid stream, params MessageHop[] hops) {
    var instance = (Guid)TrackedGuid.New();
    var ids = new List<Guid>();
    var type = TypeNameFormatter.Format(typeof(OrderCreatedEvent));
    var scopeJson = JsonSerializer.Serialize(_scope, _json.GetTypeInfo(typeof(PerspectiveScope)));
    foreach (var hop in hops) {
      var id = (Guid)TrackedGuid.New();
      ids.Add(id);
      var payload = JsonSerializer.Serialize(new OrderCreatedEvent { OrderId = stream, CustomerName = "row-values" },
        _json.GetTypeInfo(typeof(OrderCreatedEvent)));
      await _execAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'row-values', @type, 'env',
           jsonb_build_object('id', @id::text, 'p', @payload::jsonb, 'h', @hops::jsonb),
           '{}'::jsonb, @scope::jsonb, 1, 0, @inst, @lease, 0, @sid, true, 0, @now)
        """,
        ("id", id), ("type", type), ("payload", payload), ("hops", StoredMetadataCorpus.HopsJson([hop])), ("scope", scopeJson),
        ("inst", instance), ("lease", _now.AddMinutes(5)), ("sid", stream), ("now", _now));
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids.ToArray());
    emit.Parameters.AddWithValue("inst", instance);
    emit.Parameters.AddWithValue("lease", _now.AddMinutes(5));
    emit.Parameters.AddWithValue("now", _now);
    _ = await emit.ExecuteScalarAsync();
    return [.. ids];
  }

  private static async Task<NpgsqlConnection> _connectionAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<string> _metadataAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT metadata::text FROM wh_event_body WHERE event_id = @id";
    cmd.Parameters.AddWithValue(nameof(id), id);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
