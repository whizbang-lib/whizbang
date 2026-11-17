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
    var rewinds = new RewindCountingLogger();
    await using var pipeline = _composePipeline(rewinds, snapshots: true);
    await using var conn = await _openAsync(ct);
    await pipeline.RegisterAsync(conn, ct);
    await _registerItemProgressAsync(conn, ct);

    var specimen = await _seedFirstEventHasTheHighestIdAsync(pipeline, conn, streamId, ct);
    var workIds = await _workIdsAsync(conn, streamId, ct);
    await Assert.That(workIds.Count).IsEqualTo(2).Because("versions 3 and 4 are pending, as on the deployed environment");

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

    await Assert.That(rewindsWhenSettled).IsEqualTo(1).Because("one rewind settles the stream");
    await Assert.That(pipeline.RunnerRewinds.Count).IsEqualTo(rewindsWhenSettled).Because("nothing rewinds it again");
    await Assert.That(reclaims).IsEqualTo(0).Because("a settled stream is not claimed again");
    await Assert.That(cursorEvent).IsEqualTo(specimen.Version4).Because("the cursor ends on the stream's last version");
    await Assert.That(flagged).IsFalse().Because("the completed rewind clears its flag");
    await Assert.That(pending).IsEqualTo(0L).Because("the pending work is completed");
    await Assert.That(completed).IsTrue().Because("the row never loses the Completed state it holds");
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

    await using (var shape = conn.CreateCommand()) {
      // Received from one origin: version 2 sits before version 1 there; versions 3 and 4 carry no origin position.
      // Versions 1 and 2 were applied (their work is gone); 3 and 4 are pending and unleased.
      shape.CommandText = """
        UPDATE wh_event_store
        SET origin_service_id = @origin,
            origin_commit_sequence = CASE event_id WHEN @v1 THEN 4790121 WHEN @v2 THEN 4790063 END
        WHERE stream_id = @stream;
        DELETE FROM wh_perspective_events WHERE stream_id = @stream AND event_id IN (@v1, @v2);
        UPDATE wh_perspective_events SET instance_id = NULL, lease_expiry = NULL WHERE stream_id = @stream;
        INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at,
                                            rewind_trigger_event_id, rewind_flagged_at, rewind_first_flagged_at)
        VALUES (@stream, @perspective, @v2, 2 | 32, NOW(), @v2, NOW(), NOW());
        INSERT INTO wh_per_item_progress (id, data, metadata, scope, created_at, updated_at, version)
        VALUES (@stream,
          jsonb_build_object('Id', @stream, 'Started', true, 'Attempts', 1, 'Completed', true),
          jsonb_build_object('EventId', @v1::text, 'EventType', @completedType, 'Timestamp', NOW(),
            'CommitSequence', (SELECT commit_sequence FROM wh_event_store WHERE event_id = @v2)),
          '{}'::jsonb, NOW(), NOW(), 2);
        """;
      shape.Parameters.AddWithValue("origin", _specimenOrigin);
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
    return specimen;
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
