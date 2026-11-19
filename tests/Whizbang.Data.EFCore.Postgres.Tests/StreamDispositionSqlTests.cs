// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The stream lifecycle ledger's side of the scheduled disposition of sealed streams (#1734, WS-3 S5, migration 280),
/// against a database the EF Core driver migrated: a sealed stream with no disposition is scheduled for the later of its
/// seal and now, plus the grace; the due streams come back oldest first with whether each can go (a perspective that owes
/// an event blocks it, an active hold holds it, an archived one is done); and the step, end to end, archives a sealed
/// stream whole once its grace has passed and not before.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresStreamDispositionLedger.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/280_FinalSnapshotAndScheduledDisposition.sql</code-under-test>
[Category("Integration")]
[Category("Shard1")]
public class StreamDispositionSqlTests : EFCoreTestBase {
  private const string LEDGER = "Whizbang.Tests.LedgerEntry";
  private const string OWED = "Whizbang.Tests.OwedEntry";
  private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _grace = TimeSpan.FromDays(2);

  private PostgresStreamDispositionLedger _ledger() =>
    new(new PostgresArchiveConnection(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, Schema: null));

  [Test]
  public async Task Schedule_ASealedStreamWithNoSchedule_IsDueAGraceAfterTheLaterOfItsSealAndNow_AndNothingElseIsTouchedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var sealedEarlier = await _sealedAsync(conn, _t0);
    var sealedLater = await _sealedAsync(conn, _t0.AddHours(3));
    var active = await _streamAsync(conn);
    var scheduled = await _sealedAsync(conn, _t0);
    await _executeAsync(conn, "UPDATE wh_stream_lifecycle SET disposition_due_at = @d WHERE stream_id = @s", ("d", _t0.AddDays(30)), ("s", scheduled));
    var archived = await _sealedAsync(conn, _t0);
    await Assert.That((await _store().WriteAsync(ArchiveBatch.WholeStream(archived))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    var now = _t0.AddHours(1);

    var count = await _ledger().ScheduleAsync(_grace, 500, now);

    await Assert.That(count).IsEqualTo(2);
    await Assert.That(await _dueAsync(conn, sealedEarlier)).IsEqualTo(now + _grace)
      .Because("the grace counts from when the step first sees the seal, so a stream sealed long before (or restored) gets a full grace");
    await Assert.That(await _dueAsync(conn, sealedLater)).IsEqualTo(_t0.AddHours(3) + _grace)
      .Because("a seal later than the clock reads counts from the seal");
    await Assert.That(await _dueAsync(conn, active)).IsNull().Because("an open stream has no end to schedule");
    await Assert.That(await _dueAsync(conn, scheduled)).IsEqualTo(_t0.AddDays(30)).Because("a schedule is never moved by scheduling again");
    await Assert.That(await _dueAsync(conn, archived)).IsNull().Because("an archived stream is past its disposition");
  }

  [Test]
  public async Task Schedule_TakesAtMostItsLimit_AndTheNextCallTheRestAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    for (var i = 0; i < 3; i++) {
      _ = await _sealedAsync(conn, _t0);
    }

    var first = await _ledger().ScheduleAsync(_grace, 2, _t0);
    var second = await _ledger().ScheduleAsync(_grace, 2, _t0);

    await Assert.That(first).IsEqualTo(2);
    await Assert.That(second).IsEqualTo(1);
  }

