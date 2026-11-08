// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A stream remembers when it started after its first event is removed (#2152, migration 358): the superseding reaper
/// and the prefix truncate record the first event's business time on the stream's ledger row before they delete it, and
/// <c>wh_stream_started_at</c> answers it only while the stream no longer holds its first event.
/// </summary>
/// <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/358_StreamsRememberWhenTheyStarted.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class StreamStartedAtSqlTests : EFCoreTestBase {
  // Seven fractional digits, as .NET writes a time: the database keeps six, cut as the driver cuts a .NET time it binds.
  private static readonly DateTimeOffset _t1 = DateTimeOffset.Parse("2020-03-01T09:00:00.1234567+00:00", System.Globalization.CultureInfo.InvariantCulture);
  private static readonly DateTimeOffset _t2 = new(2020, 6, 5, 9, 0, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset _t3 = new(2020, 9, 9, 9, 0, 0, TimeSpan.Zero);

  // ===== An event's business time =====

  [Test]
  public async Task BusinessTime_IsTheLastStoredHopsTimestamp_CutToTheMicrosecondAsync() {
    await using var conn = await _openAsync();
    var last = DateTimeOffset.Parse("2020-03-02T10:00:00.9999999+00:00", System.Globalization.CultureInfo.InvariantCulture);

    var read = await _timeAsync(conn, "SELECT _wh_event_business_time(@m::jsonb)",
      ("m", """{"Hops":[{"ts":"2020-03-01T09:00:00.1234567+00:00"},{"ts":"2020-03-02T10:00:00.9999999+00:00"}]}"""));
    var written = await _timeAsync(conn, "SELECT @t::timestamptz", ("t", last.UtcDateTime));

    await Assert.That(read).IsEqualTo(written)
      .Because("the runner dates a row at the applied envelope's last hop, and the driver cuts that .NET time to the "
        + "microsecond as it writes it; the recorded start has to be the same value, not one rounded up");
    await Assert.That(await _timeAsync(conn, "SELECT _wh_event_business_time(@m::jsonb)", ("m", """{"hops":[{"ts":"2020-03-01T09:00:00+00:00"}]}""")))
      .IsEqualTo(new DateTime(2020, 3, 1, 9, 0, 0, DateTimeKind.Utc))
      .Because("metadata written with lowercase keys is read as the slim form reads it");
  }

  [Test]
  public async Task BusinessTime_WithNoHop_IsNullAsync() {
    await using var conn = await _openAsync();

    foreach (var metadata in new[] { "{}", """{"Hops":[]}""", """{"Hops":[{"co":"c"}]}""", """{"Hops":{"ts":"x"}}""" }) {
      await Assert.That(await _timeAsync(conn, "SELECT _wh_event_business_time(@m::jsonb)", ("m", metadata))).IsNull()
        .Because($"no hop timestamp in {metadata}, so the writers fall back to the event's created_at");
    }
    await Assert.That(await _timeAsync(conn, "SELECT _wh_event_business_time(NULL)")).IsNull();
  }

  // ===== The reaper records it =====

  [Test]
  public async Task Reap_TheStreamsFirstEvent_RecordsItsBusinessTimeOnTheLedgerAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _appendAsync(stream, _supersedingAt(stream, "a", _t1));
    await _appendAsync(stream, _supersedingAt(stream, "a", _t2));
    await _stampAsync(conn);

    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L);

    var expected = _cut(_t1);
    await Assert.That(await _timeAsync(conn, "SELECT first_event_at FROM wh_stream_lifecycle WHERE stream_id = @s", ("s", stream)))
      .IsEqualTo(expected)
      .Because("the reaper deleted version 1, so it recorded that event's business time before it went");
    await Assert.That(await _startedAtAsync(conn, stream)).IsEqualTo(expected);
  }

  [Test]
  public async Task Reap_ALaterEvent_RecordsNothingAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _appendAsync(stream, _supersedingAt(stream, "a", _t1));
    await _appendAsync(stream, _supersedingAt(stream, "b", _t2));
    await _appendAsync(stream, _supersedingAt(stream, "b", _t3));
    await _stampAsync(conn);

    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L).Because("version 2 is superseded by version 3");

    await Assert.That(await _timeAsync(conn, "SELECT first_event_at FROM wh_stream_lifecycle WHERE stream_id = @s", ("s", stream))).IsNull()
      .Because("the first event is still held, so nothing is recorded");
    await Assert.That(await _startedAtAsync(conn, stream)).IsNull();
  }

  // ===== The prefix truncate records it =====

  [Test]
  public async Task Truncate_ThroughTheFirstEvent_RecordsItsBusinessTimeOnTheLedgerAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _appendAsync(stream, _supersedingAt(stream, "a", _t1));
    await _appendAsync(stream, _supersedingAt(stream, "b", _t2));
    await _appendAsync(stream, _supersedingAt(stream, "c", _t3));

    await Assert.That(await _truncateAsync(conn, stream, 2)).IsEqualTo("truncated");
    await Assert.That(await _startedAtAsync(conn, stream)).IsEqualTo(_cut(_t1))
      .Because("the prefix held version 1, whose business time the truncate recorded");

    await _appendAsync(stream, _supersedingAt(stream, "d", _t3));
    await Assert.That(await _truncateAsync(conn, stream, 3)).IsEqualTo("truncated");
    await Assert.That(await _startedAtAsync(conn, stream)).IsEqualTo(_cut(_t1))
      .Because("a later cut holds no first event and never writes over the recorded start");
  }

  // ===== wh_stream_started_at =====

  [Test]
  public async Task StartedAt_AStreamHoldingItsFirstEvent_IsNullAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _appendAsync(stream, _supersedingAt(stream, "a", _t1));
    await _executeAsync(conn, "UPDATE wh_stream_lifecycle SET first_event_at = @t WHERE stream_id = @s",
      ("t", _t3.UtcDateTime), ("s", stream));

    await Assert.That(await _startedAtAsync(conn, stream)).IsNull()
      .Because("a stream with its first event rebuilds exactly as before, whatever the ledger records");
    await Assert.That(await _startedAtAsync(conn, (Guid)TrackedGuid.New())).IsNull()
      .Because("a stream with no ledger row has no recorded start");
  }

  [Test]
  public async Task StartedAt_AFirstEventRemovedWithNothingRecorded_IsTheFirstAppendAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _appendAsync(stream, _supersedingAt(stream, "a", _t1));
    await _appendAsync(stream, _supersedingAt(stream, "b", _t2));
    // A first event removed before 358 recorded anything.
    await _executeAsync(conn, """
      DELETE FROM wh_event_body WHERE event_id IN (SELECT event_id FROM wh_event_store WHERE stream_id = @s AND version = 1);
      DELETE FROM wh_event_store WHERE stream_id = @s AND version = 1;
      """, ("s", stream));

    var firstAppended = await _timeAsync(conn, "SELECT first_appended_at FROM wh_stream_lifecycle WHERE stream_id = @s", ("s", stream));
    await Assert.That(firstAppended.HasValue).IsTrue();
    await Assert.That(await _startedAtAsync(conn, stream)).IsEqualTo(firstAppended)
      .Because("with no recorded business time, the time the first event was stored is the closest start the ledger knows");
  }

  [Test]
  public async Task EventStore_GetStreamStartedAt_ReadsTheStreamsStartAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _appendAsync(stream, _supersedingAt(stream, "a", _t1));
    await _appendAsync(stream, _supersedingAt(stream, "a", _t2));
    await using var context = CreateDbContext();
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    await Assert.That(await store.GetStreamStartedAtAsync(stream)).IsNull().Because("the stream still holds its first event");

    await _stampAsync(conn);
    await Assert.That(await _reapAsync(conn)).IsEqualTo(1L);

    var startedAt = await store.GetStreamStartedAtAsync(stream);
    await Assert.That(startedAt).IsEqualTo(_cut(_t1));
    await Assert.That(startedAt!.Value.Kind).IsEqualTo(DateTimeKind.Utc);
  }

  // ===== Helpers =====

  private static DateTime _cut(DateTimeOffset at) => new(at.UtcTicks - (at.UtcTicks % 10), DateTimeKind.Utc);

  private static MessageEnvelope<SupersedingProbeEmbedded> _supersedingAt(Guid stream, string key, DateTimeOffset at) => new() {
    MessageId = MessageId.New(),
    Payload = new SupersedingProbeEmbedded { DocumentId = stream, Model = key },
    Hops = [new MessageHop {
      Type = HopType.Current,
      Timestamp = at,
      ServiceInstance = new ServiceInstanceInfo { InstanceId = Guid.CreateVersion7(), ServiceName = "svc", HostName = "h", ProcessId = 1 },
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private async Task _appendAsync<TMessage>(Guid stream, MessageEnvelope<TMessage> envelope) {
    await using var context = CreateDbContext();
    await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(stream, envelope);
  }

  private static Task _stampAsync(NpgsqlConnection conn) => _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");

  private static async Task<long> _reapAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("SELECT rows_affected FROM wh_reap_superseded_events(5000)", conn);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<string> _truncateAsync(NpgsqlConnection conn, Guid stream, long throughVersion) {
    await using var cmd = new NpgsqlCommand("SELECT truncate_status FROM wh_truncate_stream_prefix(@s, @v)", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("v", throughVersion);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static Task<DateTime?> _startedAtAsync(NpgsqlConnection conn, Guid stream) =>
    _timeAsync(conn, "SELECT wh_stream_started_at(@s)", ("s", stream));

  private static async Task<DateTime?> _timeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = new NpgsqlCommand(sql, conn);
    foreach (var (name, value) in parameters) {
      cmd.Parameters.Add(value is DateTime
        ? new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value }
        : new NpgsqlParameter(name, value));
    }
    var result = await cmd.ExecuteScalarAsync();
    return result is DateTime at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = new NpgsqlCommand(sql, conn);
    foreach (var (name, value) in parameters) {
      cmd.Parameters.Add(value is DateTime
        ? new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value }
        : new NpgsqlParameter(name, value));
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }
}
