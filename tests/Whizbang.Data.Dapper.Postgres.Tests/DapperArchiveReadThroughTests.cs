// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Dapper.Postgres.Tests.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Archive read-through on the Dapper driver (#1738, WS-3 S9): a stream archived whole, a stream archived through a
/// prefix and the same stream never archived read back the same events through the archive seam over this driver's
/// event store, on the polymorphic read a rebuild makes, the between-read and the typed reads; and this driver decodes a
/// stored row with <see cref="IEventStore.DeserializeStreamEvents"/>, which the seam decodes archived rows with.
/// </summary>
/// <docs>fundamentals/events/archive-store#read-through</docs>
public class DapperArchiveReadThroughTests : PostgresTestBase {
  private static readonly IReadOnlyList<Type> _types = [typeof(PostgresRetryTestEvent), typeof(DapperJsonbNoteChangedEvent)];

  [Test]
  public async Task Load_AHotStream_AStreamArchivedWhole_AndOneArchivedThroughAPrefix_ReadTheSameEventsAsync() {
    var (hot, whole, prefix) = await _threeCopiesAsync();
    var store = new ArchiveAwareEventStoreDecorator(_eventStore(), _archive(), readThrough: true);

    var expected = await _describeAsync(store.ReadPolymorphicAsync(hot, null, _types));
    await Assert.That(expected).IsEquivalentTo(["retry one", "note two", "retry three", "note four"])
      .Because("the control: the hot copy reads every event");
    foreach (var archived in new[] { whole, prefix }) {
      await Assert.That(await _describeAsync(store.ReadPolymorphicAsync(archived, null, _types))).IsEquivalentTo(expected);
      await Assert.That((await store.GetEventsBetweenPolymorphicAsync(archived, null, Guid.Empty, _types)).Select(_describe))
        .IsEquivalentTo(expected);
    }
  }

  [Test]
  public async Task Load_TypedReads_OfAnArchivedStream_ReturnItsEventsAsync() {
    var stream = Guid.CreateVersion7();
    var driver = _eventStore();
    foreach (var payload in new[] { "a", "b", "c" }) {
      await driver.AppendAsync(stream, new PostgresRetryTestEvent { StreamId = stream, Payload = payload });
    }
    _ = await _archive().WriteAsync(ArchiveBatch.Prefix(stream, 1));
    var store = new ArchiveAwareEventStoreDecorator(driver, _archive(), readThrough: true);

    var bySequence = await _listAsync(store.ReadAsync<PostgresRetryTestEvent>(stream, fromSequence: 0));
    var byId = await _listAsync(store.ReadAsync<PostgresRetryTestEvent>(stream, (Guid?)null));
    var between = await store.GetEventsBetweenAsync<PostgresRetryTestEvent>(stream, null, Guid.Empty);

    foreach (var read in new[] { bySequence, byId, between }) {
      await Assert.That(read.Select(e => e.Payload.Payload)).IsEquivalentTo(["a", "b", "c"]);
    }
    await Assert.That(bySequence[2].LocalStreamVersion).IsEqualTo(3L)
      .Because("every read carries the stream version, which is how the seam sees a gap without asking the archive");
  }

