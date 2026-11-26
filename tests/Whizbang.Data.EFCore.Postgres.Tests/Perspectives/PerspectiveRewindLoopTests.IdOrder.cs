// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A stream whose first event has the highest id (#2379), in the exact shape a deployed environment held after #2366's
/// upgrade: version 1 is a Completed event carrying the stream's highest id, versions 2 to 4 are Started events with
/// lower ids (version 2 the lowest), one snapshot at version 1, the cursor on version 2 flagged for a rewind with version 2
/// as its trigger, versions 3 and 4 pending, and the row stamped at version 1's id with version 2's commit sequence. A
/// rewind restored the snapshot and read "the events after it" by id, which selects nothing when the snapshot's event has
/// the highest id, so nothing was applied, the cursor never moved and the stream rewound about 900 times a minute.
/// </summary>
/// <docs>fundamentals/perspectives/rewind#one-order-for-a-stream</docs>
public partial class PerspectiveRewindLoopTests {
  /// <summary>Runner rewinds tolerated before the RED run is called a loop: a converging stream needs one.</summary>
  private const int LOOP_REWINDS = 5;

  private static readonly Guid _specimenOrigin = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-000000002379");

  /// <summary>
  /// The live shape exactly (#2379). One rewind settles it: the replay reads versions 2 to 4 after the snapshot at
  /// version 1, the cursor ends on version 4 with its flag clear, the pending work is completed, no later poll claims the
  /// stream again, and the row keeps its Completed state.
  /// </summary>
  [Test]
  public async Task AStreamWhoseFirstEventHasTheHighestId_WithASnapshotThere_ConvergesAfterOneRewindAsync() {
    var ct = CancellationToken.None;
    var streamId = (Guid)TrackedGuid.New();
    var workerLog = new RewindCountingLogger();
    await using var pipeline = _composePipeline(workerLog, snapshots: true);
    await using var conn = await _openAsync(ct);
    await pipeline.RegisterAsync(conn, ct);
    await _registerItemTrailAsync(conn, ct);

    var specimen = await _seedFirstEventHasTheHighestIdAsync(pipeline, conn, streamId, ct);
    var workIds = await _workIdsAsync(conn, streamId, ct);
    await Assert.That(workIds.Count).IsEqualTo(2).Because("versions 3 and 4 are pending, as on the deployed environment");
    await Assert.That(await _scalarAsync<int>(conn, "SELECT wh_queue_unreachable_rewinds()", streamId, ct)).IsEqualTo(0)
      .Because("a looping stream with pending work needs no repair: its next claim settles it");

    await _convergesAfterOneRewindAsync(pipeline, workerLog, conn, streamId, specimen, workIds, ct);
  }

  /// <summary>
  /// The live shape with its pending work retired (#2379): nothing is left to claim, so the flag would wait for the
  /// stream's next event. The upgrade repair queues the trigger, and one rewind settles the stream.
  /// </summary>
  [Test]
  public async Task AStreamWhoseFirstEventHasTheHighestId_WithItsWorkRetired_IsQueuedOnUpgrade_AndConvergesAfterOneRewindAsync() {
    var ct = CancellationToken.None;
    var streamId = (Guid)TrackedGuid.New();
    var workerLog = new RewindCountingLogger();
    await using var pipeline = _composePipeline(workerLog, snapshots: true);
    await using var conn = await _openAsync(ct);
    await pipeline.RegisterAsync(conn, ct);
    await _registerItemTrailAsync(conn, ct);

    var specimen = await _seedFirstEventHasTheHighestIdAsync(pipeline, conn, streamId, ct);
    await _executeAsync(conn, "DELETE FROM wh_perspective_events WHERE stream_id = @id", streamId, ct);

    // The deploy: migration 466's repair, as it runs once on upgrade.
    await Assert.That(await _scalarAsync<int>(conn, "SELECT wh_queue_unreachable_rewinds()", streamId, ct)).IsEqualTo(1);
    var workIds = await _workIdsAsync(conn, streamId, ct);
    await Assert.That(workIds.Count).IsEqualTo(1).Because("the flag's trigger is queued, unleased");

    await _convergesAfterOneRewindAsync(pipeline, workerLog, conn, streamId, specimen, workIds, ct);
  }

