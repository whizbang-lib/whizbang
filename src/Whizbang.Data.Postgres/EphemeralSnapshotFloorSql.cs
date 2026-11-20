// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// When a snapshot is a rewind floor for the ephemeral bodies at or below it (#2456): once its own event is past its grace
/// window. A late event inside the window can land anywhere above the bodies already past it, so a snapshot taken at the
/// tip, above bodies still inside the window, is no floor for them; reaping the bodies below it left a late event landing
/// there with nothing to rewind from. One fragment for the readers that mirror the reap's coverage gate (migration 534):
/// the reap-driven snapshot targets and the destruction batch.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#the-reap-boundary-snapshot</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReapBoundaryFloorSqlTests.cs</tests>
internal static class EphemeralSnapshotFloorSql {
  /// <summary>
  /// A predicate on a snapshot aliased <c>s</c>: true unless the store holds the snapshot's event and it is still inside
  /// its grace window (the event type's own window, else the <c>ephemeral_rewind_grace_seconds</c> setting, else 300
  /// seconds). A snapshot whose event the store no longer holds is long past the window.
  /// </summary>
  /// <param name="prefix">The schema prefix, built by <see cref="PgIdentifier"/>, or empty.</param>
  internal static string SnapshotEventPastGrace(string prefix) =>
    $"NOT EXISTS (SELECT 1 FROM {prefix}wh_event_store sv " +
    $"LEFT JOIN {prefix}wh_ephemeral_type_grace sg ON sg.event_type = sv.event_type " +
    "WHERE sv.event_id = s.snapshot_event_id " +
    "AND sv.created_at >= NOW() - (COALESCE(sg.grace_seconds, " +
    $"(SELECT setting_value::int FROM {prefix}wh_settings WHERE setting_key = 'ephemeral_rewind_grace_seconds'), 300) " +
    "* INTERVAL '1 second'))";
}
