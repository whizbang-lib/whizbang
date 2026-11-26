// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2601: a rewind starts only from an event the perspective reads, the types its association rows name. A perspective
/// with no association rows reads nothing: the store queues it no event (the fan-out joins the association rows), so no
/// rewind is raised for it. The trigger counts only when the perspective reads it.
/// </summary>
public partial class OriginPositionFillSqlTests {
  private const string NO_ASSOCIATIONS = "NoLongerRegistered+Projection";
  private const string READ_FLOOR_REPAIRED = "rewind_read_floor_repaired";
  private static readonly long[] _atVersionFour = [4];

  /// <summary>
  /// The shape a deployed environment held: a stream whose version 1 is a type the perspective does not read, with its
  /// body, its own events after it, no snapshots, and a rewind raised from version 1. The rewind starts at version 2, the
  /// first event it reads, and that is what is queued; version 1 is never queued for it.
  /// </summary>
  [Test]
  public async Task Rewind_FromAnEventThePerspectiveDoesNotRead_StartsAtTheFirstEventItReads_AndQueuesThatAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var unread = await _seedAsync(conn, null, null, minutesAgo: 10, stream: stream, version: 1, type: OTHER_TYPE);
    var firstRead = await _seedAsync(conn, null, null, minutesAgo: 10, stream: stream, version: 2);
    var last = await _seedAsync(conn, null, null, minutesAgo: 10, stream: stream, version: 3);
    await _associateAsync(conn, TYPE, READS_TYPE);
    await _cursorAsync(conn, stream, READS_TYPE, last);

    var rewound = await _scalarAsync(conn,
      $"SELECT _wh_rewind_perspectives_from(ARRAY['{stream}']::uuid[], ARRAY['{READS_TYPE}'], ARRAY['{unread}']::uuid[], NOW())");

