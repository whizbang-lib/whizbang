// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// The event store's version questions, which account for the archive the stream lifecycle ledger records (#1933). Shared
/// by both Postgres drivers so they ask them the same way. Parameters: <c>@StreamId</c>, and <c>@BeforeEventId</c> for
/// the history probes.
/// </summary>
/// <remarks>
/// <para>
/// Each question is one statement, so a stream that was never archived costs what it always did: one round trip. The
/// ledger is read in the same statement, by its key.
/// </para>
/// <para>
/// A stream has archived history when its ledger row is <c>Archived</c>, names an archive (<c>archive_locator</c>), or
/// records that its archive was deleted (<c>archive_deleted_at</c>). Every archive takes a stream's lowest versions, so
/// that history is older than every hot event. The ledger records that an archive exists, not the types it holds, so
/// the typed probe counts archived history for every perspective: a false "yes" costs a re-fold that reads through the
/// archive and builds the right row, while a false "no" would build the row from the new events alone.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/archive-store#version-questions</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamHistoryProbeSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchivedStreamHistoryProbeTests.cs</tests>
internal static class ArchivedStreamHistorySql {
  // The ledger row of a stream with archived history, read by its key.
  private static string _archivedHistory(string prefix) => $"""
    SELECT 1 FROM {prefix}wh_stream_lifecycle l
    WHERE l.stream_id = @StreamId
      AND (l.state = 'Archived' OR l.archive_locator IS NOT NULL OR l.archive_deleted_at IS NOT NULL)
    """;

  /// <summary>
  /// The stream's last version: its newest hot version, else the last version a stream archived whole recorded on its
  /// tombstone, else -1. The ledger lookup is the second argument of the <c>COALESCE</c>, so it runs only when nothing is
  /// hot.
  /// </summary>
  /// <param name="prefix">The schema prefix (<see cref="PgIdentifier.QualifyPrefix"/>), empty for the search path.</param>
  /// <returns>The statement; one <c>bigint</c> column.</returns>
  internal static string LastSequence(string prefix) => $"""
    SELECT COALESCE(
      (SELECT MAX(e.version) FROM {prefix}wh_event_store e WHERE e.stream_id = @StreamId),
      (SELECT l.last_version FROM {prefix}wh_stream_lifecycle l WHERE l.stream_id = @StreamId AND l.state = 'Archived'),
      -1)
    """;

  /// <summary>Whether the stream holds a hot event ordered before <c>@BeforeEventId</c>, or has archived history.</summary>
  /// <param name="prefix">The schema prefix (<see cref="PgIdentifier.QualifyPrefix"/>), empty for the search path.</param>
  /// <returns>The statement; one <c>boolean</c> column.</returns>
  internal static string HasEventsBefore(string prefix) => $"""
    SELECT EXISTS (SELECT 1 FROM {prefix}wh_event_store e WHERE e.stream_id = @StreamId AND e.event_id < @BeforeEventId)
        OR EXISTS ({_archivedHistory(prefix)})
    """;

  /// <summary>
  /// The distinct stored types of the stream's hot events ordered before <c>@BeforeEventId</c>, plus one null row when
  /// the stream has archived history.
  /// </summary>
  /// <param name="prefix">The schema prefix (<see cref="PgIdentifier.QualifyPrefix"/>), empty for the search path.</param>
  /// <returns>The statement; one nullable <c>text</c> column.</returns>
  internal static string EventTypesBefore(string prefix) => $"""
    (SELECT DISTINCT e.event_type FROM {prefix}wh_event_store e WHERE e.stream_id = @StreamId AND e.event_id < @BeforeEventId)
    UNION ALL
    (SELECT NULL::text FROM ({_archivedHistory(prefix)}) archived)
    """;

  /// <summary>
  /// Reads the rows of <see cref="EventTypesBefore"/>: true when one is the null row of archived history, or names a
  /// type in <paramref name="eventTypes"/> (matched through <see cref="Whizbang.Core.Messaging.EventTypeMatchingHelper"/> only).
  /// </summary>
  /// <param name="storedTypes">The rows.</param>
  /// <param name="eventTypes">The types the perspective folds.</param>
  /// <returns>Whether the stream has history the perspective folds, or archived history.</returns>
  internal static bool AnyHandledOrArchived(IEnumerable<string?> storedTypes, IReadOnlyList<Type> eventTypes) {
    var lookup = global::Whizbang.Core.Messaging.EventTypeMatchingHelper.BuildTypeLookup(eventTypes);
    foreach (var stored in storedTypes) {
      if (stored is null || global::Whizbang.Core.Messaging.EventTypeMatchingHelper.TryResolveType(lookup, stored, out _)) {
        return true;
      }
    }
    return false;
  }
}