  /// <summary>
  /// Two services with real stores (#2379): the origin emits three Started events and then the Completed one; the
  /// consumer receives the Completed event first, so it holds it at version 1 with the stream's highest id and the
  /// Started events above it with lower ids. Left as the loop left it (the cursor on version 2 flagged for it, a snapshot
  /// at version 1, versions 3 and 4 pending), the consumer settles with one rewind, and audit cycles against the origin
  /// find nothing to repair and never rewind it again.
  /// </summary>
  [Test]
  [NotInParallel("IntegrityManifestGates")]   // the manifest receptors' per-process gates are shared state
  public async Task TwoServices_AConsumerStreamWhoseFirstEventHasTheHighestId_ConvergesAfterOneRewind_AndAuditCyclesNeverRewindItAgainAsync() {
    var ct = CancellationToken.None;
    var streamId = (Guid)TrackedGuid.New();
    var workerLog = new RewindCountingLogger();
    await using var pipeline = _composePipeline(workerLog, snapshots: true);
    await using var conn = await _openAsync(ct);
    await pipeline.RegisterAsync(conn, ct);
    await _registerItemTrailAsync(conn, ct);
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, []);
    var started = TypeNameFormatter.Format(typeof(ItemProgressStartedEvent));
    var completed = TypeNameFormatter.Format(typeof(ItemProgressCompletedEvent));
    var options = JsonContextRegistry.CreateCombinedOptions();
    var startedPayload = JsonSerializer.Serialize(new ItemProgressStartedEvent { StreamId = streamId },
      options.GetTypeInfo(typeof(ItemProgressStartedEvent)));
    var completedPayload = JsonSerializer.Serialize(new ItemProgressCompletedEvent { StreamId = streamId },
      options.GetTypeInfo(typeof(ItemProgressCompletedEvent)));

    var ids = await h.EmitAsync(
      new TwoServiceEmit(streamId, started, 0, null, startedPayload),
      new TwoServiceEmit(streamId, started, 0, null, startedPayload),
      new TwoServiceEmit(streamId, started, 0, null, startedPayload),
      new TwoServiceEmit(streamId, completed, 0, null, completedPayload));
    await Assert.That((await h.DeliverAsync([ids[3]])).Count).IsEqualTo(1);
    await Assert.That((await h.DeliverAsync(ids[..3])).Count).IsEqualTo(3);
    var specimen = new FirstEventHasTheHighestId(Version1: ids[3], Version2: ids[0], Version3: ids[1], Version4: ids[2]);
    await _leftLoopingAsync(pipeline, conn, streamId, specimen, ct);
    var workIds = await _releasedWorkIdsAsync(conn, streamId, pipeline.InstanceProvider.InstanceId, ct);
    await Assert.That(workIds.Count).IsEqualTo(2).Because("versions 3 and 4 are pending on the consumer");

    await _convergesAfterOneRewindAsync(pipeline, workerLog, conn, streamId, specimen, workIds, ct);
    var held = await TwoServiceHarness.FingerprintAsync(h.Consumer, [streamId]);

