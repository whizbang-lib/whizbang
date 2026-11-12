// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2273, the handled ledger against the real SQL: the completion that processes an inbox event records it, the lookup
/// answers which of a bundle's events were handled and when coverage starts, and the sweep keeps the ledger to the
/// lookback, moving coverage forward before it removes anything and never moving it back.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/401_RepairedEventsRunOnlyReceptorsThatNeverRan.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
[Category("Integration")]
[Category("Shard5")]
public class HandledLedgerSqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Upstream.Contracts.OrderPlaced, Upstream.Contracts";
  private readonly List<WorkCoordinationDbContext> _contexts = [];

  // Typed as the interface the workers call through, so a member left at its default is what this would reach.
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Deliberate: an unimplemented member reaches the interface default here as it does in the workers.")]
  private IWorkCoordinator _coordinator() {
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

  private static InboxMessage _message(Guid id, bool isEvent) => new() {
    MessageId = id,
    HandlerName = "OrderPlacedHandler",
    Envelope = new MessageEnvelope<JsonElement> {
      MessageId = new MessageId(id),
      Payload = JsonElement.Parse("{}"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Outbox },
    },
    EnvelopeType = EnvelopeTypeNameHelper.Format(EVENT_TYPE),
    StreamId = TrackedGuid.New().Value,
    IsEvent = isEvent,
    Flags = EventFlags.None,
    Metadata = new EnvelopeMetadata { MessageId = new MessageId(id), Hops = [] },
    MessageType = EVENT_TYPE,
  };

  private static async Task _completeAsync(NpgsqlConnection conn, Guid id, int status) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM process_inbox_completions($1::jsonb, NOW())";
    cmd.Parameters.Add(new NpgsqlParameter { Value = $$"""[{"MessageId":"{{id}}","Status":{{status}}}]""", NpgsqlDbType = NpgsqlDbType.Jsonb });
    await cmd.ExecuteScalarAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<DateTimeOffset> _nowAsync(NpgsqlConnection conn) =>
    new(DateTime.SpecifyKind(await _scalarAsync<DateTime>(conn, "SELECT now()"), DateTimeKind.Utc));

  private static Task<bool> _recordedAsync(NpgsqlConnection conn, Guid id) =>
    _scalarAsync<bool>(conn, "SELECT EXISTS (SELECT 1 FROM wh_receptor_handled WHERE event_id = $1)", id);

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task AnEventCompletedAsProcessed_IsRecordedHandledAsync() {
    var handled = TrackedGuid.New().Value;
    await _coordinator().StoreInboxMessagesAsync([_message(handled, isEvent: true)], partitionCount: 100);
    await using var conn = await _openAsync();

    await _completeAsync(conn, handled, status: 2);

    await Assert.That(await _recordedAsync(conn, handled)).IsTrue()
      .Because("a processed completion is the moment the service had its chance to react, so a repair of it must know");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT EXISTS (SELECT 1 FROM wh_inbox WHERE message_id = $1)", handled))
      .IsFalse().Because("the completion still deletes the row, as before");
  }

  [Test]
  public async Task ACompletionThatIsNotProcessed_ACommand_AndAnUntimedId_AreNotRecordedAsync() {
    var unprocessed = TrackedGuid.New().Value;
    var command = TrackedGuid.New().Value;
    var untimed = TrackedGuid.NewRandom().Value;
    await _coordinator().StoreInboxMessagesAsync(
      [_message(unprocessed, isEvent: true), _message(command, isEvent: false), _message(untimed, isEvent: true)], partitionCount: 100);
    await using var conn = await _openAsync();

    await _completeAsync(conn, unprocessed, status: 1);
    await _completeAsync(conn, command, status: 2);
    await _completeAsync(conn, untimed, status: 2);

    await Assert.That(await _recordedAsync(conn, unprocessed)).IsFalse().Because("only a processed completion handled the event");
    await Assert.That(await _recordedAsync(conn, command)).IsFalse().Because("repair redelivers events, never commands");
    await Assert.That(await _recordedAsync(conn, untimed)).IsFalse()
      .Because("an id that carries no time is never run on repair, so its record would only cost");
  }

  [Test]
  public async Task TheLookup_AnswersTheHandledSubset_AndTheCoverageStartAsync() {
    var handled = TrackedGuid.New().Value;
    var never = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    await _executeAsync(conn, "INSERT INTO wh_receptor_handled (event_id) VALUES ($1)", handled);
    var now = await _nowAsync(conn);

    var answer = await _coordinator().LookupHandledEventsAsync([handled, never]);
    var empty = await _coordinator().LookupHandledEventsAsync([]);

    await Assert.That(answer.Handled).IsEquivalentTo([handled]);
    await Assert.That(answer.CoversFrom.HasValue).IsTrue();
    await Assert.That(answer.CoversFrom!.Value).IsLessThanOrEqualTo(now)
      .Because("coverage started when the ledger was created, before anything was asked");
    await Assert.That(empty.Handled).IsEmpty();
  }

  [Test]
  public async Task TheSweep_MovesCoverageUpToTheCutoff_ThenRemovesOlderRecordsAsync() {
    await using var conn = await _openAsync();
    var now = await _nowAsync(conn);
    await _executeAsync(conn, "UPDATE wh_receptor_handled_coverage SET covers_from = $1", now - TimeSpan.FromDays(30));
    var old = Guid.CreateVersion7(now - TimeSpan.FromDays(10));
    var recent = Guid.CreateVersion7(now - TimeSpan.FromDays(1));
    await _executeAsync(conn, "INSERT INTO wh_receptor_handled (event_id) VALUES ($1), ($2)", old, recent);

    var removed = await _coordinator().SweepHandledLedgerAsync(now, TimeSpan.FromDays(7));

    await Assert.That(removed).IsEqualTo(1L);
    await Assert.That(await _recordedAsync(conn, old)).IsFalse();
    await Assert.That(await _recordedAsync(conn, recent)).IsTrue();
    var coversFrom = await _scalarAsync<DateTime>(conn, "SELECT covers_from FROM wh_receptor_handled_coverage");
    await Assert.That(new DateTimeOffset(DateTime.SpecifyKind(coversFrom, DateTimeKind.Utc))).IsEqualTo(now - TimeSpan.FromDays(7))
      .Because("records older than the cutoff are gone, so coverage now starts at the cutoff");
  }

  /// <summary>
  /// The clamp's other half: raising the window keeps the coverage where it is, so the lookback in effect grows into the
  /// new window only as the ledger fills, never by reading the records already removed as never handled.
  /// </summary>
  [Test]
  public async Task AWindowGrownPastTheCoverage_NeverMovesTheCoverageBackAsync() {
    await using var conn = await _openAsync();
    var now = await _nowAsync(conn);
    await _executeAsync(conn, "UPDATE wh_receptor_handled_coverage SET covers_from = $1", now - TimeSpan.FromDays(30));
    var coordinator = _coordinator();
    await coordinator.SweepHandledLedgerAsync(now, TimeSpan.FromDays(7));

    var removed = await coordinator.SweepHandledLedgerAsync(now, TimeSpan.FromDays(30));
    var answer = await coordinator.LookupHandledEventsAsync([TrackedGuid.New().Value]);

    await Assert.That(removed).IsEqualTo(0L);
    await Assert.That(answer.CoversFrom).IsEqualTo(now - TimeSpan.FromDays(7));
  }

  [Test]
  public async Task OncePartitionsAreMaintained_TheSweepClearsOnlyTheDefaultPartitionAsync() {
    await using var conn = await _openAsync();
    var now = await _nowAsync(conn);
    var partitionedAt = now - TimeSpan.FromDays(10);
    await _executeAsync(conn,
      "INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('partition_maintenance_enabled', 'true', 'boolean') " +
      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value");
    await _executeAsync(conn, "SELECT count(*) FROM wh_partition_maintain('wh_receptor_handled', INTERVAL '1 day', 1, NULL, $1)", partitionedAt);
    var inAPartition = Guid.CreateVersion7(partitionedAt);
    var inTheDefault = Guid.CreateVersion7(now - TimeSpan.FromDays(20));
    await _executeAsync(conn, "INSERT INTO wh_receptor_handled (event_id) VALUES ($1), ($2)", inAPartition, inTheDefault);

    var removed = await _coordinator().SweepHandledLedgerAsync(now, TimeSpan.FromDays(7));

    await Assert.That(removed).IsEqualTo(1L);
    await Assert.That(await _recordedAsync(conn, inTheDefault)).IsFalse();
    await Assert.That(await _recordedAsync(conn, inAPartition)).IsTrue()
      .Because("a record in a partition goes with its partition, which the partition manager drops whole");
  }
}
