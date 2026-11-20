// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2323: the Dapper store's rows leave out the message id (<c>message_id</c>) and the hop scope their own columns hold,
/// and every read path of the store rebuilds them: an event reads back the same from a trimmed row as from the same row
/// in the slim form written before it.
/// </summary>
/// <docs>data/event-store#row-values</docs>
public class DapperStoredMetadataRowValuesTests : PostgresTestBase {
  private static readonly DateTimeOffset _sent = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);
  private static readonly PerspectiveScope _scope = new() { TenantId = "tenant-2323", UserId = "user-2323" };

  [Test]
  public async Task EveryReader_ReadsATrimmedRow_AsItReadTheSameRowInTheSlimFormAsync() {
    var store = _eventStore();
    var stream = (Guid)TrackedGuid.New();
    var envelopes = new[] { _envelope(stream), _envelope(stream) };
    foreach (var envelope in envelopes) {
      await store.AppendAsync(stream, envelope);
    }
    var ids = envelopes.Select(e => e.MessageId.Value).ToArray();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    using (var trimmed = JsonDocument.Parse(await conn.ExecuteScalarAsync<string>(
      "SELECT metadata::text FROM wh_event_body WHERE event_id = @id", new { id = ids[0] }) ?? "{}")) {
      await Assert.That(trimmed.RootElement.TryGetProperty("message_id", out _)).IsFalse()
        .Because("the message id is the row's event id, so the row leaves it out");
      await Assert.That(trimmed.RootElement.GetProperty("hops")[0].TryGetProperty("sc", out _)).IsFalse()
        .Because("the hop scope is the row's scope column, so the row leaves it out");
    }
    var fromTrimmed = await _readEverywayAsync(store, conn, stream, ids);

    _ = await conn.ExecuteAsync("""
      UPDATE wh_event_body b SET metadata = _wh_slim_event_metadata(s.metadata, s.route_id)
      FROM wh_event_metadata_staging s WHERE s.event_id = b.event_id AND b.event_id = ANY(@ids)
      """, new { ids });
    await Assert.That(await conn.ExecuteScalarAsync<string>("SELECT metadata ->> 'message_id' FROM wh_event_body WHERE event_id = @id", new { id = ids[0] }))
      .IsEqualTo(ids[0].ToString()).Because("control: the row is back in the slim form, message id and all");
    var fromSlim = await _readEverywayAsync(store, conn, stream, ids);

    await Assert.That(fromTrimmed.Keys).IsEquivalentTo(fromSlim.Keys);
    foreach (var (reader, read) in fromSlim) {
      await Assert.That(fromTrimmed[reader]).IsEquivalentTo(read, TUnit.Assertions.Enums.CollectionOrdering.Matching)
        .Because($"{reader} reads a trimmed row as it read the slim one");
      await Assert.That(read).Count().IsEqualTo(2).Because($"{reader} read both events");
    }
    var first = envelopes[0].Hops[0];
    await Assert.That(fromSlim["ReadAsync(fromSequence)"][0])
      .IsEqualTo($"{ids[0]}|tenant-2323/user-2323|{first.CorrelationId}|{first.CausationId}|{_sent:O}")
      .Because("the message id, scope, correlation, causation and timestamp are what the event was written with");
  }

  [Test]
  public async Task MessageIdOf_TheStoredKey_ElseTheRowsEventId_ElseTheRowCannotBeReadAsync() {
    var eventId = (Guid)TrackedGuid.New();
    var written = (Guid)TrackedGuid.New();
    var withKey = new Dictionary<string, JsonElement> { ["message_id"] = JsonSerializer.SerializeToElement(written.ToString()) };

    await Assert.That(JsonbPersistenceModel.MessageIdOf(withKey, eventId)).IsEqualTo(written)
      .Because("a message id the row keeps is returned as written");
    await Assert.That(JsonbPersistenceModel.MessageIdOf([], eventId)).IsEqualTo(eventId)
      .Because("a row that left it out has its event id");
    await Assert.That(() => JsonbPersistenceModel.MessageIdOf([], null)).Throws<InvalidOperationException>()
      .Because("a read that selects no event id cannot rebuild one, and says so as it always did");
  }

  // ===== Helpers =====

  private static MessageEnvelope<DapperMixedStreamProbeNoted> _envelope(Guid stream) => new() {
    MessageId = MessageId.From(TrackedGuid.New()),
    Payload = new DapperMixedStreamProbeNoted { StreamId = stream },
    Hops = [new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo { ServiceName = "orders", InstanceId = (Guid)TrackedGuid.New(), HostName = "host", ProcessId = 7 },
      Timestamp = _sent,
      CorrelationId = CorrelationId.FromExternal(TrackedGuid.New()),
      CausationId = MessageId.From(TrackedGuid.New()),
      Scope = ScopeDelta.FromPerspectiveScope(_scope),
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static string _describe(IMessageEnvelope envelope) {
    var scope = envelope.GetCurrentScope()?.Scope;
    var first = envelope.Hops[0];
    return $"{envelope.MessageId.Value}|{scope?.TenantId}/{scope?.UserId}|{first.CorrelationId}|{first.CausationId}|{first.Timestamp:O}";
  }

  private static async Task<Dictionary<string, List<string>>> _readEverywayAsync(
      DapperPostgresEventStore store, NpgsqlConnection conn, Guid stream, Guid[] ids) {
    Type[] types = [typeof(DapperMixedStreamProbeNoted)];
    var reads = new Dictionary<string, List<string>> {
      ["ReadAsync(fromSequence)"] = [],
      ["ReadAsync(fromEventId)"] = [],
      ["ReadPolymorphicAsync"] = [],
    };
    await foreach (var e in store.ReadAsync<DapperMixedStreamProbeNoted>(stream, fromSequence: 0)) {
      reads["ReadAsync(fromSequence)"].Add(_describe(e));
    }
    await foreach (var e in store.ReadAsync<DapperMixedStreamProbeNoted>(stream, fromEventId: null)) {
      reads["ReadAsync(fromEventId)"].Add(_describe(e));
    }
    await foreach (var e in store.ReadPolymorphicAsync(stream, null, types)) {
      reads["ReadPolymorphicAsync"].Add(_describe(e));
    }
    reads["GetEventsBetweenAsync"] = [.. (await store.GetEventsBetweenAsync<DapperMixedStreamProbeNoted>(stream, null, ids[^1])).Select(e => _describe(e))];
    reads["GetEventsBetweenPolymorphicAsync"] = [.. (await store.GetEventsBetweenPolymorphicAsync(stream, null, ids[^1], types)).Select(_describe)];
    var rows = await conn.QueryAsync<(Guid EventId, string EventType, string EventData, string Metadata, string Scope)>("""
      SELECT es.event_id, es.event_type, eb.event_data::text, eb.metadata::text, es.scope::text
      FROM wh_event_store es JOIN wh_event_body eb ON eb.event_id = es.event_id
      WHERE es.stream_id = @stream ORDER BY es.version
      """, new { stream });
    var raw = rows.Select(r => _raw(stream, r)).ToList();
    reads["DeserializeStreamEvents"] = [.. store.DeserializeStreamEvents(raw, types).Select(_describe)];
    return reads;
  }

  private static StreamEventData _raw(Guid stream, (Guid EventId, string EventType, string EventData, string Metadata, string Scope) row) => new() {
    StreamId = stream,
    EventId = row.EventId,
    EventType = row.EventType,
    EventData = row.EventData,
    Metadata = row.Metadata,
    Scope = row.Scope,
    EventWorkId = (Guid)TrackedGuid.New(),
  };

  private DapperPostgresEventStore _eventStore() {
    var jsonOptions = Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions();
    return new DapperPostgresEventStore(
      ConnectionFactory,
      Executor,
      jsonOptions,
      new EventEnvelopeJsonbAdapter(jsonOptions),
      new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance),
      new PolicyEngine(),
      null,
      NullLogger<DapperPostgresEventStore>.Instance);
  }
}
