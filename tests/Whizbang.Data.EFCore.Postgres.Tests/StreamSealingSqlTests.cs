// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>A terminal event: storing one ends its stream.</summary>
[TerminalEvent]
public sealed record SealingProbeAccountClosed : IEvent {
  /// <summary>The account, which is the stream.</summary>
  [StreamId]
  public Guid AccountId { get; init; }
}

/// <summary>An ordinary Sourced event on the same kind of stream.</summary>
public sealed record SealingProbeDeposited : IEvent {
  /// <summary>The account, which is the stream.</summary>
  [StreamId]
  public Guid AccountId { get; init; }
}

/// <summary>
/// Developer-driven stream finalization and sealing (#1733, WS-3 S4, migration 266). A stream is sealed by
/// <see cref="IStreamFinalizer.FinalizeAsync"/> or by storing a <see cref="TerminalEventAttribute">terminal event</see>,
/// and a sealed stream refuses every later append: a direct append throws <see cref="StreamSealedException"/>, and an
/// outbox or inbox write dead-letters the refused event alone while the rest of the write is stored.
/// </summary>
/// <remarks>
/// The chain is driven with a fixed instant wherever a test reads the seal time back, so the time it asserts is the one it
/// passed in.
/// </remarks>
/// <docs>fundamentals/events/stream-end-of-life#sealing-a-stream</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/266_SealedStreamsRefuseAppends.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StreamSealingSqlTests : EFCoreTestBase {
  private const string TERMINAL_TYPE = "Probe.Contracts.AccountClosed, Probe.Contracts";
  private const string EVENT_TYPE = "Probe.Contracts.Deposited, Probe.Contracts";
  private const int FLAG_EPHEMERAL = 8;
  private const int FLAG_TERMINAL = 64;
  private const int STREAM_SEALED_REASON = 25;
  private const string STREAM_SEALED_STATE = "WHS01";

  private static readonly DateTimeOffset _t0 = new(2026, 10, 29, 12, 10, 0, TimeSpan.Zero);

  // ===== A terminal event seals its stream in the transaction that stores it =====

  [Test]
  public async Task TerminalEvent_OnTheOutbox_SealsTheStream_InTheTransactionThatStoresItAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, (stream, EVENT_TYPE, 0));

    // Inside one transaction: the terminal event is stored and the stream sealed by the same statement...
    await using (var tx = await conn.BeginTransactionAsync()) {
      var ids = await _emitOutboxAsync(conn, _t0.AddMinutes(1), (stream, TERMINAL_TYPE, FLAG_TERMINAL));
      var inside = await _rowAsync(conn, stream);
      await Assert.That(inside!.State).IsEqualTo("Sealed");
      await Assert.That(inside.SealedAt).IsEqualTo(_t0.AddMinutes(1));
      await Assert.That(inside.SealedReason).IsEqualTo($"Terminal event Probe.Contracts.AccountClosed (event {ids[0]})")
        .Because("the reason names the terminal event that ended the stream");
      await Assert.That(await _storedCountAsync(conn, stream)).IsEqualTo(2L);
      // ...so a rollback undoes both: there is never a stored terminal event on an open stream, or a seal without one.
      await tx.RollbackAsync();
    }

    var after = await _rowAsync(conn, stream);
    await Assert.That(after!.State).IsEqualTo("Active");
    await Assert.That(after.SealedAt).IsNull();
    await Assert.That(after.SealedReason).IsNull();
    await Assert.That(await _storedCountAsync(conn, stream)).IsEqualTo(1L);
  }

  [Test]
  public async Task TerminalEvent_DirectAppend_SealsTheStream_AndTheNextAppendThrowsAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    await store.AppendAsync(stream, _envelope(new SealingProbeDeposited { AccountId = stream }));
    var closed = _envelope(new SealingProbeAccountClosed { AccountId = stream });

    await store.AppendAsync(stream, closed);

    var conn = await _openAsync(context);
    var row = await _rowAsync(conn, stream);
    await Assert.That(row!.State).IsEqualTo("Sealed")
      .Because("[TerminalEvent] is stamped as EventFlags.Terminal on the direct append, and wh_append_event seals on it");
    await Assert.That(row.SealedReason).IsEqualTo(
      $"Terminal event {typeof(SealingProbeAccountClosed).FullName} (event {closed.MessageId.Value})");
    var thrown = await Assert.That(async () =>
        await store.AppendAsync(stream, _envelope(new SealingProbeDeposited { AccountId = stream })))
      .Throws<StreamSealedException>();
    await Assert.That(thrown!.StreamId).IsEqualTo(stream);
    await Assert.That(thrown.SealedAt).IsEqualTo(row.SealedAt);
    await Assert.That(thrown.Reason).IsEqualTo(row.SealedReason);
    await Assert.That(await _storedCountAsync(conn, stream)).IsEqualTo(2L).Because("the refused append stores nothing");
  }

  [Test]
  public async Task TerminalEvent_OnTheInbox_SealsTheStream_AndLaterRowsAreRefusedAsync() {
    await using var dbContext = CreateDbContext();
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(3);
    var before = _received(stream, EVENT_TYPE, EventFlags.None, ids[0]);
    var terminal = _received(stream, TERMINAL_TYPE, EventFlags.Terminal, ids[1]);
    var after = _received(stream, EVENT_TYPE, EventFlags.None, ids[2]);

    var conn = await _receiveAsync(dbContext, after, terminal, before);

    await Assert.That(await _storedIdsAsync(conn, stream)).IsEquivalentTo([before.MessageId, terminal.MessageId])
      .Because("a received stream ends where its terminal event stands, as the producer's did");
    await Assert.That((await _rowAsync(conn, stream))!.State).IsEqualTo("Sealed");
    var dead = await _deadLetterAsync(conn, after.MessageId);
    await Assert.That(dead!.SourceTable).IsEqualTo("wh_inbox");
    await Assert.That(dead.FailureReason).IsEqualTo(STREAM_SEALED_REASON);
  }

  [Test]
  public async Task TerminalFlag_OnAnEphemeralEvent_SealsNothingAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();

    await _emitOutboxAsync(conn, _t0, (stream, TERMINAL_TYPE, FLAG_TERMINAL | FLAG_EPHEMERAL));
    await _emitOutboxAsync(conn, _t0.AddMinutes(1), (stream, TERMINAL_TYPE, FLAG_EPHEMERAL));

    await Assert.That(await _rowAsync(conn, stream)).IsNull()
      .Because("an ephemeral stream has no lifecycle row to seal: it ends by its time to live");
    await Assert.That(await _storedCountAsync(conn, stream)).IsEqualTo(2L)
      .Because("nothing seals an ephemeral stream, so nothing after the flagged event is refused");
  }

  // ===== A sealed stream refuses every append =====

  [Test]
  public async Task DirectAppend_ToASealedStream_ThrowsStreamSealedException_AndStoresNothingAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    await store.AppendAsync(stream, _envelope(new SealingProbeDeposited { AccountId = stream }));
    await _finalizer(_t0).FinalizeAsync(stream, "Account closed by the owner");

    var thrown = await Assert.That(async () =>
        await store.AppendAsync(stream, _envelope(new SealingProbeDeposited { AccountId = stream })))
      .Throws<StreamSealedException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(stream);
    await Assert.That(thrown.SealedAt).IsEqualTo(_t0);
    await Assert.That(thrown.Reason).IsEqualTo("Account closed by the owner");
    await Assert.That(thrown.Message).IsEqualTo(
      $"Stream {stream} was sealed at 2026-10-29T12:10:00.000000Z and refuses appends. Reason: Account closed by the owner");
    var conn = await _openAsync(context);
    await Assert.That(await _storedCountAsync(conn, stream)).IsEqualTo(1L);

    // The control: the same append to an open stream is stored.
    var open = (Guid)TrackedGuid.New();
    await store.AppendAsync(open, _envelope(new SealingProbeDeposited { AccountId = open }));
    await Assert.That(await _storedCountAsync(conn, open)).IsEqualTo(1L);
  }

  [Test]
  public async Task OutboxAppend_ToASealedStream_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync() {
    await using var conn = await _openAsync();
    var sealedStream = (Guid)TrackedGuid.New();
    var openStream = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, (sealedStream, EVENT_TYPE, 0));
    await _finalizer(_t0).FinalizeAsync(sealedStream, "Closed for audit");

    var ids = await _emitOutboxAsync(conn, _t0.AddMinutes(1),
      (sealedStream, EVENT_TYPE, 0), (openStream, EVENT_TYPE, 0), (openStream, EVENT_TYPE, 0));

    await Assert.That(await _storedCountAsync(conn, sealedStream)).IsEqualTo(1L)
      .Because("the sealed stream takes no new event");
    await Assert.That(await _storedCountAsync(conn, openStream)).IsEqualTo(2L)
      .Because("a refusal is one event's failure; the other streams in the same batch are stored");
    await Assert.That(await _outboxCountAsync(conn, ids[0])).IsEqualTo(0L)
      .Because("the refused row is moved out of the outbox, so it is neither published nor retried");
    var dead = await _deadLetterAsync(conn, ids[0]);
    await Assert.That(dead).IsNotNull();
    await Assert.That(dead!.SourceTable).IsEqualTo("wh_outbox");
    await Assert.That(dead.FailureReason).IsEqualTo(STREAM_SEALED_REASON);
    await Assert.That(dead.ErrorText).IsEqualTo(
      $"Stream {sealedStream} was sealed at 2026-10-29T12:10:00.000000Z and refuses appends. Reason: Closed for audit")
      .Because("the dead letter carries the same refusal a direct append throws");
    await Assert.That(await _deadLetterAsync(conn, ids[1])).IsNull();
  }

  [Test]
  public async Task StoreOutboxMessages_ToASealedStream_DeadLettersTheEvent_ThroughTheCoordinatorAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var stream = (Guid)TrackedGuid.New();
    var other = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_outbox(stream)], partitionCount: 10000);
    await _finalizer(_t0).FinalizeAsync(stream, "Closed");
    var refused = _outbox(stream);
    var stored = _outbox(other);

    await coordinator.StoreOutboxMessagesAsync([refused, stored], partitionCount: 10000);

    var conn = await _openAsync(dbContext);
    await Assert.That((await _deadLetterAsync(conn, refused.MessageId))!.FailureReason).IsEqualTo(STREAM_SEALED_REASON)
      .Because("the refusal sits in the emit chain, so a write through the coordinator cannot go around it");
    await Assert.That(await _storedCountAsync(conn, other)).IsEqualTo(1L);
  }

  [Test]
  public async Task StoreOutboxMessages_AnAwaitedWriteToASealedStream_ThrowsStreamSealedException_AndStoresNothingAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var stream = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_outbox(stream)], partitionCount: 10000);
    await _finalizer(_t0).FinalizeAsync(stream, "Closed by the owner");
    // The flag the immediate and scoped strategies set on a flush their caller awaits.
    var awaited = _outbox(stream) with { RaiseOnMixedStream = true };
    var unrelated = _outbox((Guid)TrackedGuid.New()) with { RaiseOnMixedStream = true };

    var thrown = await Assert.That(async () =>
        await coordinator.StoreOutboxMessagesAsync([awaited, unrelated], partitionCount: 10000))
      .Throws<StreamSealedException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(stream);
    await Assert.That(thrown.SealedAt).IsEqualTo(_t0);
    await Assert.That(thrown.Reason).IsEqualTo("Closed by the owner")
      .Because("a caller that awaits the write is told, as the mixed-stream refusal tells it (#1546)");
    var conn = await _openAsync(dbContext);
    await Assert.That(await _outboxCountAsync(conn, unrelated.MessageId)).IsEqualTo(0L)
      .Because("an awaited write is all or nothing: the caller sees the failure and nothing of the write is kept");
    await Assert.That(await _deadLetterAsync(conn, awaited.MessageId)).IsNull()
      .Because("the caller has the refusal in hand; dead-lettering it as well would report it twice");
    await Assert.That(await _storedCountAsync(conn, stream)).IsEqualTo(1L);
  }

  [Test]
  public async Task StoreOutboxMessages_TheSameWriteNotAwaited_IsDeadLettered_AndAnAwaitedWriteToAnOpenStreamIsStoredAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var stream = (Guid)TrackedGuid.New();
    var open = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_outbox(stream)], partitionCount: 10000);
    await _finalizer(_t0).FinalizeAsync(stream, "Closed");
    var fireAndForget = _outbox(stream);
    var awaitedOpen = _outbox(open) with { RaiseOnMixedStream = true };

    await coordinator.StoreOutboxMessagesAsync([fireAndForget], partitionCount: 10000);
    await coordinator.StoreOutboxMessagesAsync([awaitedOpen], partitionCount: 10000);

    var conn = await _openAsync(dbContext);
    await Assert.That((await _deadLetterAsync(conn, fireAndForget.MessageId))!.FailureReason).IsEqualTo(STREAM_SEALED_REASON)
      .Because("a write nobody awaits has no caller to tell, so the refused event is dead-lettered");
    await Assert.That(await _storedCountAsync(conn, open)).IsEqualTo(1L)
      .Because("the control: an awaited write to an open stream is stored, so the raise is the seal's doing");
  }

  [Test]
  public async Task StoreOutboxMessages_AnAwaitedWriteAfterATerminalEventInTheSameWrite_ThrowsAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(2);
    var terminal = CreateTestOutboxMessage(ids[0], "sealing-topic", stream, isEvent: true) with {
      MessageType = TERMINAL_TYPE,
      Flags = EventFlags.Terminal,
      RaiseOnMixedStream = true,
    };
    var after = CreateTestOutboxMessage(ids[1], "sealing-topic", stream, isEvent: true) with {
      MessageType = EVENT_TYPE,
      RaiseOnMixedStream = true,
    };

    var thrown = await Assert.That(async () =>
        await coordinator.StoreOutboxMessagesAsync([after, terminal], partitionCount: 10000))
      .Throws<StreamSealedException>();

    await Assert.That(thrown!.Reason).IsEqualTo($"Terminal event Probe.Contracts.AccountClosed (event {ids[0]})");
    var conn = await _openAsync(dbContext);
    await Assert.That(await _storedCountAsync(conn, stream)).IsEqualTo(0L)
      .Because("the awaited write rolls back whole, the terminal event with it");
  }

  [Test]
  public async Task OutboxBatch_EventsAfterATerminalEvent_AreDeadLettered_AndTheOnesBeforeItAreStoredAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(4);

    // Out of order on purpose: version order is message id order, whatever order the batch arrives in.
    await _emitOutboxAsync(conn, _t0,
      [ids[3], ids[1], ids[0], ids[2]],
      (stream, EVENT_TYPE, 0), (stream, TERMINAL_TYPE, FLAG_TERMINAL), (stream, EVENT_TYPE, 0), (stream, EVENT_TYPE, 0));

    var versions = await _versionsAsync(conn, stream);
    List<(Guid, long)> expected = [(ids[0], 1L), (ids[1], 2L)];
    await Assert.That(versions).IsEquivalentTo(expected)
      .Because("the terminal event ends the stream at its own version; what sorts after it is refused, not stored");
    foreach (var refused in new[] { ids[2], ids[3] }) {
      var dead = await _deadLetterAsync(conn, refused);
      await Assert.That(dead!.FailureReason).IsEqualTo(STREAM_SEALED_REASON);
      await Assert.That(dead.ErrorText).IsEqualTo(
        $"Stream {stream} was sealed at 2026-10-29T12:10:00.000000Z and refuses appends. Reason: Terminal event Probe.Contracts.AccountClosed (event {ids[1]})")
        .Because("an event after a terminal event in the same write is refused as if the seal had come first");
    }
    await Assert.That((await _rowAsync(conn, stream))!.SealedAt).IsEqualTo(_t0);
  }

  [Test]
  public async Task InboxAppend_ToASealedStream_DeadLettersTheRow_AndStoresTheRestAsync() {
    await using var dbContext = CreateDbContext();
    var stream = (Guid)TrackedGuid.New();
    var other = (Guid)TrackedGuid.New();
    var conn = await _receiveAsync(dbContext, _received(stream, EVENT_TYPE, EventFlags.None));
    await _finalizer(_t0).FinalizeAsync(stream, "Closed");

    var refused = _received(stream, EVENT_TYPE, EventFlags.None);
    var stored = _received(other, EVENT_TYPE, EventFlags.None);
    await _receiveAsync(dbContext, refused, stored);

    await Assert.That(await _storedIdsAsync(conn, other)).IsEquivalentTo([stored.MessageId])
      .Because("the claim's other rows are stored as if the refused one were not there");
    await Assert.That(await _storedCountAsync(conn, stream)).IsEqualTo(1L);
    var dead = await _deadLetterAsync(conn, refused.MessageId);
    await Assert.That(dead!.SourceTable).IsEqualTo("wh_inbox");
    await Assert.That(dead.FailureReason).IsEqualTo(STREAM_SEALED_REASON);
    await Assert.That(dead.ErrorText).StartsWith($"Stream {stream} was sealed at ");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_inbox_state WHERE message_id = @id AND processed_at IS NULL",
      ("id", refused.MessageId))).IsEqualTo(0L)
      .Because("the refused row is completed where it stands, so the claim never offers it again");
  }

  // ===== Finalizing =====

  [Test]
  public async Task Finalize_SealsTheStream_WithTheTimeAndTheReasonAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, (stream, EVENT_TYPE, 0));

    var result = await _finalizer(_t0.AddHours(2)).FinalizeAsync(stream, "Policy matured");

    await Assert.That(result).IsEqualTo(new StreamFinalizeResult(stream, StreamFinalizeStatus.Sealed, _t0.AddHours(2), "Policy matured"));
    var row = await _rowAsync(conn, stream);
    await Assert.That(row).IsEqualTo(new LedgerSeal("Sealed", _t0.AddHours(2), "Policy matured"))
      .Because("the seal time is the finalizer's clock, and the reason is stored with it");
  }

  [Test]
  public async Task Finalize_IsIdempotent_TheSecondCallKeepsTheFirstSealAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, (stream, EVENT_TYPE, 0));
    await _finalizer(_t0).FinalizeAsync(stream, "First");

    var again = await _finalizer(_t0.AddDays(1)).FinalizeAsync(stream, "Second");

    await Assert.That(again).IsEqualTo(new StreamFinalizeResult(stream, StreamFinalizeStatus.AlreadySealed, _t0, "First"))
      .Because("finalizing a sealed stream changes nothing and reports the seal it already has");
    await Assert.That(await _rowAsync(conn, stream)).IsEqualTo(new LedgerSeal("Sealed", _t0, "First"));
  }

  [Test]
  public async Task Finalize_AStreamWithNoLedgerRowYet_BuildsItFromTheEvents_AndSealsItAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, (stream, EVENT_TYPE, 0), (stream, EVENT_TYPE, 0));
    // The backfill has not reached the stream yet: it has events and no row.
    await _executeAsync(conn, "DELETE FROM wh_stream_lifecycle WHERE stream_id = @s", ("s", stream));

    var result = await _finalizer(_t0.AddHours(1)).FinalizeAsync(stream, "Closed");

    await Assert.That(result.Status).IsEqualTo(StreamFinalizeStatus.Sealed);
    await Assert.That(await _scalarAsync(conn,
      "SELECT count(*) FROM wh_stream_lifecycle WHERE stream_id = @s AND last_version = 2 AND stream_type = 'Probe.Contracts.Deposited' AND first_appended_at = @t0",
      ("s", stream), ("t0", _t0))).IsEqualTo(1L)
      .Because("the row is built from the stream's events by the ledger's rules, so the backfill finds it already right");
    await Assert.That(await _rowAsync(conn, stream)).IsEqualTo(new LedgerSeal("Sealed", _t0.AddHours(1), "Closed"));
  }

  [Test]
  public async Task Finalize_AStreamWithNoSourcedEvent_HasNothingToSealAsync() {
    await using var conn = await _openAsync();
    var ephemeral = (Guid)TrackedGuid.New();
    var never = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, (ephemeral, EVENT_TYPE, FLAG_EPHEMERAL));
    var finalizer = _finalizer(_t0);

    foreach (var stream in new[] { ephemeral, never }) {
      await Assert.That(await finalizer.FinalizeAsync(stream, "Closed"))
        .IsEqualTo(new StreamFinalizeResult(stream, StreamFinalizeStatus.NothingToSeal, null, null))
        .Because("an ephemeral stream ends by its time to live, and a stream never written has nothing to end");
      await Assert.That(await _rowAsync(conn, stream)).IsNull();
    }
    // The empty stream id is "no stream", answered by the function itself as well as refused by the finalizer.
    await Assert.That(await _scalarAsync(conn,
      $"SELECT count(*) FROM wh_finalize_stream('{Guid.Empty}'::uuid, 'Closed', NOW()) WHERE finalize_status = 'nothing_to_seal'"))
      .IsEqualTo(1L);
  }

  [Test]
  public async Task Finalize_WithoutAReason_IsRefusedAsync() {
    await using var conn = await _openAsync();
    var finalizer = _finalizer(_t0);
    var stream = (Guid)TrackedGuid.New();

    await Assert.That(async () => await finalizer.FinalizeAsync(stream, " ")).Throws<ArgumentException>()
      .Because("the reason is carried by every refusal, so a seal without one would explain nothing");
    await Assert.That(async () => await finalizer.FinalizeAsync(stream, null!)).Throws<ArgumentException>();
    await Assert.That(async () => await finalizer.FinalizeAsync(Guid.Empty, "Closed")).Throws<ArgumentException>()
      .Because("the empty stream id is \"no stream\"");
    var raised = await Assert.That(async () => await _executeAsync(conn,
        "SELECT * FROM wh_finalize_stream(@s, '  ', NOW())", ("s", stream)))
      .Throws<PostgresException>();
    await Assert.That(raised!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidParameterValue)
      .Because("the function refuses it too, for a caller that is not the finalizer");
  }

  [Test]
  public async Task Finalize_WaitsForTheStreamsAppendLockAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, (stream, EVENT_TYPE, 0));
    // A second session holds the key the emit chains and the direct append take for this stream.
    await using var appender = new NpgsqlConnection(ConnectionString);
    await appender.OpenAsync();
    await using var appending = await appender.BeginTransactionAsync();
    await _executeAsync(appender, "SELECT pg_advisory_xact_lock(hashtext('wh_event_store:' || @s::text))", ("s", stream));

    await using (var held = await conn.BeginTransactionAsync()) {
      await _executeAsync(conn, "SET LOCAL lock_timeout = '100ms'");
      var waited = await Assert.That(async () => await _executeAsync(conn,
          "SELECT * FROM wh_finalize_stream(@s, 'Closed', NOW())", ("s", stream)))
        .Throws<PostgresException>();
      await Assert.That(waited!.SqlState).IsEqualTo(PostgresErrorCodes.LockNotAvailable)
        .Because("the seal waits for the append in flight, so that append is stored before the stream closes");
      await held.RollbackAsync();
    }

    // The control: once the append commits, the same call seals.
    await appending.CommitAsync();
    await Assert.That((await _finalizer(_t0).FinalizeAsync(stream, "Closed")).Status).IsEqualTo(StreamFinalizeStatus.Sealed);
  }

  [Test]
  public async Task Finalize_AnArchivedStream_StaysArchived_AndARestoreReturnsItSealedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, (stream, EVENT_TYPE, 0), (stream, EVENT_TYPE, 0));
    var archive = new PostgresTableArchiveStore(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);
    await Assert.That((await archive.WriteAsync(ArchiveBatch.WholeStream(stream))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);

    var result = await _finalizer(_t0).FinalizeAsync(stream, "Closed");

    await Assert.That(result.Status).IsEqualTo(StreamFinalizeStatus.Sealed);
    await Assert.That(await _rowAsync(conn, stream)).IsEqualTo(new LedgerSeal("Archived", _t0, "Closed"))
      .Because("the row stays the archive's tombstone; the seal is sealed_at, not the state");
    await archive.RestoreAsync(stream);
    await Assert.That(await _rowAsync(conn, stream)).IsEqualTo(new LedgerSeal("Sealed", _t0, "Closed"))
      .Because("a restore brings the events back, not the stream's life: a sealed stream comes back sealed");
    var refused = await Assert.That(async () => await _executeAsync(conn,
        "SELECT wh_append_event(gen_random_uuid(), @s, @s, 'Agg', 'T, A', '{}'::jsonb, '{}'::jsonb, NULL::jsonb, NOW())",
        ("s", stream)))
      .Throws<PostgresException>();
    await Assert.That(refused!.SqlState).IsEqualTo(STREAM_SEALED_STATE);
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheFinalizer_OnItsDataSource_WithTheRegisteredClockAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, (stream, EVENT_TYPE, 0));
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<FinalizerRegistrationDbContext>(options => options.UseNpgsql(ConnectionString));
    services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));
    services.AddSingleton<TimeProvider>(new FakeTimeProvider(_t0.AddMinutes(30)));
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<FinalizerRegistrationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();

    var finalizer = provider.GetRequiredService<IStreamFinalizer>();
    var result = await finalizer.FinalizeAsync(stream, "Closed");

    await Assert.That(finalizer).IsTypeOf<PostgresStreamFinalizer>();
    await Assert.That(result).IsEqualTo(new StreamFinalizeResult(stream, StreamFinalizeStatus.Sealed, _t0.AddMinutes(30), "Closed"))
      .Because("the driver hands the finalizer the host's clock, so a test host's fake clock decides the seal time");
  }

  /// <summary>A context with an empty model in the default schema: enough for the driver to register its stores.</summary>
  private sealed class FinalizerRegistrationDbContext(DbContextOptions<FinalizerRegistrationDbContext> options) : DbContext(options);

  // ===== The append path's cost: one probe of the sealed streams' index =====

  [Test]
  public async Task SealedProbe_ReadsOnlyThePartialIndexOfSealedStreamsAsync() {
    await using var conn = await _openAsync();
    var streams = Enumerable.Range(0, 50).Select(_ => (Guid)TrackedGuid.New()).ToArray();
    foreach (var s in streams) {
      await _emitOutboxAsync(conn, _t0, (s, EVENT_TYPE, 0));
    }
    // A ledger of a working size, so the plan is the one a real store gets: one sealed stream among thousands.
    await _executeAsync(conn, """
      INSERT INTO wh_stream_lifecycle (stream_id, stream_type, first_appended_at, last_appended_bucket, last_version)
      SELECT gen_random_uuid(), 'Probe.Contracts.Deposited', @t0, @t0, 1 FROM generate_series(1, 20000)
      """, ("t0", _t0));
    await _finalizer(_t0).FinalizeAsync(streams[0], "Closed");
    await _executeAsync(conn, "ANALYZE wh_stream_lifecycle");

    await using var explain = conn.CreateCommand();
    explain.CommandText = """
      EXPLAIN (FORMAT JSON)
      SELECT 1 FROM wh_stream_lifecycle l WHERE l.stream_id = ANY(@ids) AND l.sealed_at IS NOT NULL
      """;
    explain.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, streams);
    var plan = (string)(await explain.ExecuteScalarAsync())!;

    await Assert.That(plan).Contains("idx_stream_lifecycle_sealed")
      .Because("the check every write makes is answered by an index as small as the number of sealed streams");
    await Assert.That(await _scalarAsync(conn, """
      SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
      WHERE i.indrelid = 'wh_stream_lifecycle'::regclass AND c.relname = 'idx_stream_lifecycle_sealed'
        AND pg_get_expr(i.indpred, i.indrelid) = '(sealed_at IS NOT NULL)'
      """)).IsEqualTo(1L);
  }

  // =================================================================================================

  private sealed record LedgerSeal(string State, DateTimeOffset? SealedAt, string? SealedReason);

  private sealed record DeadLetterRow(string SourceTable, int FailureReason, string ErrorText);

  private PostgresStreamFinalizer _finalizer(DateTimeOffset now) =>
    new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, timeProvider: new FakeTimeProvider(now));

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static Guid[] _orderedIds(int count) =>
    [.. Enumerable.Range(0, count).Select(_ => (Guid)TrackedGuid.New()).Order()];

  private static MessageEnvelope<TMessage> _envelope<TMessage>(TMessage payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static OutboxMessage _outbox(Guid stream) =>
    CreateTestOutboxMessage((Guid)TrackedGuid.New(), "sealing-topic", stream, isEvent: true) with { MessageType = EVENT_TYPE };

  private static Task<Guid[]> _emitOutboxAsync(NpgsqlConnection conn, DateTimeOffset now, params (Guid Stream, string Type, int Flags)[] events) =>
    _emitOutboxAsync(conn, now, [.. events.Select(_ => (Guid)TrackedGuid.New())], events);

  // Queues the events in the outbox and runs the outbox emit chain over them, as store_outbox_messages does.
  private static async Task<Guid[]> _emitOutboxAsync(
      NpgsqlConnection conn, DateTimeOffset now, Guid[] ids, params (Guid Stream, string Type, int Flags)[] events) {
    var instanceId = (Guid)TrackedGuid.New();
    for (var i = 0; i < events.Length; i++) {
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'sealing-topic', @type, 'env', jsonb_build_object('p', jsonb_build_object('n', 1)),
           '{}'::jsonb, NULL, 1, 0, @inst, @lease, 0, @sid, true, @flags, @now)
        """,
        ("id", ids[i]), ("type", events[i].Type), ("inst", instanceId), ("lease", now.AddMinutes(5)),
        ("sid", events[i].Stream), ("flags", events[i].Flags), ("now", now));
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    emit.Parameters.AddWithValue("inst", instanceId);
    emit.Parameters.AddWithValue("lease", now.AddMinutes(5));
    emit.Parameters.AddWithValue(nameof(now), now);
    _ = await emit.ExecuteScalarAsync();
    return ids;
  }

  private static InboxMessage _received(Guid streamId, string messageType, EventFlags flags, Guid? id = null) {
    var messageId = id ?? (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.SerializeToElement(new { probe = true }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "ProbeHandler",
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = messageType,
      StreamId = streamId,
      IsEvent = true,
      Flags = flags,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
    };
  }

  // Stores the rows in the inbox, leases them to one instance and runs the claim's emit chain over them.
  private static async Task<NpgsqlConnection> _receiveAsync(WorkCoordinationDbContext dbContext, params InboxMessage[] rows) {
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.StoreInboxMessagesAsync(rows, partitionCount: 100);
    var conn = await _openAsync(dbContext);
    var instanceId = (Guid)TrackedGuid.New();
    Guid[] ids = [.. rows.Select(r => r.MessageId)];
    await _executeAsync(conn,
      "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)",
      ("inst", instanceId), ("ids", ids));
    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)",
      ("inst", instanceId));
    return conn;
  }

  private static async Task<LedgerSeal?> _rowAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT state, sealed_at, sealed_reason FROM wh_stream_lifecycle WHERE stream_id = @s";
    cmd.Parameters.AddWithValue("s", stream);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return null;
    }
    return new LedgerSeal(
      reader.GetString(0),
      await reader.IsDBNullAsync(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
      await reader.IsDBNullAsync(2) ? null : reader.GetString(2));
  }

  private static Task<long> _storedCountAsync(NpgsqlConnection conn, Guid stream) =>
    _scalarAsync(conn, "SELECT count(*) FROM wh_event_store WHERE stream_id = @s", ("s", stream));

  private static async Task<List<Guid>> _storedIdsAsync(NpgsqlConnection conn, Guid stream) =>
    [.. (await _versionsAsync(conn, stream)).Select(v => v.EventId)];

  private static async Task<List<(Guid EventId, long Version)>> _versionsAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id, version FROM wh_event_store WHERE stream_id = @s ORDER BY version";
    cmd.Parameters.AddWithValue("s", stream);
    var rows = new List<(Guid, long)>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add((reader.GetGuid(0), reader.GetInt64(1)));
    }
    return rows;
  }

  private static Task<long> _outboxCountAsync(NpgsqlConnection conn, Guid messageId) =>
    _scalarAsync(conn, "SELECT count(*) FROM wh_outbox WHERE message_id = @id", ("id", messageId));

  private static async Task<DeadLetterRow?> _deadLetterAsync(NpgsqlConnection conn, Guid sourceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT source_table, failure_reason, error_text FROM wh_dead_letters WHERE source_id = @id";
    cmd.Parameters.AddWithValue("id", sourceId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return null;
    }
    return new DeadLetterRow(reader.GetString(0), reader.GetInt32(1), reader.GetString(2));
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