  [Test]
  public async Task DeserializeStreamEvents_DecodesAStoredRow_AndSkipsATypeTheReaderDoesNotFoldAsync() {
    var stream = Guid.CreateVersion7();
    var driver = _eventStore();
    await driver.AppendAsync(stream, new PostgresRetryTestEvent { StreamId = stream, Payload = "kept" });
    await driver.AppendAsync(stream, new DapperJsonbNoteChangedEvent { StreamId = stream, Note = "skipped" });
    await using var conn = new NpgsqlConnection(ConnectionString);
    var rows = (await conn.QueryAsync<(Guid EventId, string EventType, string EventData, string Metadata, string? Scope)>(
      "SELECT es.event_id, es.event_type, eb.event_data::text, eb.metadata::text, es.scope::text FROM wh_event_store es " +
      "JOIN wh_event_body eb ON eb.event_id = es.event_id WHERE es.stream_id = @stream ORDER BY es.version", new { stream })).ToList();

    var decoded = driver.DeserializeStreamEvents(
      [.. rows.Select(r => new StreamEventData {
        StreamId = stream, EventId = r.EventId, EventType = r.EventType, EventData = r.EventData,
        Metadata = r.Metadata, Scope = r.Scope, EventWorkId = Guid.Empty, CommitSequence = 7,
      })],
      [typeof(PostgresRetryTestEvent)]);

    await Assert.That(decoded).Count().IsEqualTo(1);
    await Assert.That(((PostgresRetryTestEvent)decoded[0].Payload).Payload).IsEqualTo("kept");
    await Assert.That(decoded[0].MessageId.Value).IsEqualTo(rows[0].EventId);
    await Assert.That(decoded[0].LocalCommitSequence).IsEqualTo(7L);
  }

  [Test]
  public async Task DeserializeStreamEvents_ARowThatCannotBeRead_IsSkipped_AndTheRestAreKeptAsync() {
    var driver = _eventStore();
    var good = Guid.CreateVersion7();
    var stream = Guid.CreateVersion7();
    await driver.AppendAsync(stream, new PostgresRetryTestEvent { StreamId = stream, Payload = "kept" });
    await using var conn = new NpgsqlConnection(ConnectionString);
    var stored = await conn.QuerySingleAsync<(string EventType, string EventData, string Metadata)>(
      "SELECT es.event_type, eb.event_data::text, eb.metadata::text FROM wh_event_store es " +
      "JOIN wh_event_body eb ON eb.event_id = es.event_id WHERE es.stream_id = @stream", new { stream });

    var decoded = driver.DeserializeStreamEvents([
      new StreamEventData {
        StreamId = stream, EventId = Guid.CreateVersion7(), EventType = stored.EventType, EventData = "{not json",
        Metadata = stored.Metadata, EventWorkId = Guid.Empty,
      },
      new StreamEventData {
        StreamId = stream, EventId = good, EventType = stored.EventType, EventData = stored.EventData,
        Metadata = stored.Metadata, EventWorkId = Guid.Empty,
      },
    ], [typeof(PostgresRetryTestEvent)]);

    await Assert.That(decoded).Count().IsEqualTo(1).Because("one unreadable row is skipped, never the batch");
  }

  // ===== Helpers =====

  private PostgresTableArchiveStore _archive() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  private async Task<(Guid Hot, Guid Whole, Guid Prefix)> _threeCopiesAsync() {
    var driver = _eventStore();
    var streams = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
    foreach (var stream in streams) {
      await driver.AppendAsync(stream, new PostgresRetryTestEvent { StreamId = stream, Payload = "one" });
      await driver.AppendAsync(stream, new DapperJsonbNoteChangedEvent { StreamId = stream, Note = "two" });
      await driver.AppendAsync(stream, new PostgresRetryTestEvent { StreamId = stream, Payload = "three" });
      await driver.AppendAsync(stream, new DapperJsonbNoteChangedEvent { StreamId = stream, Note = "four" });
    }
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.WholeStream(streams[1]))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(streams[2], 1))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    return (streams[0], streams[1], streams[2]);
  }

  private static string _describe(MessageEnvelope<IEvent> e) => e.Payload switch {
    PostgresRetryTestEvent r => $"retry {r.Payload}",
    DapperJsonbNoteChangedEvent n => $"note {n.Note}",
    _ => e.Payload.GetType().Name,
  };

  private static async Task<List<string>> _describeAsync(IAsyncEnumerable<MessageEnvelope<IEvent>> read) =>
    (await _listAsync(read)).ConvertAll(_describe);

  private static async Task<List<T>> _listAsync<T>(IAsyncEnumerable<T> source) {
    var list = new List<T>();
    await foreach (var item in source) {
      list.Add(item);
    }
    return list;
  }

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
