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
    await using var pipeline = _composePipeline(new RewindCountingLogger(), snapshots: true);
    await using var conn = await _openAsync(ct);
    await pipeline.RegisterAsync(conn, ct);
    await _registerItemProgressAsync(conn, ct);

    var specimen = await _seedFirstEventHasTheHighestIdAsync(pipeline, conn, streamId, ct);
    var workIds = await _workIdsAsync(conn, streamId, ct);
    await Assert.That(workIds.Count).IsEqualTo(2).Because("versions 3 and 4 are pending, as on the deployed environment");
    await Assert.That(await _scalarAsync<int>(conn, "SELECT wh_queue_unreachable_rewinds()", streamId, ct)).IsEqualTo(0)
      .Because("a looping stream with pending work needs no repair: its next claim settles it");

    await _convergesAfterOneRewindAsync(pipeline, conn, streamId, specimen, workIds, ct);
  }

  /// <summary>
  /// The live shape with its pending work retired (#2379): nothing is left to claim, so the flag would wait for the
  /// stream's next event. The upgrade repair queues the trigger, and one rewind settles the stream.
  /// </summary>
  [Test]
  public async Task AStreamWhoseFirstEventHasTheHighestId_WithItsWorkRetired_IsQueuedOnUpgrade_AndConvergesAfterOneRewindAsync() {
    var ct = CancellationToken.None;
    var streamId = (Guid)TrackedGuid.New();
    await using var pipeline = _composePipeline(new RewindCountingLogger(), snapshots: true);
    await using var conn = await _openAsync(ct);
    await pipeline.RegisterAsync(conn, ct);
    await _registerItemProgressAsync(conn, ct);

    var specimen = await _seedFirstEventHasTheHighestIdAsync(pipeline, conn, streamId, ct);
    await _executeAsync(conn, "DELETE FROM wh_perspective_events WHERE stream_id = @id", streamId, ct);

    // The deploy: migration 485's repair, as it runs once on upgrade.
    await Assert.That(await _scalarAsync<int>(conn, "SELECT wh_queue_unreachable_rewinds()", streamId, ct)).IsEqualTo(1);
    var workIds = await _workIdsAsync(conn, streamId, ct);
    await Assert.That(workIds.Count).IsEqualTo(1).Because("the flag's trigger is queued, unleased");

    await _convergesAfterOneRewindAsync(pipeline, conn, streamId, specimen, workIds, ct);
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
    await using var pipeline = _composePipeline(new RewindCountingLogger(), snapshots: true);
    await using var conn = await _openAsync(ct);
    await pipeline.RegisterAsync(conn, ct);
    await _registerItemProgressAsync(conn, ct);
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, []);
    var started = TypeNameFormatter.Format(typeof(ItemProgressStartedEvent));
    var completed = TypeNameFormatter.Format(typeof(ItemProgressCompletedEvent));
    var options = JsonContextRegistry.CreateCombinedOptions();
    var startedPayload = JsonSerializer.Serialize(new ItemProgressStartedEvent { StreamId = streamId },
      options.GetTypeInfo(typeof(ItemProgressStartedEvent)));
    var completedPayload = JsonSerializer.Serialize(new ItemProgressCompletedEvent { StreamId = streamId },
      options.GetTypeInfo(typeof(ItemProgressCompletedEvent)));

    var ids = await h.EmitAsync([
      new TwoServiceEmit(streamId, started, 0, null, startedPayload),
      new TwoServiceEmit(streamId, started, 0, null, startedPayload),
      new TwoServiceEmit(streamId, started, 0, null, startedPayload),
      new TwoServiceEmit(streamId, completed, 0, null, completedPayload),
    ]);
    await Assert.That((await h.DeliverAsync([ids[3]])).Count).IsEqualTo(1);
    await Assert.That((await h.DeliverAsync(ids[..3])).Count).IsEqualTo(3);
    var specimen = new FirstEventHasTheHighestId(Version1: ids[3], Version2: ids[0], Version3: ids[1], Version4: ids[2]);
    await _leftLoopingAsync(pipeline, conn, streamId, specimen, ct);
    var workIds = await _releasedWorkIdsAsync(conn, streamId, pipeline.InstanceProvider.InstanceId, ct);
    await Assert.That(workIds.Count).IsEqualTo(2).Because("versions 3 and 4 are pending on the consumer");

    await _convergesAfterOneRewindAsync(pipeline, conn, streamId, specimen, workIds, ct);
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
  /// Starts the pipeline and asserts the stream settles with one rewind: its work completes before the runners rewind it
  /// <see cref="LOOP_REWINDS"/> times, the cursor ends on version 4 with its flag clear, nothing is pending, no later poll
  /// claims it again, and the row keeps its Completed state.
  /// </summary>
  private static async Task _convergesAfterOneRewindAsync(
      Pipeline pipeline, NpgsqlConnection conn, Guid streamId, FirstEventHasTheHighestId specimen, List<Guid> workIds,
      CancellationToken ct) {
    await pipeline.StartAsync(ct);
    var settled = Task.WhenAll(workIds.Select(pipeline.CompletionOf));
    var looping = pipeline.RunnerRewinds.Reached(LOOP_REWINDS);
    var first = await Task.WhenAny(settled, looping).WaitAsync(_signalDeadline, ct);
    Console.WriteLine($"settled={first == settled} runner rewinds={pipeline.RunnerRewinds.Count} {await _cursorAsync(conn, streamId, ct)}");
    await Assert.That(first == looping).IsFalse()
      .Because($"the stream rewound {pipeline.RunnerRewinds.Count} times without completing its work: {pipeline.RunnerRewinds.Last}");
    await settled;
    var rewindsWhenSettled = pipeline.RunnerRewinds.Count;

    var reclaims = await _reclaimsOverQuietPollsAsync(pipeline, conn, streamId, ct);
    var cursorEvent = await _scalarAsync<Guid>(conn,
      "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @id", streamId, ct);
    var flagged = await _scalarAsync<bool>(conn,
      "SELECT COALESCE(bool_or((status & 32) = 32), false) FROM wh_perspective_cursors WHERE stream_id = @id", streamId, ct);
    var pending = await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_perspective_events WHERE stream_id = @id AND processed_at IS NULL", streamId, ct);
    var completed = await _scalarAsync<bool>(conn,
      "SELECT (data->>'Completed')::boolean FROM wh_per_item_progress WHERE id = @id", streamId, ct);
    var attempts = await _scalarAsync<int>(conn,
      "SELECT (data->>'Attempts')::int FROM wh_per_item_progress WHERE id = @id", streamId, ct);

    await Assert.That(rewindsWhenSettled).IsEqualTo(1).Because("one rewind settles the stream");
    await Assert.That(pipeline.RunnerRewinds.Count).IsEqualTo(rewindsWhenSettled).Because("nothing rewinds it again");
    await Assert.That(reclaims).IsEqualTo(0).Because("a settled stream is not claimed again");
    await Assert.That(cursorEvent).IsEqualTo(specimen.Version4).Because("the cursor ends on the stream's last version");
    await Assert.That(flagged).IsFalse().Because("the completed rewind clears its flag");
    await Assert.That(pending).IsEqualTo(0L).Because("the pending work is completed");
    await Assert.That(completed).IsTrue().Because("the row never loses the Completed state it holds");
    await Assert.That(attempts).IsEqualTo(1)
      .Because("the model orders on the event id, so the replayed Started events, all with lower ids than the Completed one, are refused, and the rewind still settles");
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
      Pipeline pipeline, NpgsqlConnection conn, Guid streamId, FirstEventHasTheHighestId specimen, CancellationToken ct) {
    await using (var shape = conn.CreateCommand()) {
      shape.CommandText = """
        DELETE FROM wh_perspective_events WHERE stream_id = @stream AND event_id IN (@v1, @v2);
        UPDATE wh_perspective_events SET instance_id = NULL, lease_expiry = NULL WHERE stream_id = @stream;
        INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at,
                                            rewind_trigger_event_id, rewind_flagged_at, rewind_first_flagged_at)
        VALUES (@stream, @perspective, @v2, 2 | 32, NOW(), @v2, NOW(), NOW())
        ON CONFLICT (stream_id, perspective_name) DO UPDATE
          SET last_event_id = EXCLUDED.last_event_id, status = EXCLUDED.status,
              rewind_trigger_event_id = EXCLUDED.rewind_trigger_event_id;
        INSERT INTO wh_per_item_progress (id, data, metadata, scope, created_at, updated_at, version)
        VALUES (@stream,
          jsonb_build_object('Id', @stream, 'Started', true, 'Attempts', 1, 'Completed', true),
          jsonb_build_object('EventId', @v1::text, 'EventType', @completedType, 'Timestamp', NOW(),
            'CommitSequence', (SELECT commit_sequence FROM wh_event_store WHERE event_id = @v2)),
          '{}'::jsonb, NOW(), NOW(), 2)
        ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data, metadata = EXCLUDED.metadata;
        """;
      shape.Parameters.AddWithValue("v1", specimen.Version1);
      shape.Parameters.AddWithValue("v2", specimen.Version2);
      shape.Parameters.AddWithValue("stream", streamId);
      shape.Parameters.AddWithValue("perspective", _itemProgressPerspective);
      shape.Parameters.AddWithValue("completedType", typeof(ItemProgressCompletedEvent).FullName!);
      await shape.ExecuteNonQueryAsync(ct);
    }

    // The one snapshot, at version 1, in the format the runner writes.
    var model = new ItemProgress { Id = streamId, Started = true, Attempts = 1, Completed = true };
    using (var modelDoc = JsonSerializer.SerializeToDocument(model,
             JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(ItemProgress)))) {
      using var snapshot = SnapshotEnvelope.Wrap(modelDoc.RootElement, SerializationVersion.CURRENT);
      var version1Sequence = await _scalarAsync<long>(conn,
        "SELECT commit_sequence FROM wh_event_store WHERE event_id = @id", specimen.Version1, ct);
      await pipeline.Services.GetRequiredService<IPerspectiveSnapshotStore>().CreateSnapshotAsync(
        streamId, _itemProgressPerspective, specimen.Version1, version1Sequence, snapshot, ct);
    }

    var snapshotAt = await _scalarAsync<string>(conn, """
      SELECT format('v%s seq %s', snapshot_version, sequence_number) FROM wh_perspective_snapshots WHERE stream_id = @id
      """, streamId, ct);
    await Assert.That(snapshotAt).IsEqualTo("v1 seq 1").Because("one snapshot, at version 1");
    var versionsByIdOrder = await _scalarAsync<string>(conn,
      "SELECT string_agg(version::text, ',' ORDER BY event_id) FROM wh_event_store WHERE stream_id = @id", streamId, ct);
    await Assert.That(versionsByIdOrder).IsEqualTo("2,3,4,1").Because("version 1 has the highest id, version 2 the lowest");
  }

  private static readonly string _itemProgressPerspective = TypeNameFormatter.GetPerspectiveName(typeof(ItemProgressPerspective));

  /// <summary>Associates the per-item events with their perspective, so a commit queues their work.</summary>
  private static async Task _registerItemProgressAsync(NpgsqlConnection conn, CancellationToken ct) {
    foreach (var eventType in new[] { typeof(ItemProgressStartedEvent), typeof(ItemProgressCompletedEvent) }) {
      await using var assoc = conn.CreateCommand();
      assoc.CommandText = """
        INSERT INTO wh_message_associations
          (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
        VALUES (gen_random_uuid(), @eventType, 'perspective', @target, 'rewind-loop-svc', @eventType, NOW(), NOW())
        ON CONFLICT DO NOTHING
        """;
      assoc.Parameters.AddWithValue("eventType", TypeNameFormatter.Format(eventType));
      assoc.Parameters.AddWithValue("target", _itemProgressPerspective);
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
      if (!message.StartsWith("Restoring ", StringComparison.Ordinal)
          && !message.Contains("performing full replay", StringComparison.Ordinal)) {
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
