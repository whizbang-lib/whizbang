// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// How far the live sink's applies of one collective reached, as a replay reads it (#1970): the progress record of each
/// handler's apply (<see cref="CollectiveApplyProgress"/>), and whether the collective's work row is still pending.
/// </summary>
/// <remarks>
/// <para>A replay folds the collective into a row through a handler only where the live row holds it, or never will:</para>
/// <list type="bullet">
///   <item><description>The handler's apply completed: every row.</description></item>
///   <item><description>It is partway, or was abandoned by a dead letter: the rows up to its <c>through_id</c>, the ones its
///     committed batches reached. A pending apply applies the rest live; an abandoned one leaves them without it.</description></item>
///   <item><description>It has no record, and the collective is still queued or was dead-lettered: no row. The live sink
///     applies a queued collective to the replayed row afterwards, once; a dead-lettered one applied nothing.</description></item>
///   <item><description>It has no record otherwise (applied before records existed, or its record was swept): every row.</description></item>
/// </list>
/// <para>
/// A row is reached when its id sorts at or below <c>through_id</c> in Postgres uuid order, which is the order of the ids'
/// text, the order the sink's batches take the rows in.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events#collective-replay-from-a-start</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyReachTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CollectiveReplayRebuildIntegrationTests.cs:Rebuild_ACollectiveDeadLetteredPartway_FoldsOnlyIntoTheRowsItReachedAsync</tests>
public sealed class CollectiveApplyReach {
  /// <summary>The entry key under which a dead letter records that a handler with no record applied nothing.</summary>
  public const string NOTHING_ELSE_APPLIED_KEY = "*";

  private readonly Dictionary<string, (Guid? Through, bool Completed)> _entries;
  private readonly bool _nothingElseApplied;

  private CollectiveApplyReach(
      bool queued, bool nothingElseApplied, Dictionary<string, (Guid? Through, bool Completed)> entries) {
    Queued = queued;
    _nothingElseApplied = nothingElseApplied;
    _entries = entries;
  }

  /// <summary>Whether the collective's work row is still pending: queued, mid-apply, or recovered from a dead letter.</summary>
  public bool Queued { get; }

  /// <summary>Whether the live row <paramref name="rowId"/> holds, or will never receive, the collective through the
  /// handler <paramref name="entryKey"/>, so a replay of the row folds it.</summary>
  /// <param name="entryKey">The handler's <see cref="Whizbang.Core.Perspectives.CollectiveApplyEntry.EntryKey"/>.</param>
  /// <param name="rowId">The row the replay folds into.</param>
  /// <returns>True when the replay folds the collective into the row through the handler.</returns>
  public bool Reaches(string entryKey, Guid rowId) {
    if (_entries.TryGetValue(entryKey, out var entry)) {
      return entry.Completed
        || (entry.Through is Guid through
            && string.CompareOrdinal(rowId.ToString("D"), through.ToString("D")) <= 0);
    }
    return !(Queued || _nothingElseApplied);
  }

  /// <summary>Reads the reach of each of <paramref name="eventIds"/> that has a record or a pending work row.</summary>
  /// <param name="database">The service's database.</param>
  /// <param name="eventIds">The collectives a replay folds.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The reach by collective event id; a collective missing from it has neither, and folds in full.</returns>
  public static async Task<IReadOnlyDictionary<Guid, CollectiveApplyReach>> ReadAsync(
      PostgresArchiveConnection database, IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(database);
    ArgumentNullException.ThrowIfNull(eventIds);
    var rows = new List<(Guid EventId, string? EntryKey, Guid? Through, bool Completed, bool Abandoned, bool Queued)>();
    await using (var connection = await database.Open(cancellationToken).ConfigureAwait(false)) {
      await using var command = new NpgsqlCommand(
        "SELECT out_event_id, out_entry_key, out_through_id, out_completed, out_abandoned, out_queued FROM "
        + PgIdentifier.Qualify(database.Schema, "wh_collective_apply_reach") + "(@ids)", connection);
      command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = eventIds.ToArray() });
      await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        rows.Add((
          reader.GetGuid(0),
          await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1),
          await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(2),
          reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5)));
      }
    }
    return From(rows);
  }

  /// <summary>Folds the reach read's rows into one reach per collective.</summary>
  /// <param name="rows">One row per record, and one (no entry key, queued) per collective whose work row is pending.</param>
  /// <returns>The reach by collective event id.</returns>
  internal static Dictionary<Guid, CollectiveApplyReach> From(
      IEnumerable<(Guid EventId, string? EntryKey, Guid? Through, bool Completed, bool Abandoned, bool Queued)> rows) =>
    rows.GroupBy(r => r.EventId).ToDictionary(
      g => g.Key,
      g => new CollectiveApplyReach(
        queued: g.Any(r => r.Queued),
        nothingElseApplied: g.Any(r => r.EntryKey == NOTHING_ELSE_APPLIED_KEY && r.Abandoned),
        entries: g.Where(r => r.EntryKey is not null and not NOTHING_ELSE_APPLIED_KEY)
          .ToDictionary(r => r.EntryKey!, r => (r.Through, r.Completed), StringComparer.Ordinal)));
}
