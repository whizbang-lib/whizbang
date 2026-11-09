// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>A second superseding type, so one cycle has several types to walk.</summary>
[Whizbang.Core.Attributes.Superseding]
public sealed record TypeHistoryWalkProbeOther : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public Guid DocumentId { get; init; }

  /// <summary>The key.</summary>
  [Whizbang.Core.Attributes.SupersedingKey]
  public string Model { get; init; } = "";
}

/// <summary>
/// #2205: a type-scoped history walk (the superseding reconcile here, the ephemeral reclassify in #2206) reads its type's
/// own events through the type's index, in commit order, in bounded batches (migration 374). It used to read the whole
/// event store by event id for every type, so its cost was the store's size times the number of types. Each test seeds a
/// store where the type is a small part of it.
/// </summary>
/// <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/374_HistoryWalksReadTheirOwnType.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/TypeHistoryWalker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SupersedingHistoryReconcileStep.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class TypeHistoryWalkSqlTests : EFCoreTestBase {
  private const int SUPERSEDING = 128;
  private const string TYPE_INDEX = EventStorePlanCapture.TYPE_INDEX;
  private static readonly string _embedded = TypeNameFormatter.Format(typeof(SupersedingProbeEmbedded));
  private static readonly string _other = TypeNameFormatter.Format(typeof(TypeHistoryWalkProbeOther));
  private static readonly string _definitionName = TypeNameFormatter.FormatClrTypeName(typeof(SupersedingProbeEmbedded));
  private static readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();

  // ===== Cost =====

  /// <summary>
  /// The type's 7 events sit among 20,000 events of other types. The walk reads ceil(7 / 3) = 3 batches of the type's own
  /// events, never one of the others; it used to read the 20,007 events of the store, 3 at a time.
  /// </summary>
  [Test]
  public async Task Reconcile_CostTracksTheTypesOwnEvents_NotTheStoreAsync() {
    await using var conn = await _openAsync();
    await EventStorePlanCapture.SeedOtherTypesAsync(conn, 10_000);
    var ids = new List<Guid>();
    for (var i = 0; i < 7; i++) {
      var stream = (Guid)TrackedGuid.New();
      ids.Add(await _appendAsync(conn, stream, _embedded, _embeddedJson(stream, "k")));
      if (i == 3) {
        await EventStorePlanCapture.SeedOtherTypesAsync(conn, 10_000);
      }
    }

    var result = await _step(3, 100, typeof(SupersedingProbeEmbedded)).ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(result.Batches).IsEqualTo(3).Because("ceil(7 / 3) windows of the type's own events, whatever the store holds");
    await Assert.That(result.EventsScanned).IsEqualTo(7L).Because("a window holds the type's events only");
    await Assert.That(result.EventsStamped).IsEqualTo(7L);
    await Assert.That(result.TypesFinished).IsEqualTo(1);
  }

  /// <summary>
  /// The walk's statements, run with auto_explain on a session whose plans are generic, find the type's rows through an
  /// index on the type: the window in idx_event_store_type_commit_sequence, and every other read by key or by type. The
  /// type's events are split by 20,000 events of other types in commit order, so a walk of the commit-sequence index
  /// across a window, or of the primary key, would fetch them.
  /// </summary>
  [Test]
  public async Task TheWalk_ReadsTheTypeThroughItsIndex_NeverTheStoreAsync() {
    await using (var conn = await _openAsync()) {
      for (var half = 0; half < 2; half++) {
        for (var i = 0; i < 25; i++) {
          var stream = (Guid)TrackedGuid.New();
          await _appendAsync(conn, stream, _embedded, _embeddedJson(stream, "k"));
        }
        await EventStorePlanCapture.SeedOtherTypesAsync(conn, 20_000);
      }
      await EventStorePlanCapture.BuildTypeIndexAsync(conn);
    }
    var capture = new EventStorePlanCapture();

    var result = await _step(20, 100, typeof(SupersedingProbeEmbedded))
      .ReconcileAsync(_services(capture.Connection(ConnectionString)), CancellationToken.None);

    var scans = capture.Scans();
    await Assert.That(result.EventsStamped).IsEqualTo(50L).Because("precondition: the walk did its work");
    await Assert.That(scans).IsNotEmpty().Because("precondition: the walk's plans were captured");
    await Assert.That(scans.Any(s => s.Node.Contains(TYPE_INDEX, StringComparison.Ordinal) && s.IndexConditions.Contains("event_type", StringComparison.Ordinal)))
      .IsTrue().Because("the window is the type's range of idx_event_store_type_commit_sequence");
    await Assert.That(scans.Where(s => s.Node.StartsWith("Seq Scan", StringComparison.Ordinal)).Select(s => s.Node)).IsEmpty()
      .Because("no statement of the walk reads the event store from end to end");
    var fetched = scans.Sum(s => s.RowsFetched);
    await Assert.That(fetched).IsLessThan(2_000L)
      .Because($"the walk reads the type's 50 events a few times over; it fetched {fetched} rows of a 40,050-event store: "
        + string.Join("; ", scans.Where(s => s.RowsFetched > 100).Select(s => $"{s.Node} [{s.IndexConditions}] {s.RowsFetched}")));
  }

  [Test]
  public async Task Reconcile_BeforeTheTypeIndexIsBuilt_AnswersTheSameAsync() {
    await using var conn = await _openAsync();
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM pg_indexes WHERE indexname = '{TYPE_INDEX}'")).IsEqualTo(0L)
      .Because("precondition: the index is built after the migrations, by the maintenance cycle");
    var stream = (Guid)TrackedGuid.New();
    await EventStorePlanCapture.SeedOtherTypesAsync(conn, 100);
    var a1 = await _appendAsync(conn, stream, _embedded, _embeddedJson(stream, "k"));
    await _appendAsync(conn, stream, _embedded, _embeddedJson(stream, "k"));

    var result = await _step(1, 100, typeof(SupersedingProbeEmbedded)).ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(result.EventsScanned).IsEqualTo(2L).Because("the walk reads the type's events through idx_event_store_type_tenant until then");
    await Assert.That(result.TypesFinished).IsEqualTo(1);
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([a1]);
  }

  // ===== Completion =====

  /// <summary>
  /// An event of the type not yet numbered by the commit-order stamper is not in the walk's order yet. The walk reconciles
  /// what is stamped and does not complete; once the event is stamped, it lands past the cursor and is reconciled.
  /// </summary>
  [Test]
  public async Task Reconcile_EventsUnstampedAtTheStart_AreReconciledOnceStamped_AndHoldTheWalkOpenAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var a1 = await _appendAsync(conn, stream, _embedded, _embeddedJson(stream, "k"));
    var a2 = await _appendAsync(conn, stream, _embedded, _embeddedJson(stream, "k"));
    var unstamped = await _appendAsync(conn, stream, _embedded, _embeddedJson(stream, "k"), stamp: false);

    var first = await _step(100, 10, typeof(SupersedingProbeEmbedded)).ReconcileAsync(_services(), CancellationToken.None);
    var still = await _step(100, 10, typeof(SupersedingProbeEmbedded)).ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(first.EventsStamped).IsEqualTo(2L).Because("the stamped history is reconciled");
    await Assert.That(await _flagsAsync(conn, unstamped)).IsEqualTo(0L).Because("an unstamped event is read once it is stamped");
    await Assert.That(first.TypesFinished).IsEqualTo(0).Because("a walk never completes while an event of its type is unstamped");
    await Assert.That(first.Status).StartsWith("waiting");
    await Assert.That(still.Status).StartsWith("waiting").Because("it stays open until the stamper reaches the event");
    await Assert.That(await _completedAsync(conn, _embedded)).IsFalse();

    await _stampAsync(conn);
    var last = await _step(100, 10, typeof(SupersedingProbeEmbedded)).ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(last.EventsStamped).IsEqualTo(1L);
    await Assert.That(await _flagsAsync(conn, unstamped)).IsEqualTo((long)SUPERSEDING);
    await Assert.That(last.TypesFinished).IsEqualTo(1);
    await Assert.That(await _completedAsync(conn, _embedded)).IsTrue();
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([a1, a2]);
  }

  [Test]
  public async Task Begin_AWalkPastTheEnd_WaitsForUnstampedEvents_ThenFinishesAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _appendAsync(conn, stream, _embedded, _embeddedJson(stream, "k"), stamp: false);
    await _executeAsync(conn, "SELECT wh_type_history_ensure('superseding', @t, @d)", ("t", _embedded), ("d", _definitionName));

    await Assert.That(await _beginStatusAsync(conn)).StartsWith("waiting")
      .Because("nothing is stamped to read, and the type still has an event the stamper owes a number");
    await _stampAsync(conn);
    await _executeAsync(conn,
      "UPDATE wh_type_history_walks SET last_commit_sequence = (SELECT max(commit_sequence) FROM wh_event_store) WHERE event_type = @t",
      ("t", _embedded));

    await Assert.That(await _beginStatusAsync(conn)).IsEqualTo("finished").Because("past the type's newest event, with nothing unstamped");
    await Assert.That(await _beginStatusAsync(conn)).IsEqualTo("complete");
  }

  // ===== Several types, one budget =====

  [Test]
  public async Task Reconcile_SeveralTypesInOneCycle_ShareTheBatchBudget_AndACompleteTypeSpendsNoneOfItAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    for (var i = 0; i < 2; i++) {
      await _appendAsync(conn, stream, _embedded, _embeddedJson(stream, "k"));
    }
    var otherStream = (Guid)TrackedGuid.New();
    for (var i = 0; i < 4; i++) {
      await _appendAsync(conn, otherStream, _other, _otherJson(otherStream, "k"));
    }
    var step = _step(2, 3, typeof(SupersedingProbeEmbedded), typeof(TypeHistoryWalkProbeOther));

    var first = await step.ReconcileAsync(_services(), CancellationToken.None);
    var second = await step.ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(first.Batches).IsEqualTo(3).Because("one batch finishes the first type and two go to the second, within the budget");
    await Assert.That(first.EventsStamped).IsEqualTo(6L).Because("both types are walked in the same cycle");
    await Assert.That(first.TypesFinished).IsEqualTo(1);
    await Assert.That(second.TypesFinished).IsEqualTo(1)
      .Because("the complete first type spends none of the next cycle's budget, so the second type reaches its end");
    await Assert.That(second.Batches).IsEqualTo(0).Because("the second type's last window came back empty: nothing left to read");
  }

  [Test]
  public async Task Reconcile_ABudgetSpentOnOneType_LeavesTheNextTypeUntouchedForTheNextCycleAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    for (var i = 0; i < 3; i++) {
      await _appendAsync(conn, stream, _embedded, _embeddedJson(stream, "k"));
    }
    var otherStream = (Guid)TrackedGuid.New();
    var other = await _appendAsync(conn, otherStream, _other, _otherJson(otherStream, "k"));
    var step = _step(2, 1, typeof(SupersedingProbeEmbedded), typeof(TypeHistoryWalkProbeOther));

    var first = await step.ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(first.Batches).IsEqualTo(1);
    await Assert.That(await _flagsAsync(conn, other)).IsEqualTo(0L).Because("the cycle's one batch went to the first type");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_type_history_walks WHERE event_type = @t", ("t", _other)))
      .IsEqualTo(0L).Because("a type the budget does not reach is not even ensured this cycle");

    var second = await step.ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(second.Batches).IsEqualTo(1);
    await Assert.That(second.TypesFinished).IsEqualTo(1).Because("the first type's last window came back short");
    await Assert.That(await _flagsAsync(conn, other)).IsEqualTo(0L).Because("its batch again spent the cycle's budget");
  }

  // ===== The cursor rows 345 kept =====

  [Test]
  public async Task CursorRowsThe345WalkLeft_AreCleared_AndEveryTypeIsWalkedAgainFromItsFirstEventAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var a1 = await _appendAsync(conn, stream, _embedded, _embeddedJson(stream, "k"));
    await _appendAsync(conn, stream, _embedded, _embeddedJson(stream, "k"));
    // A walk by event id that had passed both events and completed under the earlier migration.
    await _executeAsync(conn,
      "INSERT INTO wh_superseding_history (event_type, last_event_id, completed_at) VALUES (@t, 'ffffffff-ffff-ffff-ffff-ffffffffffff', NOW())",
      ("t", _embedded));

    await _executeAsync(conn, _migrationSql("374_HistoryWalksReadTheirOwnType"));
    var result = await _step(100, 10, typeof(SupersedingProbeEmbedded)).ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_superseding_history")).IsEqualTo(0L)
      .Because("a cursor by event id means nothing to the walker");
    await Assert.That(result.EventsStamped).IsEqualTo(2L).Because("the type is walked again from its first event");
    await Assert.That(result.TypesFinished).IsEqualTo(1);
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([a1]);
  }

  // ===== Helpers =====

  private static SupersedingHistoryReconcileStep _step(int scanSize, int maxBatches, params Type[] types) =>
    new(NullLogger.Instance, scanSize, maxBatches, () => types);

  private PostgresArchiveConnection _connection() =>
    new(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, "public");

  private ServiceProvider _services(PostgresArchiveConnection? connection = null) =>
    new ServiceCollection().AddSingleton(connection ?? _connection()).BuildServiceProvider();

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static string _migrationSql(string name) =>
    new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public").GetMigration(name)!.Sql;

  private static string _embeddedJson(Guid stream, string model) =>
    JsonSerializer.Serialize(new SupersedingProbeEmbedded { DocumentId = stream, Model = model }, _json.GetTypeInfo(typeof(SupersedingProbeEmbedded)));

  private static string _otherJson(Guid stream, string model) =>
    JsonSerializer.Serialize(new TypeHistoryWalkProbeOther { DocumentId = stream, Model = model }, _json.GetTypeInfo(typeof(TypeHistoryWalkProbeOther)));

  /// <summary>An event as a write path stored it before its type was declared superseding: no flag, no key.</summary>
  private static async Task<Guid> _appendAsync(NpgsqlConnection conn, Guid stream, string type, string payload, bool stamp = true) {
    var id = (Guid)TrackedGuid.New();
    await _executeAsync(conn,
      "SELECT wh_append_event(@id, @s, @s, split_part(@t, ',', 1), @t, @p::jsonb, @m::jsonb, NULL, NOW(), 0)",
      ("id", id), ("s", stream), ("t", type), ("p", payload), ("m", $$"""{"MessageId":"{{id}}","Hops":[]}"""));
    if (stamp) {
      await _stampAsync(conn);
    }
    return id;
  }

  private static async Task<string> _beginStatusAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT status FROM wh_type_history_begin('superseding', @t, @d, 10)";
    cmd.Parameters.AddWithValue("t", _embedded);
    cmd.Parameters.AddWithValue("d", _definitionName);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<bool> _completedAsync(NpgsqlConnection conn, string type) =>
    await _scalarAsync(conn,
      "SELECT count(*) FROM wh_type_history_walks WHERE operation = 'superseding' AND event_type = @t AND completed_at IS NOT NULL",
      ("t", type)) == 1L;

  private static Task _stampAsync(NpgsqlConnection conn) => _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");

  private static Task<long> _flagsAsync(NpgsqlConnection conn, Guid eventId) =>
    _scalarAsync(conn, "SELECT COALESCE(flags, 0) FROM wh_event_store WHERE event_id = @id", ("id", eventId));

  private static async Task<List<Guid>> _queuedAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id FROM wh_superseded_events ORDER BY event_id";
    var rows = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetGuid(0));
    }
    return rows;
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
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
