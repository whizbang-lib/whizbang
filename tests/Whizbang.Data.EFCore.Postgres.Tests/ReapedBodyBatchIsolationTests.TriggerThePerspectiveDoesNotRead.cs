// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A rewind whose trigger is an event the perspective does not read completes. The shape a deployed environment held:
/// the origin-position fill flagged every cursor of a stream to rewind from the stream's first event, and that event is
/// of a type the perspective does not read and this service does not register. The trigger is queued for the
/// perspective, its body is present, and nothing could ever replay it: the drain reads no event it can apply, forms no
/// pair, and leaves the row leased. The row was claimed on every pass with nothing recorded against it (failures 0, so
/// the dead-letter bound never applied) and the cursor stayed flagged, holding a Blocking startup to its cap on every start.
/// </summary>
public partial class ReapedBodyBatchIsolationTests {
  /// <summary>Claims of the stream after the flag with its rewind not done: the drain has given it up that many times.</summary>
  private const int CLAIMS_WITH_THE_REWIND_UNDONE = 40;

  /// <summary>
  /// The stream: version 1 is an event of a type the service does not register, with its body; versions 2 and 3 are the
  /// perspective's own events, applied. The cursor is flagged from version 1 and version 1 queued for it, as a database
  /// flagged before migration 602 holds it, while the worker runs. The rewind completes: the queued replay is completed, the cursor is no longer flagged and the model
  /// holds the stream's last amount.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task ARewindFromAnEventThePerspectiveDoesNotRead_Completes_AndTheCursorIsNoLongerFlaggedAsync(
      CancellationToken cancellationToken) {
    await using var pipeline = _composePipeline(maxFailures: 10, RewindStartupMode.Background);
    await using var conn = await _openAsync(cancellationToken);
    await pipeline.RegisterAsync(conn, cancellationToken);
    var perspective = TypeNameFormatter.GetPerspectiveName(typeof(OrderPerspective));
    var stream = (Guid)TrackedGuid.New();
    var unread = await _seedAnEventOfATypeNoOneHereReadsAsync(conn, stream, cancellationToken);

    await pipeline.StartAsync(cancellationToken);
    var own = await pipeline.CommitAsync(conn, [(stream, 10m), (stream, 20m)], cancellationToken);
    var ownWork = new List<Guid>();
    foreach (var eventId in own) {
      ownWork.Add(await _workIdForEventAsync(conn, eventId, cancellationToken));
    }
    await pipeline.Completed.WaitAsync(items => ownWork.All(items.Contains), cancellationToken);
    await Assert.That(await _readAmountAsync(conn, stream, cancellationToken)).IsEqualTo(20m);

    // The flag as a database raised before migration 602 holds it: the cursor flagged from the stream's first event and
    // that event queued for the perspective. Since 602 the rewind floors such a trigger to the first event it reads, so the
    // shape is seeded as stored; the worker has to resolve it either way.
    await using (var flag = conn.CreateCommand()) {
      flag.CommandText = """
        UPDATE wh_perspective_cursors SET status = status | 32, rewind_trigger_event_id = @t, rewind_flagged_at = NOW(),
                                          rewind_first_flagged_at = NOW()
        WHERE stream_id = @s AND perspective_name = @p;
        INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
        VALUES (gen_random_uuid(), @s, @p, @t, compute_partition(@s, 10000), 1, 62, NOW());
        """;
      flag.Parameters.AddWithValue("s", stream);
      flag.Parameters.AddWithValue("p", perspective);
      flag.Parameters.AddWithValue("t", unread);
      await flag.ExecuteNonQueryAsync(cancellationToken);
    }
    var queued = await _pendingWorkAsync(conn, stream, cancellationToken);
    await Assert.That(queued.Count).IsEqualTo(1).Because("the trigger is queued for the perspective");

    // The replay is completed, unless the stream is claimed again and again with it still pending: then the drain gave
    // it up each time, and the test ends with what the database recorded against the row.
    var replayed = pipeline.Completed.WaitAsync(items => queued.All(items.Contains), cancellationToken, fromNow: true);
    var givenUp = pipeline.Claims.WaitAsync(
      items => items.Count(c => c.Contains(stream)) >= CLAIMS_WITH_THE_REWIND_UNDONE, cancellationToken, fromNow: true);
    var first = await Task.WhenAny(replayed, givenUp);
    if (first == givenUp && givenUp.IsCompletedSuccessfully && !replayed.IsCompleted) {
      var row = await _rowStateAsync(conn, queued[0], cancellationToken);
      Assert.Fail($"the rewind never ran: the stream was claimed {CLAIMS_WITH_THE_REWIND_UNDONE} times after the flag and its "
        + $"queued replay is still pending ({row}), the cursor still flagged");
    }
    await replayed;

    await Assert.That(await _cursorStatusAsync(conn, stream, perspective, cancellationToken)).IsEqualTo(2)
      .Because("Completed, with the RewindRequired bit cleared by the rewind");
    await Assert.That(await _readAmountAsync(conn, stream, cancellationToken)).IsEqualTo(20m)
      .Because("the replay folds the perspective's own events, and the event it does not read changes nothing");
  }

  /// <summary>
  /// Version 1 of the stream: an event of a type no perspective or handler here registers, stamped, with its body, as a
  /// received event of another service's contract is stored. Returns its id.
  /// </summary>
  private static async Task<Guid> _seedAnEventOfATypeNoOneHereReadsAsync(NpgsqlConnection conn, Guid streamId, CancellationToken ct) {
    var id = (Guid)TrackedGuid.New();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at,
                                  commit_sequence, flags)
      VALUES (@id, @sid, @sid, 'Order', 1, 'Elsewhere.Contracts.StepApprovedEvent, Elsewhere.Contracts', NULL, NOW(),
              nextval('wh_commit_seq'), 0);
      INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES (@id, '{"Step": 1}'::jsonb, '{}'::jsonb);
      """;
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("sid", streamId);
    await cmd.ExecuteNonQueryAsync(ct);
    return id;
  }

  private static async Task<List<Guid>> _pendingWorkAsync(NpgsqlConnection conn, Guid streamId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_work_id FROM wh_perspective_events WHERE stream_id = @sid AND processed_at IS NULL";
    cmd.Parameters.AddWithValue("sid", streamId);
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<string> _rowStateAsync(NpgsqlConnection conn, Guid workId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT format('attempts=%s failures=%s error=%s', attempts, failures, COALESCE(error, 'NULL'))
      FROM wh_perspective_events WHERE event_work_id = @work
      """;
    cmd.Parameters.AddWithValue("work", workId);
    return (string?)await cmd.ExecuteScalarAsync(ct) ?? "row gone";
  }

  private static async Task<int> _cursorStatusAsync(NpgsqlConnection conn, Guid streamId, string perspective, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT status FROM wh_perspective_cursors WHERE stream_id = @sid AND perspective_name = @p";
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("p", perspective);
    return (int)(await cmd.ExecuteScalarAsync(ct))!;
  }
}