    await Assert.That(rewound).IsEqualTo(1L);
    await Assert.That(await _triggerAsync(conn, stream, READS_TYPE)).IsEqualTo(firstRead)
      .Because("the perspective does not read version 1, so its replay starts at version 2");
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_perspective_events WHERE stream_id = '{stream}' AND event_id = '{firstRead}' AND processed_at IS NULL"))
      .IsEqualTo(1L).Because("the event the rewind starts from is queued, so a drain pair forms for it");
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_perspective_events WHERE stream_id = '{stream}' AND event_id = '{unread}'"))
      .IsEqualTo(0L).Because("an event the perspective does not read is never queued for it");
  }

  /// <summary>
  /// A perspective with no association rows reads nothing here: nothing is flagged, deleted or queued for it.
  /// </summary>
  [Test]
  public async Task Rewind_ForAPerspectiveWithNoAssociationRows_FlagsDeletesAndQueuesNothingAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var v1 = await _seedAsync(conn, null, null, minutesAgo: 10, stream: stream, version: 1);
    await _cursorAsync(conn, stream, NO_ASSOCIATIONS, v1);
    await _snapshotAsync(conn, stream, NO_ASSOCIATIONS, v1, 1);

    var rewound = await _scalarAsync(conn,
      $"SELECT _wh_rewind_perspectives_from(ARRAY['{stream}']::uuid[], ARRAY['{NO_ASSOCIATIONS}'], ARRAY['{v1}']::uuid[], NOW())");

    await Assert.That(rewound).IsEqualTo(0L);
    await Assert.That(await _flagAsync(conn, stream, NO_ASSOCIATIONS)).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_perspective_events WHERE stream_id = '{stream}'")).IsEqualTo(0L);
    await Assert.That(await _snapshotVersionsAsync(conn, stream, NO_ASSOCIATIONS)).IsEquivalentTo(_atVersionOne);
  }

  /// <summary>
  /// #2601 (part 2): the fill never rewinds a perspective with no association rows, however the stream's order changed,
  /// and never rewinds one whose own events keep their order when only events it does not read move.
  /// </summary>
  [Test]
  public async Task Fill_ThatReordersOnlyEventsAPerspectiveDoesNotRead_RewindsNothingAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var e = await _fourEventStreamAsync(conn, origin, stream);
    await _associateAsync(conn, OTHER_TYPE, READS_TYPE);
    foreach (var perspective in new[] { READS_TYPE, NO_ASSOCIATIONS }) {
      await _cursorAsync(conn, stream, perspective, e[3]);
      await _snapshotAsync(conn, stream, perspective, e[3], 4);
    }

    var result = await _store().FillAsync(new OriginPositionsAnswer { OriginServiceId = origin, Positions = [new(e[2], 40)] },
      _t0, CancellationToken.None);

    await Assert.That(result.PerspectivesRewound).IsEqualTo(0).Because("the reordered events are not ones either perspective reads");
    await Assert.That(await _flagAsync(conn, stream, READS_TYPE)).IsEqualTo(0L);
    await Assert.That(await _flagAsync(conn, stream, NO_ASSOCIATIONS)).IsEqualTo(0L)
      .Because("a perspective with no association rows is queued no event here, so it reads none");
    await Assert.That(await _snapshotVersionsAsync(conn, stream, NO_ASSOCIATIONS)).IsEquivalentTo(_atVersionFour);
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_perspective_events WHERE stream_id = '{stream}'")).IsEqualTo(0L);
  }

  /// <summary>
  /// The one-off repair over what a database already holds, in the deployed shapes. A cursor flagged from an event its
  /// perspective does not read is moved to the first event it reads, its queued replay of the unread event deleted and the
  /// new trigger queued. A cursor of a perspective with no association rows is cleared and its queued replay deleted. A
  /// trigger the perspective reads is left alone. A second run changes nothing.
  /// </summary>
  [Test]
  public async Task Repair_MovesAnUnreadTriggerToTheFirstReadEvent_ClearsOneWithNone_LeavesTheRest_AndRunsOnceAsync() {
    await using var conn = await _openAsync();
    var moved = TrackedGuid.New().Value;
    var cleared = TrackedGuid.New().Value;
    var kept = TrackedGuid.New().Value;
    var m1 = await _seedAsync(conn, null, null, minutesAgo: 10, stream: moved, version: 1, type: OTHER_TYPE);
    var m2 = await _seedAsync(conn, null, null, minutesAgo: 10, stream: moved, version: 2);
    var m3 = await _seedAsync(conn, null, null, minutesAgo: 10, stream: moved, version: 3);
    var c1 = await _seedAsync(conn, null, null, minutesAgo: 10, stream: cleared, version: 1);
    var k1 = await _seedAsync(conn, null, null, minutesAgo: 10, stream: kept, version: 1);
    await _associateAsync(conn, TYPE, READS_TYPE);
    await _cursorAsync(conn, moved, READS_TYPE, m3, flaggedAt: m1);
    await _cursorAsync(conn, cleared, NO_ASSOCIATIONS, c1, flaggedAt: c1);
    await _cursorAsync(conn, kept, READS_TYPE, k1, flaggedAt: k1);
    (Guid Stream, string Perspective, Guid Event)[] queued = [(moved, READS_TYPE, m1), (cleared, NO_ASSOCIATIONS, c1), (kept, READS_TYPE, k1)];
    foreach (var (stream, perspective, evt) in queued) {
      await _execAsync(conn, $"""
        INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
        VALUES (gen_random_uuid(), '{stream}', '{perspective}', '{evt}', 0, 1, 62, NOW())
        """);
    }
    await _execAsync(conn, $"DELETE FROM wh_settings WHERE setting_key = '{READ_FLOOR_REPAIRED}'");

    var first = await _readFloorRepairAsync(conn);
    var second = await _readFloorRepairAsync(conn);

    await Assert.That(first).IsEqualTo((1L, 1L, 2L))
      .Because("one cursor cleared, one moved, and the two queued replays of events their perspective does not read deleted");
    await Assert.That(second).IsEqualTo((0L, 0L, 0L)).Because("the repair runs once per database");
    await Assert.That(await _triggerAsync(conn, moved, READS_TYPE)).IsEqualTo(m2)
      .Because("version 2 is the first event at or after the trigger that the perspective reads");
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_perspective_events WHERE stream_id = '{moved}' AND event_id = '{m2}' AND processed_at IS NULL AND attempts = 0"))
      .IsEqualTo(1L).Because("the new trigger is queued so the rewind runs");
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_perspective_events WHERE stream_id = '{moved}' AND event_id = '{m1}'"))
      .IsEqualTo(0L);
    await Assert.That(await _flagAsync(conn, cleared, NO_ASSOCIATIONS)).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = '{cleared}' AND rewind_trigger_event_id IS NULL AND rewind_flagged_at IS NULL AND rewind_first_flagged_at IS NULL"))
      .IsEqualTo(1L).Because("the trigger and both flag times are cleared with the flag");
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_perspective_events WHERE stream_id = '{cleared}'")).IsEqualTo(0L);
    await Assert.That(await _triggerAsync(conn, kept, READS_TYPE)).IsEqualTo(k1).Because("a trigger the perspective reads is its own floor");
    await Assert.That(await _scalarAsync(conn, $"SELECT count(*) FROM wh_perspective_events WHERE stream_id = '{kept}'")).IsEqualTo(1L);
  }

  private static async Task<(long Cleared, long Moved, long Deleted)> _readFloorRepairAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT cursors_cleared, cursors_moved, rows_deleted FROM _wh_repair_rewinds_from_unread_events()";
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
  }
}