  [Test]
  public async Task GetDue_ReturnsTheDueStreamsOldestFirst_WithWhetherEachCanGo_AndOnlyUpToItsLimitAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var ready = await _sealedAsync(conn, _t0);
    // A perspective subscribes to OWED, so its stream's event leaves owed work.
    await _executeAsync(conn,
      "INSERT INTO wh_message_associations (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at) " +
      "VALUES (@id, @t, 'perspective', 'Probe.P', 'test-service', @t, NOW(), NOW())", ("id", Guid.CreateVersion7()), ("t", OWED));
    var blocked = await _sealedAsync(conn, _t0, OWED);
    var held = await _sealedAsync(conn, _t0);
    await _executeAsync(conn,
      "INSERT INTO wh_event_destruction_hold (event_id, hold_until) SELECT es.event_id, NOW() + INTERVAL '1 day' FROM wh_event_store es WHERE es.stream_id = @s",
      ("s", held));
    var done = await _sealedAsync(conn, _t0);
    await Assert.That((await _store().WriteAsync(ArchiveBatch.WholeStream(done))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    var notYet = await _sealedAsync(conn, _t0);
    var ledger = _ledger();
    await ledger.SetDueAsync(done, _t0.AddMinutes(1));
    await ledger.SetDueAsync(ready, _t0.AddMinutes(2));
    await ledger.SetDueAsync(blocked, _t0.AddMinutes(3));
    await ledger.SetDueAsync(held, _t0.AddMinutes(4));
    await ledger.SetDueAsync(notYet, _t0.AddMinutes(30));

    var due = await ledger.GetDueAsync(_t0.AddMinutes(10), 10);
    var limited = await ledger.GetDueAsync(_t0.AddMinutes(10), 2);

    await Assert.That(due).IsEquivalentTo([
      new DueStreamDisposition(done, StreamDispositionReadiness.Done, LEDGER),
      new DueStreamDisposition(ready, StreamDispositionReadiness.Ready, LEDGER),
      new DueStreamDisposition(blocked, StreamDispositionReadiness.Blocked, OWED),
      new DueStreamDisposition(held, StreamDispositionReadiness.Held, LEDGER),
    ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(limited.Select(d => d.StreamId)).IsEquivalentTo([done, ready], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task SetDue_MovesAndClearsTheSchedule_AndGetCursorsListsEachPerspectivesPositionAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = await _sealedAsync(conn, _t0);
    var eventId = await _firstEventAsync(conn, stream);
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@s, 'Probe.P', @e, 0, NOW())",
      ("s", stream), ("e", eventId));
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@s, 'Probe.Q', NULL, 0, NOW())",
      ("s", stream));
    // Named the way the EF Core driver names it: the context's schema.
    var ledger = new PostgresStreamDispositionLedger(
      new PostgresArchiveConnection(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, Schema: "public"));

    await ledger.SetDueAsync(stream, _t0.AddDays(1));
    var moved = await _dueAsync(conn, stream);
    await ledger.SetDueAsync(stream, null);

    await Assert.That(moved).IsEqualTo(_t0.AddDays(1));
    await Assert.That(await _dueAsync(conn, stream)).IsNull();
    await Assert.That(await ledger.GetCursorsAsync(stream)).IsEquivalentTo(
      [new StreamCursorPosition("Probe.P", eventId), new StreamCursorPosition("Probe.Q", null)],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(() => new PostgresStreamDispositionLedger(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Step_ArchivesASealedStreamWhole_OnceItsGraceHasPassed_AndNotBeforeAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = await _sealedAsync(conn, _t0);
    var clock = new FakeTimeProvider(_t0);
    var services = new ServiceCollection()
      .AddSingleton<IStreamDispositionLedger>(_ledger())
      .AddSingleton<IArchiveStore>(_store())
      .AddSingleton(Options.Create(new StreamLifecycleOptions { FinalizeGrace = _grace }))
      .AddSingleton(Options.Create(new MaintenanceWorkerOptions()))
      .BuildServiceProvider();
    var step = new StreamDispositionStep(clock, NullLogger<StreamDispositionStep>.Instance);

    await step.RunAsync(services, CancellationToken.None);
    clock.Advance(_grace - TimeSpan.FromSeconds(1));
    await step.RunAsync(services, CancellationToken.None);
    var stateJustBefore = await _stateAsync(conn, stream);
    clock.Advance(TimeSpan.FromSeconds(1));
    await step.RunAsync(services, CancellationToken.None);

    await Assert.That(stateJustBefore).IsEqualTo("Sealed").Because("a second before the grace ends, the stream stays hot");
    await Assert.That(await _stateAsync(conn, stream)).IsEqualTo("Archived")
      .Because("once the grace has passed, the default disposition archives the whole stream");
    await Assert.That(await _dueAsync(conn, stream)).IsNull().Because("a disposed stream has nothing left scheduled");
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheLedgerAndTheStep_AsAMaintenanceStepAsync() {
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("stream-disposition-registration"));
    _ = new Whizbang.Core.Perspectives.WhizbangPerspectiveBuilder(services).WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;
    await using var registered = services.BuildServiceProvider();
    using var scope = registered.CreateScope();

    await Assert.That(scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<StreamDispositionStep>()).Count().IsEqualTo(1)
      .Because("sealed streams are disposed of by the maintenance cycle, under the maintainer duty, on either driver");
    await Assert.That(services.Any(d => d.ServiceType == typeof(IStreamDispositionLedger))).IsTrue();
  }

  // =================================================================================================

  private PostgresTableArchiveStore _store() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  // One Sourced event through the emit chain on a new stream.
  private static async Task<Guid> _streamAsync(NpgsqlConnection connection, string type = LEDGER) {
    var stream = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{type}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": null, "Scope": null, "StreamId": "{{stream}}", "IsEvent": true, "Flags": 0
        }]
      }
      """;
    await _executeAsync(connection, "SELECT commit_handler_result(@req::jsonb)", ("req", request));
    return stream;
  }

  private static async Task<Guid> _sealedAsync(NpgsqlConnection connection, DateTimeOffset sealedAt, string type = LEDGER) {
    var stream = await _streamAsync(connection, type);
    await _executeAsync(connection, "SELECT wh_finalize_stream(@s, 'ended', @at)", ("s", stream), ("at", sealedAt));
    return stream;
  }

  private static async Task<Guid> _firstEventAsync(NpgsqlConnection connection, Guid stream) {
    await using var c = connection.CreateCommand();
    c.CommandText = "SELECT event_id FROM wh_event_store WHERE stream_id = @s ORDER BY version LIMIT 1";
    c.Parameters.AddWithValue("s", stream);
    return (Guid)(await c.ExecuteScalarAsync())!;
  }

  private static async Task<DateTimeOffset?> _dueAsync(NpgsqlConnection connection, Guid stream) {
    await using var c = connection.CreateCommand();
    c.CommandText = "SELECT disposition_due_at FROM wh_stream_lifecycle WHERE stream_id = @s";
    c.Parameters.AddWithValue("s", stream);
    return await c.ExecuteScalarAsync() is DateTime due ? new DateTimeOffset(due, TimeSpan.Zero) : null;
  }

  private static async Task<string> _stateAsync(NpgsqlConnection connection, Guid stream) {
    await using var c = connection.CreateCommand();
    c.CommandText = "SELECT state FROM wh_stream_lifecycle WHERE stream_id = @s";
    c.Parameters.AddWithValue("s", stream);
    return (string)(await c.ExecuteScalarAsync())!;
  }

  private static async Task _executeAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) {
    await using var c = connection.CreateCommand();
    c.CommandText = sql;
    foreach (var (name, value) in parameters) {
      c.Parameters.AddWithValue(name, value);
    }
    _ = await c.ExecuteNonQueryAsync();
  }
}
