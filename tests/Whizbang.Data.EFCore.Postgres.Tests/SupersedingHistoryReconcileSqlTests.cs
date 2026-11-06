// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>A superseding event type with no key property: one key per stream.</summary>
[Superseding]
public sealed record SupersedingHistoryProbeProfile : IEvent {
  /// <summary>The user, which is the stream.</summary>
  [StreamId]
  public Guid UserId { get; init; }

  /// <summary>Which revision of the profile this is.</summary>
  public int Revision { get; init; }
}

/// <summary>An ordinary event type: never superseding.</summary>
public sealed record SupersedingHistoryProbePlain : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public Guid DocumentId { get; init; }

  /// <summary>A value that would be a key if the type were superseding.</summary>
  public string Model { get; init; } = "";
}

/// <summary>
/// #2110: the events a superseding type stored before it was declared [Superseding] are brought under the superseding
/// rule by the superseding-history-reconcile maintenance step (migration 345): stamped with the flag and the key the
/// write paths would store, and queued for the reaper, which deletes them only past its gates. Each test is built from
/// rows stored the way a write path stored them before the declaration: no flag, no key.
/// </summary>
/// <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/SupersedingHistoryReconcileStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/345_SupersedingHistoryReconcile.sql</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class SupersedingHistoryReconcileSqlTests : EFCoreTestBase {
  private const int SUPERSEDING = 128;
  private const int EPHEMERAL = 8;

  private static readonly string _embedded = TypeNameFormatter.Format(typeof(SupersedingProbeEmbedded));
  private static readonly string _profile = TypeNameFormatter.Format(typeof(SupersedingHistoryProbeProfile));
  private static readonly string _plain = TypeNameFormatter.Format(typeof(SupersedingHistoryProbePlain));
  private static readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();

  // ===== The history is reaped down to the newest of each key =====

  [Test]
  public async Task Reconcile_HistoryStoredBeforeTheDeclaration_IsReapedDownToTheNewestOfEachKey_OnceConsumedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var a1 = await _historicAsync(conn, stream, _embeddedJson(stream, "model-a"));
    var a2 = await _historicAsync(conn, stream, _embeddedJson(stream, "model-a"));
    var b1 = await _historicAsync(conn, stream, _embeddedJson(stream, "model-b"));
    var plain = await _historicAsync(conn, stream, _plainJson(stream, "model-a"), _plain);
    var a3 = await _historicAsync(conn, stream, _embeddedJson(stream, "model-a"));

    var result = await _step().ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(result.EventsStamped).IsEqualTo(4L).Because("every event of the type stored without the flag is stamped");
    await Assert.That(result.EventsQueued).IsEqualTo(2L);
    await Assert.That(await _keyAsync(conn, a1)).IsEqualTo("model-a").Because("the key is read from the stored payload by the generated accessor");
    await Assert.That(await _keyAsync(conn, b1)).IsEqualTo("model-b");
    await Assert.That(await _flagsAsync(conn, a1)).IsEqualTo((long)SUPERSEDING);
    await Assert.That(await _flagsAsync(conn, plain)).IsEqualTo(0L).Because("an event of another type in the same stream is not touched");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([a1, a2]);
    await Assert.That(await _storedAsync(conn, stream)).Count().IsEqualTo(5)
      .Because("the reconcile deletes nothing; the reaper's gates stay the only path to deletion");

    await _stampAsync(conn);
    await Assert.That(await _reapAsync(conn)).IsEqualTo(2L);
    await Assert.That(await _storedAsync(conn, stream)).IsEquivalentTo([b1, plain, a3])
      .Because("only the newest event of each stream, type and key is kept");
  }

  [Test]
  public async Task Reconcile_AnOldEventStampedAfterTheNewerOneOfItsKey_IsQueuedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var old = await _historicAsync(conn, stream, _embeddedJson(stream, "model-a"));
    // Stored once the type is superseding: flagged and keyed by the write path, which queues no unkeyed older event.
    var live = await _appendAsync(conn, stream, _embeddedJson(stream, "model-a"), _embedded, SUPERSEDING, "model-a");
    await Assert.That(await _queuedAsync(conn)).IsEmpty().Because("the control: the write path cannot see the old event's key");

    await _step().ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([old])
      .Because("an event stamped after the newer event of its key queues itself, so the order of stamping never matters");
    await Assert.That(await _flagsAsync(conn, live)).IsEqualTo((long)SUPERSEDING);
  }

  [Test]
  public async Task Reconcile_OldEventsTheGatesProtect_StayAsync() {
    await using var conn = await _openAsync();
    var unconsumed = await _pairAsync(conn);
    var cursorPinned = await _pairAsync(conn);
    var snapshotPinned = await _pairAsync(conn);
    var held = await _pairAsync(conn);
    var free = await _pairAsync(conn);
    await _executeAsync(conn, """
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, instance_id, lease_expiry, priority)
      VALUES (gen_random_uuid(), @s, 'EmbeddingView', @e, 0, 1, 0, NOW(), NULL, NULL, 0)
      """, ("s", unconsumed.Stream), ("e", unconsumed.Old));
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@s, 'P', @e, 0, NOW())",
      ("s", cursorPinned.Stream), ("e", cursorPinned.Old));
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version) " +
      "VALUES (@s, 'P', @e, '{}'::jsonb, 1, 1)", ("s", snapshotPinned.Stream), ("e", snapshotPinned.Old));
    await _executeAsync(conn, "INSERT INTO wh_event_destruction_hold (event_id, hold_until) VALUES (@e, NOW() + INTERVAL '1 day')", ("e", held.Old));

    await _step().ReconcileAsync(_services(), CancellationToken.None);
    await _stampAsync(conn);
    var reaped = await _reapAsync(conn);

    await Assert.That(reaped).IsEqualTo(1L).Because("the control: the old event no gate protects is reaped");
    await Assert.That(await _storedAsync(conn, free.Stream)).IsEquivalentTo([free.New]);
    foreach (var pair in new[] { unconsumed, cursorPinned, snapshotPinned, held }) {
      await Assert.That(await _storedAsync(conn, pair.Stream)).IsEquivalentTo([pair.Old, pair.New])
        .Because("an unconsumed, cursor-pinned, snapshot-pinned or held old event stays queued until its gate opens");
    }
  }

  [Test]
  public async Task Reconcile_ANullKey_IsStampedUnkeyed_AndNeverQueued_AKeylessTypeKeepsOnePerStreamAsync() {
    await using var conn = await _openAsync();
    var nullKeyed = (Guid)TrackedGuid.New();
    var profileStream = (Guid)TrackedGuid.New();
    var n1 = await _historicAsync(conn, nullKeyed, _embeddedJson(nullKeyed, null));
    var n2 = await _historicAsync(conn, nullKeyed, _embeddedJson(nullKeyed, null));
    var p1 = await _historicAsync(conn, profileStream, _profileJson(profileStream, 1), _profile);
    var p2 = await _historicAsync(conn, profileStream, _profileJson(profileStream, 2), _profile);
    var p3 = await _historicAsync(conn, profileStream, _profileJson(profileStream, 3), _profile);

    await _step(typeof(SupersedingProbeEmbedded), typeof(SupersedingHistoryProbeProfile)).ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(await _flagsAsync(conn, n1)).IsEqualTo((long)SUPERSEDING).Because("a null key is stamped, so it is not read again");
    await Assert.That(await _keyAsync(conn, n1)).IsNull().Because("an unknown key is never merged with another, so the event is kept");
    await Assert.That(await _keyAsync(conn, n2)).IsNull();
    await Assert.That(await _keyAsync(conn, p1)).IsEqualTo("").Because("a type with no key property has the empty key");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([p1, p2]).Because("a keyless type keeps one event per stream");
    await Assert.That(await _flagsAsync(conn, p3)).IsEqualTo((long)SUPERSEDING);
  }

  [Test]
  public async Task Reconcile_ATypeThatIsNotSuperseding_IsNotTouchedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var p1 = await _historicAsync(conn, stream, _plainJson(stream, "k"), _plain);
    var p2 = await _historicAsync(conn, stream, _plainJson(stream, "k"), _plain);
    // A walk left by an instance under which the type was superseding.
    await _executeAsync(conn, "INSERT INTO wh_superseding_history (event_type) VALUES (@t)", ("t", _plain));

    var result = await _step().ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(result.EventsStamped).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, p1)).IsEqualTo(0L).Because("only the types registered as superseding are walked");
    await Assert.That(await _flagsAsync(conn, p2)).IsEqualTo(0L);
    await Assert.That(await _queuedAsync(conn)).IsEmpty();
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_superseding_history WHERE last_event_id IS NOT NULL")).IsEqualTo(0L)
      .Because("the walk of a type that is no longer superseding does not move");
  }

  [Test]
  public async Task Reconcile_AnEphemeralEventOfTheType_IsLeftToTheEphemeralReaperAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var e1 = await _appendAsync(conn, stream, _embeddedJson(stream, "k"), _embedded, EPHEMERAL, null);
    var e2 = await _appendAsync(conn, stream, _embeddedJson(stream, "k"), _embedded, EPHEMERAL, null);

    await _step().ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(await _flagsAsync(conn, e1)).IsEqualTo((long)EPHEMERAL).Because("only a Sourced event is part of the durable log the rule keeps");
    await Assert.That(await _flagsAsync(conn, e2)).IsEqualTo((long)EPHEMERAL);
    await Assert.That(await _queuedAsync(conn)).IsEmpty();
  }

  [Test]
  public async Task Reconcile_ReceivedCopies_AreReconciledInTheReceivingStoreAsync() {
    await using var dbContext = CreateDbContext();
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(2);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, _json);
    // Received before the receiving service knew the type was superseding: no flag, no key.
    await coordinator.StoreInboxMessagesAsync([_received(stream, ids[0]), _received(stream, ids[1])], partitionCount: 100);
    await using var conn = await _openAsync();
    var instanceId = (Guid)TrackedGuid.New();
    await _executeAsync(conn,
      "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)",
      ("inst", instanceId), ("ids", ids));
    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)", ("inst", instanceId));
    await Assert.That(await _flagsAsync(conn, ids[0])).IsEqualTo(0L).Because("the control: the copy was stored unflagged");

    await _step().ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(await _keyAsync(conn, ids[0])).IsEqualTo("model-a").Because("each service reconciles its own store");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([ids[0]]);
  }

  // ===== Batches, reruns, crashes and other instances =====

  [Test]
  public async Task Reconcile_ASecondRun_ChangesNothingAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var a1 = await _historicAsync(conn, stream, _embeddedJson(stream, "k"));
    await _historicAsync(conn, stream, _embeddedJson(stream, "k"));
    await _step().ReconcileAsync(_services(), CancellationToken.None);
    var row = await _rowVersionAsync(conn, a1);

    var second = await _step().ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(second.EventsStamped).IsEqualTo(0L);
    await Assert.That(second.EventsQueued).IsEqualTo(0L);
    await Assert.That(second.Status).IsEqualTo("complete");
    await Assert.That(await _rowVersionAsync(conn, a1)).IsEqualTo(row).Because("a reconciled event is never rewritten again");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([a1]);
  }

  [Test]
  public async Task Reconcile_RunsInBoundedBatches_AndResumesFromItsCursor_AfterACrashAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = new List<Guid>();
    for (var i = 0; i < 4; i++) {
      ids.Add(await _historicAsync(conn, stream, _embeddedJson(stream, "k")));
    }
    await _historicAsync(conn, stream, _plainJson(stream, "k"), _plain);

    // A batch that crashed before its commit leaves nothing behind: not a stamp, not a queued event, not a cursor move.
    await _ensureAsync(conn);
    await using (var crashed = await conn.BeginTransactionAsync()) {
      await _executeAsync(conn, "SELECT * FROM wh_superseding_history_begin(@t, @d, 10)", ("t", _embedded), ("d", _definitionName));
      await _executeAsync(conn,
        "SELECT * FROM wh_superseding_history_apply(@t, NULL, @through, 10, 5, @ids, @keys)",
        ("t", _embedded), ("through", ids.Max()), ("ids", ids.ToArray()), ("keys", ids.Select(_ => "k").ToArray()));
      await crashed.RollbackAsync();
    }
    await Assert.That(await _flagsAsync(conn, ids[0])).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn,
      "SELECT count(*) FROM wh_superseding_history WHERE last_event_id IS NOT NULL OR events_stamped > 0 OR completed_at IS NOT NULL")).IsEqualTo(0L);

    var first = await _step(2, 1).ReconcileAsync(_services(), CancellationToken.None);
    var rest = await _step(2, 10).ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(first.Batches).IsEqualTo(1).Because("one batch is the cycle's limit");
    await Assert.That(first.EventsScanned).IsEqualTo(2L).Because("a batch reads a window of the whole store by event id");
    await Assert.That(first.EventsStamped).IsEqualTo(2L);
    await Assert.That(rest.EventsScanned).IsEqualTo(3L).Because("the next cycle resumes past the window the first one read");
    await Assert.That(rest.EventsStamped).IsEqualTo(2L);
    await Assert.That(rest.TypesFinished).IsEqualTo(1);
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo(ids.Take(3).ToList());
  }

  [Test]
  public async Task Reconcile_WhileAnotherInstanceHoldsTheType_SkipsWithoutWaiting_AndDoesNoWorkAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var a1 = await _historicAsync(conn, stream, _embeddedJson(stream, "k"));
    await _historicAsync(conn, stream, _embeddedJson(stream, "k"));
    await _ensureAsync(conn);
    await using var other = await _openAsync();
    await using var hold = await other.BeginTransactionAsync();
    await _executeAsync(other, "SELECT * FROM wh_superseding_history_begin(@t, @d, 10)", ("t", _embedded), ("d", _definitionName));

    var skipped = await _step().ReconcileAsync(_services(), CancellationToken.None);
    await hold.RollbackAsync();
    var ran = await _step().ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(skipped.Status).IsEqualTo("skipped (another instance is reconciling)");
    await Assert.That(skipped.EventsStamped).IsEqualTo(0L);
    await Assert.That(ran.EventsStamped).IsEqualTo(2L).Because("the control: once the other instance is done, this one reconciles");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([a1]);
  }

  [Test]
  public async Task Reconcile_ARowAnotherTransactionHolds_IsLeftForTheNextBatch_WithoutWaitingAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var a1 = await _historicAsync(conn, stream, _embeddedJson(stream, "k"));
    var a2 = await _historicAsync(conn, stream, _embeddedJson(stream, "k"));
    await using var other = await _openAsync();
    await using var hold = await other.BeginTransactionAsync();
    await _executeAsync(other, "SELECT 1 FROM wh_event_store WHERE event_id = @e FOR UPDATE", ("e", a1));

    var heldResult = await _step().ReconcileAsync(_services(), CancellationToken.None);
    await hold.RollbackAsync();
    var after = await _step().ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(heldResult.Status).StartsWith("held").Because("a held row ends the cycle; the next batch would read the same rows");
    await Assert.That(heldResult.Batches).IsEqualTo(1);
    await Assert.That(after.EventsStamped).IsEqualTo(1L).Because("the cursor stopped before the held row, so it is read again");
    await Assert.That(await _flagsAsync(conn, a2)).IsEqualTo((long)SUPERSEDING);
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([a1]);
  }

  [Test]
  public async Task Reconcile_ANewerDefinitionOfTheType_WalksItAgainFromTheStartAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _registerDefinitionAsync(conn, "superseding");
    var a1 = await _historicAsync(conn, stream, _embeddedJson(stream, "k"));
    await _step().ReconcileAsync(_services(), CancellationToken.None);
    // Stored while the type was briefly something else, by an instance that did not stamp it.
    var a2 = await _historicAsync(conn, stream, _embeddedJson(stream, "k"));

    var unchanged = await _step().ReconcileAsync(_services(), CancellationToken.None);
    await _registerDefinitionAsync(conn, "sourced");
    var again = await _step().ReconcileAsync(_services(), CancellationToken.None);

    await Assert.That(unchanged.Status).IsEqualTo("complete").Because("the control: a finished walk reads nothing more");
    await Assert.That(again.EventsStamped).IsEqualTo(1L)
      .Because("the registry holds a definition newer than the walk started under, so the type is walked again from the first event");
    await Assert.That(await _queuedAsync(conn)).IsEquivalentTo([a1]);
    await Assert.That(await _flagsAsync(conn, a2)).IsEqualTo((long)SUPERSEDING);
  }

  // ===== The step =====

  [Test]
  public async Task Reconcile_AnUnreadablePayload_IsStampedUnkeyed_AndLoggedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var wrongShape = await _historicAsync(conn, stream, $$"""{"documentId":"{{stream}}","model":123}""");
    var other = (Guid)TrackedGuid.New();
    var unreadable = await _historicAsync(conn, other, _embeddedJson(other, "k"));
    var logger = new ListLogger();

    await new SupersedingHistoryReconcileStep(logger, 1, 10, () => [typeof(SupersedingProbeEmbedded)])
      .ReconcileAsync(_services(), CancellationToken.None);
    await _executeAsync(conn, "DELETE FROM wh_superseding_history");
    await _executeAsync(conn, "UPDATE wh_event_store SET flags = 0, supersede_key = NULL WHERE event_id = @e", ("e", unreadable));
    // Options that cannot read the type at all.
    await new SupersedingHistoryReconcileStep(logger, 100, 10, () => [typeof(SupersedingProbeEmbedded)])
      .ReconcileAsync(_services(new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine() }), CancellationToken.None);

    await Assert.That(await _flagsAsync(conn, wrongShape)).IsEqualTo((long)SUPERSEDING);
    await Assert.That(await _keyAsync(conn, wrongShape)).IsNull().Because("a key that cannot be read keeps the event, the recoverable direction");
    await Assert.That(await _keyAsync(conn, unreadable)).IsNull();
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("could not be read", StringComparison.Ordinal)))
      .IsEqualTo(2).Because("a payload whose key cannot be read is named in the log, never skipped silently");
  }

  [Test]
  public async Task Step_ReportsProgress_AndOneLineWhenATypeFinishesAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _historicAsync(conn, stream, _embeddedJson(stream, "k"));
    await _historicAsync(conn, stream, _embeddedJson(stream, "k"));
    var logger = new ListLogger();
    var step = new SupersedingHistoryReconcileStep(logger, 100, 10, () => [typeof(SupersedingProbeEmbedded)]);

    await step.ReconcileAsync(_services(), CancellationToken.None);
    await step.RunAsync(_services(), CancellationToken.None);

    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("2 stamped, 1 queued", StringComparison.Ordinal)
      && e.Message.Contains("0 of 2 left", StringComparison.Ordinal))).IsEqualTo(1);
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("reconciled", StringComparison.Ordinal)))
      .IsEqualTo(1).Because("one line when a type finishes, not one per cycle after");
    await Assert.That(step.Name).IsEqualTo("superseding-history-reconcile");
  }

  [Test]
  public async Task Step_WithTheSettingOff_TouchesNothingAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var a1 = await _historicAsync(conn, stream, _embeddedJson(stream, "k"));
    await _historicAsync(conn, stream, _embeddedJson(stream, "k"));
    var services = new ServiceCollection()
      .AddSingleton(_connection())
      .AddSingleton(Options.Create(new SupersedingOptions { ReconcileHistory = false }))
      .BuildServiceProvider();

    var result = await _step().ReconcileAsync(services, CancellationToken.None);

    await Assert.That(result.Status).IsEqualTo("skipped (Whizbang:Superseding:ReconcileHistory=false)");
    await Assert.That(await _flagsAsync(conn, a1)).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_superseding_history")).IsEqualTo(0L);
  }

  [Test]
  public async Task Apply_MismatchedArrays_OrAWindowThatIsNotItsOwn_ChangeNothingAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var a1 = await _historicAsync(conn, stream, _embeddedJson(stream, "k"));

    await Assert.That(async () => await _executeAsync(conn,
      "SELECT * FROM wh_superseding_history_apply(@t, NULL, @e, 10, 1, @ids, @keys)",
      ("t", _embedded), ("e", a1), ("ids", new[] { a1 }), ("keys", Array.Empty<string>()))).Throws<PostgresException>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT status FROM wh_superseding_history_apply(@t, NULL, @e, 10, 1, @ids, @keys)";
    cmd.Parameters.AddWithValue("t", _embedded);
    cmd.Parameters.AddWithValue("e", a1);
    cmd.Parameters.AddWithValue("ids", new[] { a1 });
    cmd.Parameters.AddWithValue("keys", new[] { "k" });
    var status = (string)(await cmd.ExecuteScalarAsync())!;

    await Assert.That(status).StartsWith("skipped").Because("an apply with no walk begun in its transaction applies nothing");
    await Assert.That(await _flagsAsync(conn, a1)).IsEqualTo(0L);
  }

  [Test]
  public async Task FromServices_AndItsArguments_AreCheckedAsync() {
    await Assert.That(SupersedingHistoryReconcileStep.FromServices(new ServiceCollection().AddLogging().BuildServiceProvider())).IsNotNull();
    await Assert.That(SupersedingHistoryReconcileStep.FromServices(new ServiceCollection().BuildServiceProvider())).IsNotNull();
    await Assert.That(() => SupersedingHistoryReconcileStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new SupersedingHistoryReconcileStep(null!, 1, 1, () => [])).Throws<ArgumentNullException>();
    await Assert.That(() => new SupersedingHistoryReconcileStep(NullLogger.Instance, 1, 1, null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await _step().ReconcileAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheStepAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<SupersedingHistoryReconcileStep>().ToList();

    await Assert.That(steps.Count).IsEqualTo(1).Because("the history is reconciled by the maintenance cycle, idle-gated, under the maintainer turn");
  }

  // ===== Helpers =====

  private static readonly string _definitionName = TypeNameFormatter.FormatClrTypeName(typeof(SupersedingProbeEmbedded));

  private sealed class ListLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private static SupersedingHistoryReconcileStep _step(params Type[] types) =>
    new(NullLogger.Instance, 100, 10, () => types.Length == 0 ? [typeof(SupersedingProbeEmbedded)] : types);

  private static SupersedingHistoryReconcileStep _step(int scanSize, int maxBatches) =>
    new(NullLogger.Instance, scanSize, maxBatches, () => [typeof(SupersedingProbeEmbedded)]);

  private PostgresArchiveConnection _connection() =>
    new(async ct => {
      var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, "public");

  private ServiceProvider _services(JsonSerializerOptions? json = null) {
    var services = new ServiceCollection().AddSingleton(_connection());
    if (json is not null) {
      services.AddSingleton(json);
    }
    return services.BuildServiceProvider();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static Guid[] _orderedIds(int count) =>
    [.. Enumerable.Range(0, count).Select(_ => (Guid)TrackedGuid.New()).Order()];

  private static string _embeddedJson(Guid stream, string? model) =>
    JsonSerializer.Serialize(new SupersedingProbeEmbedded { DocumentId = stream, Model = model! }, _json.GetTypeInfo(typeof(SupersedingProbeEmbedded)));

  private static string _profileJson(Guid stream, int revision) =>
    JsonSerializer.Serialize(new SupersedingHistoryProbeProfile { UserId = stream, Revision = revision }, _json.GetTypeInfo(typeof(SupersedingHistoryProbeProfile)));

  private static string _plainJson(Guid stream, string model) =>
    JsonSerializer.Serialize(new SupersedingHistoryProbePlain { DocumentId = stream, Model = model }, _json.GetTypeInfo(typeof(SupersedingHistoryProbePlain)));

  /// <summary>An event as a write path stored it before its type was declared superseding: no flag, no key.</summary>
  private static Task<Guid> _historicAsync(NpgsqlConnection conn, Guid stream, string payload, string? type = null) =>
    _appendAsync(conn, stream, payload, type ?? _embedded, 0, null);

  private static async Task<Guid> _appendAsync(NpgsqlConnection conn, Guid stream, string payload, string type, int flags, string? key) {
    var id = (Guid)TrackedGuid.New();
    var metadata = key is null
      ? $$"""{"MessageId":"{{id}}","Hops":[]}"""
      : $$"""{"MessageId":"{{id}}","Hops":[],"sk":"{{key}}"}""";
    await _executeAsync(conn,
      "SELECT wh_append_event(@id, @s, @s, split_part(@t, ',', 1), @t, @p::jsonb, @m::jsonb, NULL, NOW(), @f)",
      ("id", id), ("s", stream), ("t", type), ("p", payload), ("m", metadata), ("f", flags));
    return id;
  }

  private static async Task<(Guid Stream, Guid Old, Guid New)> _pairAsync(NpgsqlConnection conn) {
    var stream = (Guid)TrackedGuid.New();
    var old = await _historicAsync(conn, stream, _embeddedJson(stream, "k"));
    var newer = await _historicAsync(conn, stream, _embeddedJson(stream, "k"));
    return (stream, old, newer);
  }

  private static InboxMessage _received(Guid streamId, Guid messageId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.SerializeToElement(new SupersedingProbeEmbedded { DocumentId = streamId, Model = "model-a" },
        _json.GetTypeInfo(typeof(SupersedingProbeEmbedded))),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "ProbeHandler",
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = _embedded,
      StreamId = streamId,
      IsEvent = true,
      Flags = EventFlags.None,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
    };
  }

  private static Task _ensureAsync(NpgsqlConnection conn) =>
    _executeAsync(conn, "SELECT wh_superseding_history_ensure(@t, @d)", ("t", _embedded), ("d", _definitionName));

  private static Task _registerDefinitionAsync(NpgsqlConnection conn, string settings) =>
    _executeAsync(conn,
      "SELECT * FROM register_type_definition(@n, sha256(convert_to(@s, 'UTF8')), sha256(convert_to('{}', 'UTF8')), 0)",
      ("n", _definitionName), ("s", settings));

  private static Task _stampAsync(NpgsqlConnection conn) => _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");

  private static Task<long> _reapAsync(NpgsqlConnection conn) =>
    _scalarAsync(conn, "SELECT rows_affected FROM wh_reap_superseded_events(5000)");

  private static Task<long> _flagsAsync(NpgsqlConnection conn, Guid eventId) =>
    _scalarAsync(conn, "SELECT COALESCE(flags, 0) FROM wh_event_store WHERE event_id = @id", ("id", eventId));

  private static async Task<string> _rowVersionAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ctid::text || '|' || xmin::text FROM wh_event_store WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", eventId);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<string?> _keyAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT supersede_key FROM wh_event_store WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", eventId);
    var value = await cmd.ExecuteScalarAsync();
    return value is DBNull ? null : (string?)value;
  }

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

  private static async Task<List<Guid>> _storedAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id FROM wh_event_store WHERE stream_id = @s ORDER BY version";
    cmd.Parameters.AddWithValue("s", stream);
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
