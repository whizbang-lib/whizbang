// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>
/// Names, at startup, each perspective whose model is recursive when the database was last migrated by a release before
/// #2112, because rows that release wrote may have kept only the first level of the recursion and the perspective must
/// be rebuilt from its events. Nothing is rebuilt automatically (owner decision on #2112).
/// </summary>
/// <remarks>
/// <para>
/// "Last migrated by a release before the fix" is read from the migration ledger: migration
/// <see cref="MARKER_MIGRATION"/> ships with the fix and changes nothing, so its ledger row is the fact. The check runs
/// before a start's migrations apply, so it reads the ledger the previous release left. Without a ledger the database
/// is new; with the row it is already at or after the fix. Either way nothing is said.
/// </para>
/// <para>
/// A perspective is named only when its table exists and holds a row: a table the earlier release never wrote to has
/// nothing truncated in it. Each is named once, at Warning, with <see cref="EVENT_ID"/>, on the instance doing the
/// schema work; the migrations that follow record the marker, so the next start is silent.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/RecursivePerspectiveRebuildNoticeTests.cs</tests>
public static partial class RecursiveDocumentRebuildNotice {
  /// <summary>The migration whose ledger row says the database was migrated by a release with the fix.</summary>
  public const string MARKER_MIGRATION = "361_RecursiveDocumentsStoredWhole.sql";

  /// <summary>The stable event id of the notice, for alerting and log queries.</summary>
  public const int EVENT_ID = 2112;

  /// <summary>The stable event name of the notice.</summary>
  public const string EVENT_NAME = "RecursivePerspectiveNeedsRebuild";

  /// <summary>
  /// Names each recursive perspective with rows when the ledger predates the fix, and returns the names it gave.
  /// </summary>
  /// <param name="connectionFactory">Opens a connection to the database being started.</param>
  /// <param name="schema">The schema the ledger and the tables live in.</param>
  /// <param name="recursive">The perspectives whose models are recursive, by CLR type name, with their tables.</param>
  /// <param name="logger">Where the notice goes; null still returns the names.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The perspectives named, in the order given.</returns>
  public static async Task<IReadOnlyList<string>> ReportAsync(
      Func<NpgsqlConnection> connectionFactory, string schema,
      IReadOnlyList<(string Perspective, string Table)> recursive, ILogger? logger, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(recursive);
    if (recursive.Count == 0) {
      return [];
    }

    await using var connection = connectionFactory();
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    var prefix = PgIdentifier.QualifyPrefix(schema);
    if (!await _ledgerPredatesTheFixAsync(connection, prefix, cancellationToken).ConfigureAwait(false)) {
      return [];
    }

    var named = new List<string>();
    var holdsRows = new Dictionary<string, bool>(StringComparer.Ordinal);
    foreach (var (perspective, table) in recursive) {
      if (!holdsRows.TryGetValue(table, out var rows)) {
        rows = await _holdsRowsAsync(connection, prefix, table, cancellationToken).ConfigureAwait(false);
        holdsRows.Add(table, rows);
      }
      if (rows) {
        named.Add(perspective);
        if (logger is not null) {
          LogNeedsRebuild(logger, perspective, table);
        }
      }
    }
    return named;
  }

  /// <summary>Whether a migration ledger exists and lacks the marker: an earlier release migrated it.</summary>
  private static async Task<bool> _ledgerPredatesTheFixAsync(
      NpgsqlConnection connection, string prefix, CancellationToken cancellationToken) {
    await using var probe = connection.CreateCommand();
    probe.CommandText = "SELECT to_regclass($1) IS NOT NULL";
    probe.Parameters.AddWithValue(prefix + "wh_schema_migrations");
    if (await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
      return false;
    }

    await using var marker = connection.CreateCommand();
#pragma warning disable S2077 // the prefix is quoted by PgIdentifier; the marker names are bound
    // Both spellings: the Entity Framework driver records a file with its extension, the Dapper driver without.
    marker.CommandText = $"SELECT NOT EXISTS (SELECT 1 FROM {prefix}wh_schema_migrations WHERE file_name IN ($1, $2))";
#pragma warning restore S2077
    marker.Parameters.AddWithValue(MARKER_MIGRATION);
    marker.Parameters.AddWithValue(Path.GetFileNameWithoutExtension(MARKER_MIGRATION));
    return await marker.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }

  /// <summary>Whether the table exists and holds at least one row.</summary>
  private static async Task<bool> _holdsRowsAsync(
      NpgsqlConnection connection, string prefix, string table, CancellationToken cancellationToken) {
    var qualified = prefix + PgIdentifier.Quote(table);
    await using var probe = connection.CreateCommand();
    probe.CommandText = "SELECT to_regclass($1) IS NOT NULL";
    probe.Parameters.AddWithValue(qualified);
    if (await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
      return false;
    }

    await using var rows = connection.CreateCommand();
#pragma warning disable S2077 // the table name is quoted by PgIdentifier
    rows.CommandText = $"SELECT EXISTS (SELECT 1 FROM {qualified})";
#pragma warning restore S2077
    return await rows.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }

  [LoggerMessage(EventId = EVENT_ID, EventName = EVENT_NAME, Level = LogLevel.Warning,
    Message = "Perspective '{Perspective}' (table {Table}) must be rebuilt from its events: its model is recursive, and "
      + "rows written before the #2112 fix may have kept only the first level of its recursive members. Nothing is "
      + "rebuilt automatically; until it is, those rows read back without the levels that were dropped")]
  private static partial void LogNeedsRebuild(ILogger logger, string perspective, string table);
}