    for (var cycle = 1; cycle <= 3; cycle++) {
      await h.AuditAsync(TwoServiceAudit.TypesTable, started, completed);
      await h.AuditAsync(TwoServiceAudit.Streams, started, completed);
      var reclaims = await _reclaimsOverQuietPollsAsync(pipeline, conn, streamId, ct);
      await Assert.That(h.Tally()).IsEqualTo(new TwoServiceTally(0, 0, 0, 0))
        .Because($"cycle {cycle}: the consumer holds what the origin holds, so the audit asks for nothing");
      await Assert.That(reclaims).IsEqualTo(0).Because($"cycle {cycle}: the settled stream is not claimed again");
      await Assert.That(pipeline.RunnerRewinds.Count).IsEqualTo(1).Because($"cycle {cycle}: no second rewind");
      await Assert.That(await TwoServiceHarness.FingerprintAsync(h.Consumer, [streamId])).IsEqualTo(held);
    }
  }

  /// <summary>
  /// A derived event behind a newer one (#2519, found by #2274's test with #2379): the origin stores Completed, then a
  /// retry of an older handling returns three Started events whose ids derive from that older cause (EmissionIdentity, the
  /// derivation returned events have long used), so they sort before Completed although the origin stored them after it. The consumer receives them in the origin's order and holds Completed at version 1
  /// with the stream's highest id and a snapshot there; left as a rewind for the lower-id events leaves it, one rewind
  /// settles the stream, nothing loops, and the row is the fold in the producer's order (Completed, then the three
  /// Started), not the fold in id order.
  /// </summary>
  [Test]
  [NotInParallel("IntegrityManifestGates")]   // the manifest receptors' per-process gates are shared state
  public async Task TwoServices_DerivedEventsBehindANewerEvent_WithASnapshotOnIt_ConvergeAfterOneRewindInProducerOrderAsync() {
    var ct = CancellationToken.None;
    var streamId = (Guid)TrackedGuid.New();
    var workerLog = new RewindCountingLogger();
    await using var pipeline = _composePipeline(workerLog, snapshots: true);
    await using var conn = await _openAsync(ct);
    await pipeline.RegisterAsync(conn, ct);
    await _registerItemTrailAsync(conn, ct);
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, []);
    var started = TypeNameFormatter.Format(typeof(ItemProgressStartedEvent));
    var completed = TypeNameFormatter.Format(typeof(ItemProgressCompletedEvent));
    var options = JsonContextRegistry.CreateCombinedOptions();
    var startedPayload = JsonSerializer.Serialize(new ItemProgressStartedEvent { StreamId = streamId },
      options.GetTypeInfo(typeof(ItemProgressStartedEvent)));
    var completedPayload = JsonSerializer.Serialize(new ItemProgressCompletedEvent { StreamId = streamId },
      options.GetTypeInfo(typeof(ItemProgressCompletedEvent)));

    // The older handling's message, then the newer Completed; the retry of the older handling derives its emissions.
    var olderCause = (Guid)TrackedGuid.New();
    var completedIds = await h.EmitAsync(new TwoServiceEmit(streamId, completed, 0, null, completedPayload));
    Guid[] derived = [.. Enumerable.Range(0, 3).Select(i => EmissionIdentity.Derive(olderCause, "origin", "ItemHandler", started, i))];
    var startedIds = await h.EmitAsync([.. derived.Select(id => new TwoServiceEmit(streamId, started, 0, null, startedPayload, id))]);
    await Assert.That(startedIds).IsEquivalentTo(derived, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(derived.All(id => string.CompareOrdinal(id.ToString(), completedIds[0].ToString()) < 0)).IsTrue()
      .Because("positive control: the derived events sort before the newer event the origin stored ahead of them");

    await Assert.That((await h.DeliverAsync(completedIds)).Count).IsEqualTo(1);
    await Assert.That((await h.DeliverAsync(startedIds)).Count).IsEqualTo(3);
    var specimen = new FirstEventHasTheHighestId(Version1: completedIds[0], Version2: derived[0], Version3: derived[1], Version4: derived[2]);
    await _leftLoopingAsync(pipeline, conn, streamId, specimen, ct, snapshotTrail: "C");
    var workIds = await _releasedWorkIdsAsync(conn, streamId, pipeline.InstanceProvider.InstanceId, ct);
    await Assert.That(workIds.Count).IsEqualTo(2).Because("versions 3 and 4 are pending on the consumer");

    await _convergesAfterOneRewindAsync(pipeline, workerLog, conn, streamId, specimen, workIds, ct,
      producerOrderRow: _fold(started: 3, startedFirst: false),
      otherOrderRow: _fold(started: 3, startedFirst: true));
  }

  /// <summary>
  /// The same shape in one service (#2519), with nothing but the ids returned events already derived before #2274: the
  /// stream commits Completed, then a retry of an older handling commits the three Started events it returns, whose
  /// derived ids sort before Completed. With a snapshot at Completed and a rewind flagged for the lower-id events, one
  /// rewind settles the stream and the row is the fold in the order the stream was written (Completed, then the three
  /// Started). The rewind folded them, but the row's write was refused: its frontier named the highest id it replayed,
  /// below the snapshot's event the row is stamped at, and a versioned-apply row takes only a write at or after that id.
  /// </summary>
  [Test]
  public async Task OneService_ReturnedEventsDerivedBehindANewerEvent_WithASnapshotOnIt_ConvergeAfterOneRewindInStreamOrderAsync() {
    var ct = CancellationToken.None;
    var streamId = (Guid)TrackedGuid.New();
    var workerLog = new RewindCountingLogger();
    await using var pipeline = _composePipeline(workerLog, snapshots: true);
    await using var conn = await _openAsync(ct);
    await pipeline.RegisterAsync(conn, ct);
    await _registerItemTrailAsync(conn, ct);

    var olderCause = (Guid)TrackedGuid.New();
    var completedId = (Guid)TrackedGuid.New();
    var started = TypeNameFormatter.Format(typeof(ItemProgressStartedEvent));
    Guid[] derived = [.. Enumerable.Range(0, 3).Select(i => EmissionIdentity.Derive(olderCause, "rewind-loop-svc", "ItemHandler", started, i))];
    await Assert.That(derived.All(id => string.CompareOrdinal(id.ToString(), completedId.ToString()) < 0)).IsTrue()
      .Because("positive control: the derived events sort before the newer event the stream holds ahead of them");
    await pipeline.CommitEventsAsync(conn, streamId, [(completedId, new ItemProgressCompletedEvent { StreamId = streamId })], ct);
    await pipeline.CommitEventsAsync(conn, streamId, [.. derived.Select(id => (id, (IEvent)new ItemProgressStartedEvent { StreamId = streamId }))], ct);
    var specimen = new FirstEventHasTheHighestId(Version1: completedId, Version2: derived[0], Version3: derived[1], Version4: derived[2]);
    await _leftLoopingAsync(pipeline, conn, streamId, specimen, ct, snapshotTrail: "C");
    var workIds = await _workIdsAsync(conn, streamId, ct);
    await Assert.That(workIds.Count).IsEqualTo(2).Because("versions 3 and 4 are pending");

    await _convergesAfterOneRewindAsync(pipeline, workerLog, conn, streamId, specimen, workIds, ct,
      producerOrderRow: _fold(started: 3, startedFirst: false),
      otherOrderRow: _fold(started: 3, startedFirst: true));
  }

  /// <summary>
  /// Starts the pipeline and asserts the stream settles with one rewind: its work completes before the runners rewind it
  /// <see cref="LOOP_REWINDS"/> times, the cursor ends on version 4 with its flag clear, nothing is pending, no later poll
  /// claims it again, and the row keeps its Completed state.
  /// </summary>
  private static async Task _convergesAfterOneRewindAsync(
      Pipeline pipeline, RewindCountingLogger workerLog, NpgsqlConnection conn, Guid streamId,
      FirstEventHasTheHighestId specimen, List<Guid> workIds,
      CancellationToken ct, string? producerOrderRow = null, string? otherOrderRow = null) {
    var settled = Task.WhenAll(workIds.Select(pipeline.CompletionOf));
    try {
      await pipeline.StartAsync(ct);
    } catch (TimeoutException) {
      Console.WriteLine($"DIAG start: runner rewinds={pipeline.RunnerRewinds.Count} {await _cursorAsync(conn, streamId, ct)}\nDIAG rows: {await _scalarAsync<string>(conn, "SELECT string_agg(format('%s %s st%s inst %s lease %s', perspective_name, event_id, status, instance_id, lease_expiry), ' | ') FROM wh_perspective_events WHERE stream_id = @id", streamId, ct)}\nDIAG active: {await _scalarAsync<object>(conn, "SELECT string_agg(format('%s %s', assigned_instance_id, lease_expiry), ' | ') FROM wh_active_streams WHERE stream_id = @id", streamId, ct)} me {pipeline.InstanceProvider.InstanceId}\nDIAG logs:\n{pipeline.RunnerRewinds.Warnings}\nDIAG worker:\n{string.Join("\n", workerLog.All.TakeLast(40))}");
      throw;
    }
    var looping = pipeline.RunnerRewinds.Reached(LOOP_REWINDS);
    Task first;
    try {
      first = await Task.WhenAny(settled, looping).WaitAsync(_signalDeadline, ct);
    } catch (TimeoutException) {
      Console.WriteLine($"DIAG runner rewinds={pipeline.RunnerRewinds.Count} {await _cursorAsync(conn, streamId, ct)}\nDIAG warnings:\n{pipeline.RunnerRewinds.Warnings}");
      throw;
    }
    Console.WriteLine($"settled={first == settled} runner rewinds={pipeline.RunnerRewinds.Count} {await _cursorAsync(conn, streamId, ct)}");
    await Assert.That(first == looping).IsFalse()
      .Because($"the stream rewound {pipeline.RunnerRewinds.Count} times without completing its work: {pipeline.RunnerRewinds.Last}");
    await settled;
    var rewindsWhenSettled = pipeline.RunnerRewinds.Count;

    var reclaims = await _reclaimsOverQuietPollsAsync(pipeline, conn, streamId, ct);
    var cursorEvent = await _trailScalarAsync<Guid>(conn,
      "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @id AND perspective_name = @p", streamId, ct);
    var flagged = await _trailScalarAsync<bool>(conn,
      "SELECT COALESCE(bool_or((status & 32) = 32), false) FROM wh_perspective_cursors WHERE stream_id = @id AND perspective_name = @p",
      streamId, ct);
    var pending = await _trailScalarAsync<long>(conn,
      "SELECT count(*) FROM wh_perspective_events WHERE stream_id = @id AND perspective_name = @p AND processed_at IS NULL", streamId, ct);
    var row = await _scalarAsync<string>(conn,
      "SELECT format('%s %s', data->>'State', data->>'Trail') FROM wh_per_item_trail WHERE id = @id", streamId, ct);
    // The row the producer's order gives (three Started, then Completed), and the one the stored order gives: they
    // differ, so the row says which order was folded.
    var inProducerOrder = producerOrderRow ?? _fold(started: 3, startedFirst: true);
    var inVersionOrder = otherOrderRow ?? _fold(started: 3, startedFirst: false);

    await Assert.That(rewindsWhenSettled).IsEqualTo(1).Because("one rewind settles the stream");
    await Assert.That(pipeline.RunnerRewinds.Count).IsEqualTo(rewindsWhenSettled).Because("nothing rewinds it again");
    await Assert.That(reclaims).IsEqualTo(0).Because("a settled stream is not claimed again");
    await Assert.That(cursorEvent).IsEqualTo(specimen.Version4).Because("the cursor ends on the stream's last version");
    await Assert.That(flagged).IsFalse().Because("the completed rewind clears its flag");
    await Assert.That(pending).IsEqualTo(0L).Because("the pending work is completed");
    await Assert.That(inVersionOrder).IsNotEqualTo(inProducerOrder).Because("positive control: the two orders fold differently");
    await Assert.That(row).IsEqualTo(inProducerOrder)
      .Because("the row is the fold in the producer's order, whatever order the events arrived or were stored in");
  }

  /// <summary>The row <see cref="ItemTrailPerspective"/> folds from Started events and one Completed, in either order.</summary>
  private static string _fold(int started, bool startedFirst) {
    var perspective = new ItemTrailPerspective();
    var id = Guid.Empty;
    var row = new ItemTrail();
    var completedEvent = new ItemProgressCompletedEvent { StreamId = id };
    if (!startedFirst) {
      row = perspective.Apply(row, completedEvent);
    }
    for (var i = 0; i < started; i++) {
      row = perspective.Apply(row, new ItemProgressStartedEvent { StreamId = id });
    }
    if (startedFirst) {
      row = perspective.Apply(row, completedEvent);
    }
    return $"{row.State} {row.Trail}";
  }

  /// <summary>The ids of the four events of the specimen, by version.</summary>
  private sealed record FirstEventHasTheHighestId(Guid Version1, Guid Version2, Guid Version3, Guid Version4);

  /// <summary>
  /// Seeds the deployed environment's stream (#2379) with neutral types: the events committed in version order with the
  /// ids, origin positions and commit-sequence order the live rows had; the snapshot, the row, the cursor and the pending
  /// work as they stood. Every other table the capture held (dead letters already recovered, digests, the lifecycle row,
  /// an expired stream lease) is either written by the commit itself or not read by the perspective path.
  /// </summary>
  private static async Task<FirstEventHasTheHighestId> _seedFirstEventHasTheHighestIdAsync(
      Pipeline pipeline, NpgsqlConnection conn, Guid streamId, CancellationToken ct) {
    // The ids' order as the live rows had it: version 2 lowest, then 3 and 4, then version 1 highest.
    var minted = _minted;
    var specimen = new FirstEventHasTheHighestId(
      Version1: Guid.CreateVersion7(minted.AddMilliseconds(2266)),
      Version2: Guid.CreateVersion7(minted),
      Version3: Guid.CreateVersion7(minted.AddMilliseconds(2102)),
      Version4: Guid.CreateVersion7(minted.AddMilliseconds(2103)));

    // Committed one by one in version order: version 1 first, at the lowest commit sequence.
    await pipeline.CommitEventsAsync(conn, streamId, [(specimen.Version1, new ItemProgressCompletedEvent { StreamId = streamId })], ct);
    await pipeline.CommitEventsAsync(conn, streamId, [(specimen.Version2, new ItemProgressStartedEvent { StreamId = streamId })], ct);
    await pipeline.CommitEventsAsync(conn, streamId, [(specimen.Version3, new ItemProgressStartedEvent { StreamId = streamId })], ct);
    await pipeline.CommitEventsAsync(conn, streamId, [(specimen.Version4, new ItemProgressStartedEvent { StreamId = streamId })], ct);

    await using (var origin = conn.CreateCommand()) {
      // Received from one origin: version 2 sits before version 1 there; versions 3 and 4 carry no origin position.
      origin.CommandText = """
        UPDATE wh_event_store
        SET origin_service_id = @origin,
            origin_commit_sequence = CASE event_id WHEN @v1 THEN 4790121 WHEN @v2 THEN 4790063 END
        WHERE stream_id = @stream
        """;
      origin.Parameters.AddWithValue("origin", _specimenOrigin);
      origin.Parameters.AddWithValue("v1", specimen.Version1);
      origin.Parameters.AddWithValue("v2", specimen.Version2);
      origin.Parameters.AddWithValue("stream", streamId);
      await origin.ExecuteNonQueryAsync(ct);
    }
    await _leftLoopingAsync(pipeline, conn, streamId, specimen, ct);
    return specimen;
  }

  /// <summary>
  /// Leaves a stream holding the specimen's four events in the state the loop left it in (#2379): versions 1 and 2
  /// applied (their work gone), 3 and 4 pending and unleased, the cursor on version 2 flagged with version 2 as trigger,
  /// the row Completed and stamped at version 1's id with version 2's commit sequence, and one snapshot at version 1.
  /// </summary>
  private static async Task _leftLoopingAsync(
      Pipeline pipeline, NpgsqlConnection conn, Guid streamId, FirstEventHasTheHighestId specimen, CancellationToken ct,
      string snapshotTrail = "SC") {
    await using (var shape = conn.CreateCommand()) {
      shape.CommandText = """
        -- Only this perspective's work: another perspective the shared database associates with these types is not under test.
        DELETE FROM wh_perspective_events
        WHERE stream_id = @stream AND (event_id IN (@v1, @v2) OR perspective_name <> @perspective);
        UPDATE wh_perspective_events SET instance_id = NULL, lease_expiry = NULL WHERE stream_id = @stream;
        INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at,
                                            rewind_trigger_event_id, rewind_flagged_at, rewind_first_flagged_at)
        VALUES (@stream, @perspective, @v2, 2 | 32, NOW(), @v2, NOW(), NOW())
        ON CONFLICT (stream_id, perspective_name) DO UPDATE
          SET last_event_id = EXCLUDED.last_event_id, status = EXCLUDED.status,
              rewind_trigger_event_id = EXCLUDED.rewind_trigger_event_id;
        INSERT INTO wh_per_item_trail (id, data, metadata, scope, created_at, updated_at, version)
        VALUES (@stream,
          jsonb_build_object('Id', @stream, 'State', 'Completed', 'Trail', @trail::text),
          jsonb_build_object('EventId', @v1::text, 'EventType', @completedType, 'Timestamp', NOW(),
            'CommitSequence', (SELECT commit_sequence FROM wh_event_store WHERE event_id = @v2)),
          '{}'::jsonb, NOW(), NOW(), 2)
        ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data, metadata = EXCLUDED.metadata;
        """;
      shape.Parameters.AddWithValue("v1", specimen.Version1);
      shape.Parameters.AddWithValue("v2", specimen.Version2);
      shape.Parameters.AddWithValue("stream", streamId);
      shape.Parameters.AddWithValue("perspective", _itemTrailPerspective);
      shape.Parameters.AddWithValue("completedType", typeof(ItemProgressCompletedEvent).FullName!);
      shape.Parameters.AddWithValue("trail", snapshotTrail);
      await shape.ExecuteNonQueryAsync(ct);
    }

    // The one snapshot, at version 1, in the format the runner writes.
    // As the deployed snapshot held it: taken at version 1 after a replay that folded version 2 before it (origin order).
    var model = new ItemTrail { Id = streamId, State = "Completed", Trail = snapshotTrail };
    using (var modelDoc = JsonSerializer.SerializeToDocument(model,
             JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(ItemTrail)))) {
      using var snapshot = SnapshotEnvelope.Wrap(modelDoc.RootElement, SerializationVersion.CURRENT);
      var version1Sequence = await _scalarAsync<long>(conn,
        "SELECT commit_sequence FROM wh_event_store WHERE event_id = @id", specimen.Version1, ct);
      await pipeline.Services.GetRequiredService<IPerspectiveSnapshotStore>().CreateSnapshotAsync(
        streamId, _itemTrailPerspective, specimen.Version1, version1Sequence, snapshot, ct);
    }

    var snapshotAt = await _scalarAsync<string>(conn, """
      SELECT format('v%s seq %s', snapshot_version, sequence_number) FROM wh_perspective_snapshots WHERE stream_id = @id
      """, streamId, ct);
    await Assert.That(snapshotAt).IsEqualTo("v1 seq 1").Because("one snapshot, at version 1");
    var versionsByIdOrder = await _scalarAsync<string>(conn,
      "SELECT string_agg(version::text, ',' ORDER BY event_id) FROM wh_event_store WHERE stream_id = @id", streamId, ct);
    await Assert.That(versionsByIdOrder).IsEqualTo("2,3,4,1").Because("version 1 has the highest id, version 2 the lowest");
  }

  private static readonly string _itemTrailPerspective = TypeNameFormatter.GetPerspectiveName(typeof(ItemTrailPerspective));

  /// <summary>A scalar read of the stream (<c>@id</c>) for the perspective under test (<c>@p</c>).</summary>
  private static async Task<T> _trailScalarAsync<T>(NpgsqlConnection conn, string sql, Guid streamId, CancellationToken ct) {
    await using var command = conn.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue("id", streamId);
    command.Parameters.AddWithValue("p", _itemTrailPerspective);
    return (T)(await command.ExecuteScalarAsync(ct))!;
  }

  /// <summary>Associates the per-item events with their perspective, so a commit queues their work.</summary>
  private static async Task _registerItemTrailAsync(NpgsqlConnection conn, CancellationToken ct) {
    foreach (var eventType in new[] { typeof(ItemProgressStartedEvent), typeof(ItemProgressCompletedEvent) }) {
      await using var assoc = conn.CreateCommand();
      assoc.CommandText = """
        INSERT INTO wh_message_associations
          (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
        VALUES (gen_random_uuid(), @eventType, 'perspective', @target, 'rewind-loop-svc', @eventType, NOW(), NOW())
        ON CONFLICT DO NOTHING
        """;
      assoc.Parameters.AddWithValue("eventType", TypeNameFormatter.Format(eventType));
      assoc.Parameters.AddWithValue("target", _itemTrailPerspective);
      await assoc.ExecuteNonQueryAsync(ct);
    }
  }

  /// <summary>
  /// Counts the rewinds the generated runners announce (a restore from a snapshot, or a full replay) and signals once a
  /// given count is reached, so a looping stream fails the test by its count instead of by a deadline.
  /// </summary>
  private sealed class RunnerRewindTally : ILoggerProvider, ILogger {
    private readonly Lock _gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waits = [];
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public string? Last { get; private set; }

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _warnings = new();

    /// <summary>Every warning logged, for a failure's diagnosis.</summary>
    public string Warnings => string.Join("\n", _warnings.TakeLast(80));

    public Task Reached(int count) {
      lock (_gate) {
        if (_count >= count) {
          return Task.CompletedTask;
        }
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waits.Add((count, signal));
        return signal.Task;
      }
    }

    public ILogger CreateLogger(string categoryName) => this;

    public void Dispose() { }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      _warnings.Enqueue($"{logLevel}: {message} {exception?.GetType().Name} {exception?.Message}");
      if ((!message.StartsWith("Restoring ", StringComparison.Ordinal)
           && !message.Contains("performing full replay", StringComparison.Ordinal))
          || !message.Contains(_itemTrailPerspective, StringComparison.Ordinal)) {
        return;
      }
      lock (_gate) {
        _count++;
        Last = message;
        foreach (var wait in _waits.Where(w => w.Count <= _count)) {
          wait.Signal.TrySetResult();
        }
      }
    }
  }
}
