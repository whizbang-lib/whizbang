// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>What the rewrite found, table by table, for the settle step that follows it.</summary>
/// <docs>fundamentals/perspectives/stored-form-migrations#declared-names</docs>
public sealed class DeclaredKeySpellingResult {
  internal DeclaredKeySpellingResult(IReadOnlyList<(DeclaredKeyTable Table, long RowsChanged)> checkedTables) {
    Checked = checkedTables;
  }

  /// <summary>The tables the rewrite scanned, and how many rows it changed in each.</summary>
  internal IReadOnlyList<(DeclaredKeyTable Table, long RowsChanged)> Checked { get; }
}

/// <summary>
/// The startup step that brings every stored key of every perspective document to its declared spelling (#1874).
/// </summary>
/// <remarks>
/// <para>
/// A property is stored under its declared <c>[JsonPropertyName]</c>, and releases before that was honored stored it
/// under its C# name. Where the two differ only in case, Entity Framework's mapped reader, which matches names
/// exactly, finds no key and reads the member's default without an error. So each table is scanned for keys that
/// match a declared name only case-insensitively, and each such row is rewritten.
/// </para>
/// <para>
/// The scan walks the table in primary-key order in chunks of a bounded number of rows, so each statement touches a
/// bounded number of rows whatever the table's size. The predicate runs in the database; only the rows it finds come
/// back. Each update is conditioned on the row version it read, so a row written meanwhile is left for the next start.
/// A row that cannot be rewritten (both spellings with different values, or a key matching two declared names) is
/// left exactly as it was; every other row is still rewritten, and startup then stops naming each one.
/// </para>
/// <para>
/// The settle step runs after the rest of the stored-form rewrites, so a <c>[StoredForm(DefaultWhenMissing)]</c>
/// has filled what it fills, and records each table in <c>wh_stored_form_migrations</c> under
/// <see cref="DeclaredKeyTable.JournalName"/>. A table whose rewrite changed nothing is checked for a required member
/// still missing from stored rows, which is reported once at Error with the number of rows, and is then settled
/// whatever the check found (#2128): rows nothing at startup can repair are not a reason to scan the table again at
/// every start. A settled table is skipped with one lookup until what its readers declare changes. A table whose
/// rewrite changed rows is left open and checked on the next start, which catches rows an older instance wrote
/// during a rollout and reports what is missing then, once.
/// </para>
/// <para>
/// Both scans say what they are doing at Information on a steady cadence (<see cref="StartupProgress.Cadence"/>): a
/// line when the first table is scanned, a line each cadence with the table it is on and the rows read, and a line
/// when it finishes (#2128). A start on which every table is settled says nothing.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/stored-form-migrations#declared-names</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DeclaredNameSpellingIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DeclaredKeySpellingPassTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperDeclaredNameSpellingTests.cs</tests>
public static class DeclaredKeySpellingPass {
  /// <summary>Rows read per statement of the scan.</summary>
  public const int DEFAULT_BATCH_SIZE = 1000;

  private const int CONFLICT_SAMPLES = 10;
  private const int MISSING_SAMPLES = 3;

  /// <summary>Rewrites, then settles: what a driver with no stored-form rewrites of its own between the two runs.</summary>
  /// <param name="connectionFactory">Produces the connections the pass uses.</param>
  /// <param name="schema">The schema the tables live in, unquoted.</param>
  /// <param name="tables">The tables and what their readers declare.</param>
  /// <param name="commandTimeoutSeconds">The timeout of each statement.</param>
  /// <param name="logger">Where the pass reports what it changed.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="StoredFormConversionBlockedException">A row holds a key the pass cannot rewrite.</exception>
  public static Task ApplyAsync(
      Func<NpgsqlConnection> connectionFactory, string schema, IReadOnlyList<DeclaredKeyTable> tables,
      int commandTimeoutSeconds, ILogger? logger = null, CancellationToken cancellationToken = default) =>
    ApplyAsync(connectionFactory, schema, tables, commandTimeoutSeconds, TimeProvider.System, logger, cancellationToken);

  /// <summary>Rewrites, then settles, reporting progress on <paramref name="timeProvider"/>.</summary>
  /// <param name="connectionFactory">Produces the connections the pass uses.</param>
  /// <param name="schema">The schema the tables live in, unquoted.</param>
  /// <param name="tables">The tables and what their readers declare.</param>
  /// <param name="commandTimeoutSeconds">The timeout of each statement.</param>
  /// <param name="timeProvider">The clock the progress lines are paced on.</param>
  /// <param name="logger">Where the pass reports what it changed.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="StoredFormConversionBlockedException">A row holds a key the pass cannot rewrite.</exception>
  public static async Task ApplyAsync(
      Func<NpgsqlConnection> connectionFactory, string schema, IReadOnlyList<DeclaredKeyTable> tables,
      int commandTimeoutSeconds, TimeProvider timeProvider, ILogger? logger = null, CancellationToken cancellationToken = default) {
    var result = await RewriteAsync(connectionFactory, schema, tables, commandTimeoutSeconds, timeProvider, logger, cancellationToken)
      .ConfigureAwait(false);
    await SettleAsync(connectionFactory, schema, result, commandTimeoutSeconds, timeProvider, logger, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Rewrites every stored key in another case than its declared name, table by table, skipping a table that does not
  /// exist yet or that a pass has settled.
  /// </summary>
  /// <param name="connectionFactory">Produces the connections the pass uses.</param>
  /// <param name="schema">The schema the tables live in, unquoted.</param>
  /// <param name="tables">The tables and what their readers declare.</param>
  /// <param name="commandTimeoutSeconds">The timeout of each statement.</param>
  /// <param name="logger">Where the pass reports what it changed.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The tables scanned, for <see cref="SettleAsync"/>.</returns>
  /// <exception cref="StoredFormConversionBlockedException">A row holds a key the pass cannot rewrite.</exception>
  public static Task<DeclaredKeySpellingResult> RewriteAsync(
      Func<NpgsqlConnection> connectionFactory, string schema, IReadOnlyList<DeclaredKeyTable> tables,
      int commandTimeoutSeconds, ILogger? logger = null, CancellationToken cancellationToken = default) =>
    RewriteAsync(connectionFactory, schema, tables, commandTimeoutSeconds, TimeProvider.System, logger, cancellationToken);

  /// <summary>
  /// Rewrites every stored key in another case than its declared name, reporting progress on
  /// <paramref name="timeProvider"/>.
  /// </summary>
  /// <param name="connectionFactory">Produces the connections the pass uses.</param>
  /// <param name="schema">The schema the tables live in, unquoted.</param>
  /// <param name="tables">The tables and what their readers declare.</param>
  /// <param name="commandTimeoutSeconds">The timeout of each statement.</param>
  /// <param name="timeProvider">The clock the progress lines are paced on.</param>
  /// <param name="logger">Where the pass reports what it changed.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The tables scanned, for <see cref="SettleAsync(Func{NpgsqlConnection}, string, DeclaredKeySpellingResult, int, TimeProvider, ILogger?, CancellationToken)"/>.</returns>
  /// <exception cref="StoredFormConversionBlockedException">A row holds a key the pass cannot rewrite.</exception>
  public static Task<DeclaredKeySpellingResult> RewriteAsync(
      Func<NpgsqlConnection> connectionFactory, string schema, IReadOnlyList<DeclaredKeyTable> tables,
      int commandTimeoutSeconds, TimeProvider timeProvider, ILogger? logger = null, CancellationToken cancellationToken = default) =>
    RewriteAsync(
      DeclaredKeySpellingRun.Create(connectionFactory, schema, commandTimeoutSeconds, DEFAULT_BATCH_SIZE, timeProvider, logger),
      tables, cancellationToken);

  internal static Task<DeclaredKeySpellingResult> RewriteAsync(
      Func<NpgsqlConnection> connectionFactory, string schema, IReadOnlyList<DeclaredKeyTable> tables,
      int commandTimeoutSeconds, int batchSize, ILogger? logger, CancellationToken cancellationToken) =>
    RewriteAsync(
      DeclaredKeySpellingRun.Create(connectionFactory, schema, commandTimeoutSeconds, batchSize, TimeProvider.System, logger),
      tables, cancellationToken);

  internal static async Task<DeclaredKeySpellingResult> RewriteAsync(
      DeclaredKeySpellingRun run, IReadOnlyList<DeclaredKeyTable> tables, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(tables);
    var (schema, log) = (run.Schema, run.Log);

    var progress = new ScanProgress(SPELLING_SCAN, schema, tables.Count, run.TimeProvider, log);
    var checkedTables = new List<(DeclaredKeyTable, long)>();
    var conflicts = new List<string>();
    long rowsChanged = 0;
    for (var i = 0; i < tables.Count; i++) {
      var table = tables[i];
      try {
        var outcome = await _rewriteTableAsync(run, table, progress.Table(i + 1, table.Table), cancellationToken)
          .ConfigureAwait(false);
        if (outcome is { } scanned) {
          checkedTables.Add((table, scanned.RowsChanged));
          conflicts.AddRange(scanned.Conflicts);
          rowsChanged += scanned.RowsChanged;
        }
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        // Not fatal: the table stays as it is, every reader that tolerates the other case still reads it, and the
        // next start tries again. Said at Warning because a table that never gets checked is worth seeing.
        DeclaredKeySpellingLog.TableFailed(log, ex, $"{schema}.{table.Table}");
      }
    }
    if (progress.Started) {
      var elapsed = progress.Elapsed;
      DeclaredKeySpellingLog.ScanFinished(log, SPELLING_SCAN, schema, checkedTables.Count, progress.Rows, rowsChanged,
        new ProgressElapsed(elapsed));
    }

    if (conflicts.Count > 0) {
      throw new StoredFormConversionBlockedException(conflicts);
    }
    return new DeclaredKeySpellingResult(checkedTables);
  }

  /// <summary>
  /// Reports a required member still missing from the stored rows of each table the rewrite scanned, and records each
  /// table in the stored-form journal, settled when the rewrite changed nothing and nothing is missing.
  /// </summary>
  /// <param name="connectionFactory">Produces the connections the pass uses.</param>
  /// <param name="schema">The schema the tables live in, unquoted.</param>
  /// <param name="result">What <see cref="RewriteAsync(Func{NpgsqlConnection}, string, IReadOnlyList{DeclaredKeyTable}, int, ILogger?, CancellationToken)"/> found.</param>
  /// <param name="commandTimeoutSeconds">The timeout of each statement.</param>
  /// <param name="logger">Where the pass reports what is missing.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public static Task SettleAsync(
      Func<NpgsqlConnection> connectionFactory, string schema, DeclaredKeySpellingResult result,
      int commandTimeoutSeconds, ILogger? logger = null, CancellationToken cancellationToken = default) =>
    SettleAsync(connectionFactory, schema, result, commandTimeoutSeconds, TimeProvider.System, logger, cancellationToken);

  /// <summary>
  /// Reports a required member still missing from the stored rows of each table the rewrite scanned and changed
  /// nothing in, and records each table in the stored-form journal, reporting progress on
  /// <paramref name="timeProvider"/>.
  /// </summary>
  /// <param name="connectionFactory">Produces the connections the pass uses.</param>
  /// <param name="schema">The schema the tables live in, unquoted.</param>
  /// <param name="result">What the rewrite found.</param>
  /// <param name="commandTimeoutSeconds">The timeout of each statement.</param>
  /// <param name="timeProvider">The clock the progress lines are paced on.</param>
  /// <param name="logger">Where the pass reports what is missing.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public static Task SettleAsync(
      Func<NpgsqlConnection> connectionFactory, string schema, DeclaredKeySpellingResult result,
      int commandTimeoutSeconds, TimeProvider timeProvider, ILogger? logger = null, CancellationToken cancellationToken = default) =>
    SettleAsync(
      DeclaredKeySpellingRun.Create(connectionFactory, schema, commandTimeoutSeconds, DEFAULT_BATCH_SIZE, timeProvider, logger),
      result, cancellationToken);

  internal static Task SettleAsync(
      Func<NpgsqlConnection> connectionFactory, string schema, DeclaredKeySpellingResult result,
      int commandTimeoutSeconds, int batchSize, ILogger? logger, CancellationToken cancellationToken) =>
    SettleAsync(
      DeclaredKeySpellingRun.Create(connectionFactory, schema, commandTimeoutSeconds, batchSize, TimeProvider.System, logger),
      result, cancellationToken);

  internal static async Task SettleAsync(
      DeclaredKeySpellingRun run, DeclaredKeySpellingResult result, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(result);
    var (schema, log) = (run.Schema, run.Log);

    var progress = new ScanProgress(REQUIRED_CHECK, schema, result.Checked.Count, run.TimeProvider, log);
    var checkedTables = 0;
    long rowsMissing = 0;
    for (var i = 0; i < result.Checked.Count; i++) {
      var (table, rowsChanged) = result.Checked[i];
      try {
        await using var connection = run.ConnectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var scan = new TableScan(connection, run, table);
        if (rowsChanged > 0) {
          // Rewritten this start: left open, so the next start scans it once more (rows an older instance wrote during
          // a rollout) and checks what is missing then, once.
          await scan.RecordAsync(rowsChanged, settled: false, cancellationToken).ConfigureAwait(false);
          continue;
        }
        var (rowsLacking, details) = await scan.CountMissingRequiredAsync(progress.Table(i + 1, table.Table), cancellationToken)
          .ConfigureAwait(false);
        checkedTables++;
        if (rowsLacking > 0) {
          rowsMissing += rowsLacking;
          DeclaredKeySpellingLog.RequiredMissing(log, rowsLacking, scan.DisplayTable, string.Join("; ", details));
        }
        // Settled whatever the check found: the rows it reported cannot be repaired here, and are not reported again.
        await scan.RecordAsync(0, settled: true, cancellationToken).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        DeclaredKeySpellingLog.TableFailed(log, ex, $"{schema}.{table.Table}");
      }
    }
    if (progress.Started) {
      var elapsed = progress.Elapsed;
      DeclaredKeySpellingLog.CheckFinished(log, REQUIRED_CHECK, schema, checkedTables, progress.Rows, rowsMissing,
        new ProgressElapsed(elapsed));
    }
  }

  private const string SPELLING_SCAN = "The declared-key spelling scan";
  private const string REQUIRED_CHECK = "The required-member check";

  /// <summary>One scan's progress: a line when it starts, a line each cadence, and its totals for the last line.</summary>
  private sealed class ScanProgress(string phase, string schema, int tables, TimeProvider time, ILogger log) {
    private ProgressClock? _clock;
    private int _position;
    private string _table = string.Empty;
    private long _tableRows;

    public bool Started => _clock is not null;
    public long Rows { get; private set; }
    public TimeSpan Elapsed => _clock!.Elapsed;

    /// <summary>Starts table <paramref name="position"/>; returns what its chunks report their rows to.</summary>
    public Action<int> Table(int position, string table) {
      _position = position;
      _table = $"{schema}.{table}";
      _tableRows = 0;
      return _read;
    }

    private void _read(int rows) {
      if (_clock is null) {
        _clock = new ProgressClock(time);
        DeclaredKeySpellingLog.ScanStarted(log, phase, schema, tables);
      }
      _tableRows += rows;
      Rows += rows;
      if (_clock.Due()) {
        var elapsed = _clock.Elapsed;
        DeclaredKeySpellingLog.ScanProgressed(log, phase, schema, new ScanPosition(_position, tables, _table), _tableRows, Rows,
          new ProgressElapsed(elapsed));
      }
    }
  }

  private sealed record TableOutcome(long RowsChanged, IReadOnlyList<string> Conflicts);

  private static async Task<TableOutcome?> _rewriteTableAsync(
      DeclaredKeySpellingRun run, DeclaredKeyTable table, Action<int> read, CancellationToken cancellationToken) {
    var log = run.Log;
    await using var connection = run.ConnectionFactory();
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    var scan = new TableScan(connection, run, table);

    var state = await scan.StateAsync(cancellationToken).ConfigureAwait(false);
    if (state == TableState.Absent) {
      DeclaredKeySpellingLog.Absent(log, scan.DisplayTable);
      return null;
    }
    if (state == TableState.Settled) {
      DeclaredKeySpellingLog.Settled(log, scan.DisplayTable);
      return null;
    }

    var outcome = await scan.RespellAsync(read, cancellationToken).ConfigureAwait(false);
    if (outcome.RowsChanged > 0) {
      DeclaredKeySpellingLog.Rewrote(log, scan.DisplayTable, outcome.RowsChanged, outcome.Summary);
    }
    return new TableOutcome(outcome.RowsChanged, outcome.Conflicts);
  }

  private enum TableState { Absent, Settled, Open }

  /// <summary>One table's statements, on one open connection.</summary>
  private sealed class TableScan(NpgsqlConnection connection, DeclaredKeySpellingRun run, DeclaredKeyTable table) {
    private readonly string _qualified = $"{SqlText.Identifier(run.Schema)}.{SqlText.Identifier(table.Table)}";
    private readonly string _journal = $"{SqlText.Identifier(run.Schema)}.{StoredFormMigrationSql.JOURNAL}";

    public string DisplayTable { get; } = $"{run.Schema}.{table.Table}";

    public async Task<TableState> StateAsync(CancellationToken cancellationToken) {
      await using var exists = _command("SELECT to_regclass($1) IS NOT NULL, to_regclass($2) IS NOT NULL");
      exists.Parameters.AddWithValue(_qualified);
      exists.Parameters.AddWithValue(_journal);
      bool hasTable;
      bool hasJournal;
      await using (var reader = await exists.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        hasTable = reader.GetBoolean(0);
        hasJournal = reader.GetBoolean(1);
      }
      if (!hasTable) {
        return TableState.Absent;
      }
      if (!hasJournal) {
        return TableState.Open;
      }
      await using var settled = _command($"SELECT EXISTS (SELECT 1 FROM {_journal} WHERE name = $1 AND settled_at IS NOT NULL)");
      settled.Parameters.AddWithValue(table.JournalName);
      return await settled.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true ? TableState.Settled : TableState.Open;
    }

    public async Task<ScanOutcome> RespellAsync(Action<int> read, CancellationToken cancellationToken) {
      var columns = table.Columns;
      var hit = string.Join("\n    OR ", columns.Select(c => $"({DeclaredKeySpellingSql.CandidatePredicate(c.Column, c.Shape)})"));
      var selected = string.Concat(columns.Select(c => $", {SqlText.Identifier(c.Column)}"));
      var documents = string.Concat(columns.Select(c => $", CASE WHEN wh_hit THEN {SqlText.Identifier(c.Column)}::text END"));
      // Only the rows the predicate finds bring their documents back; every row brings its id, which is where the
      // next chunk starts.
      var select =
        $"SELECT id, wh_hit, CASE WHEN wh_hit THEN wh_version::text END{documents} FROM (\n"
        + $"  SELECT id, xmin AS wh_version{selected}, coalesce({hit}, false) AS wh_hit FROM {_qualified}\n"
        + "  WHERE ($1::uuid IS NULL OR id > $1) ORDER BY id LIMIT $2) wh_chunk";

      var outcome = new ScanOutcome();
      Guid? after = null;
      while (true) {
        var chunk = await _readChunkAsync(select, after, columns.Count, cancellationToken).ConfigureAwait(false);
        if (chunk.Count == 0) {
          return outcome;
        }
        read(chunk.Count);
        after = chunk[^1].Id;
        var updates = _rewriteChunk(chunk, outcome);
        if (updates.Count > 0) {
          outcome.RowsChanged += await _updateAsync(updates, cancellationToken).ConfigureAwait(false);
        }
      }
    }

    public async Task<(long Rows, IReadOnlyList<string> Details)> CountMissingRequiredAsync(
        Action<int> read, CancellationToken cancellationToken) {
      var checks = table.Columns
        .SelectMany(c => DeclaredKeySpellingSql.MissingRequired(c.Column, c.Shape).Select(m => (Label: $"{c.Column} {m.Path}", m.Predicate)))
        .ToList();
      if (checks.Count == 0) {
        return (0, []);
      }

      // A column holding SQL NULL has no document, so nothing is missing from it.
      var select = $"SELECT id{string.Concat(checks.Select(c => $", coalesce({c.Predicate}, false)"))} FROM {_qualified} "
        + "WHERE ($1::uuid IS NULL OR id > $1) ORDER BY id LIMIT $2";
      var tally = new MissingTally(checks.Count);
      Guid? after = null;
      int chunk;
      do {
        await using var command = _command(select);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)after ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.AddWithValue(run.BatchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        chunk = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          chunk++;
          after = reader.GetGuid(0);
          tally.Add(reader, after.Value);
        }
        if (chunk > 0) {
          read(chunk);
        }
      } while (chunk > 0);

      var (counts, samples) = (tally.Counts, tally.Samples);
      return (tally.RowsMissing, [.. checks.Select((c, i) => (c.Label, Count: counts[i], Sample: samples[i]))
        .Where(c => c.Count > 0)
        .Select(c => string.Create(CultureInfo.InvariantCulture,
          $"{c.Label} in {c.Count} row(s) (for example {string.Join(", ", c.Sample)})"))]);
    }

    public async Task RecordAsync(long rowsChanged, bool settled, CancellationToken cancellationToken) {
      await using var exists = _command("SELECT to_regclass($1) IS NOT NULL");
      exists.Parameters.AddWithValue(_journal);
      if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
        return;
      }
      await using var record = _command(
        $"INSERT INTO {_journal} AS wh_j (name, table_name, kind, first_applied_at, last_applied_at, rows_converted, settled_at)\n"
        + "VALUES ($1, $2, 'generated', now(), now(), $3, CASE WHEN $4 THEN now() END)\n"
        + "ON CONFLICT (name) DO UPDATE SET first_applied_at = coalesce(wh_j.first_applied_at, EXCLUDED.first_applied_at),\n"
        + "  last_applied_at = EXCLUDED.last_applied_at, rows_converted = wh_j.rows_converted + EXCLUDED.rows_converted,\n"
        + "  settled_at = EXCLUDED.settled_at");
      record.Parameters.AddWithValue(table.JournalName);
      record.Parameters.AddWithValue(table.Table);
      record.Parameters.AddWithValue(rowsChanged);
      record.Parameters.AddWithValue(settled);
      await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      if (rowsChanged > 0 && StoredFormApplier.Current is { } applier) {
        // #2131: keys moved are a conversion, and the journal records who applied it (nothing on a journal older than 376).
        await using var columns = _command(
          "SELECT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($1) AND attname = 'applied_library_version' AND NOT attisdropped)");
        columns.Parameters.AddWithValue(_journal);
        if (await columns.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true) {
          await using var stamp = _command(
            $"UPDATE {_journal} SET applied_library_version = $2, applied_application_version = $3 WHERE name = $1");
          stamp.Parameters.AddWithValue(table.JournalName);
          stamp.Parameters.AddWithValue(applier.LibraryVersion);
          stamp.Parameters.AddWithValue(applier.ApplicationVersion);
          await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
      }
    }

    private async Task<List<ChunkRow>> _readChunkAsync(string select, Guid? after, int columnCount, CancellationToken cancellationToken) {
      await using var command = _command(select);
      command.Parameters.Add(new NpgsqlParameter { Value = (object?)after ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
      command.Parameters.AddWithValue(run.BatchSize);
      await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      var rows = new List<ChunkRow>();
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var hit = reader.GetBoolean(1);
        var documents = new string?[columnCount];
        for (var i = 0; hit && i < columnCount; i++) {
          documents[i] = reader.IsDBNull(i + 3) ? null : reader.GetString(i + 3);
        }
        rows.Add(new ChunkRow(reader.GetGuid(0), hit ? reader.GetString(2) : null, documents));
      }
      return rows;
    }

    // Each row the predicate found, rewritten column by column; a row with any conflict is left whole.
    private List<RowUpdate> _rewriteChunk(List<ChunkRow> chunk, ScanOutcome outcome) {
      var updates = new List<RowUpdate>();
      foreach (var row in chunk.Where(r => r.Version is not null)) {
        var rewritten = new string?[table.Columns.Count];
        var renames = new List<(string Column, DeclaredKeyRename Rename)>();
        var conflicted = false;
        for (var i = 0; i < table.Columns.Count; i++) {
          if (row.Documents[i] is not { } stored) {
            continue;
          }
          var column = table.Columns[i];
          var result = DeclaredKeySpellingDocument.Rewrite(stored, column.Shape);
          foreach (var conflict in result.Conflicts) {
            conflicted = true;
            outcome.AddConflict($"{DisplayTable}.{column.Column}", row.Id, conflict);
          }
          rewritten[i] = result.Json;
          renames.AddRange(result.Renames.Select(r => (column.Column, r)));
        }
        if (!conflicted && rewritten.Any(d => d is not null)) {
          updates.Add(new RowUpdate(row.Id, row.Version!, rewritten));
          outcome.Count(renames);
        }
      }
      return updates;
    }

    private async Task<long> _updateAsync(List<RowUpdate> updates, CancellationToken cancellationToken) {
      var assignments = string.Join(", ", table.Columns.Select((c, i) =>
        $"{SqlText.Identifier(c.Column)} = coalesce(v.c{i}::jsonb, t.{SqlText.Identifier(c.Column)})"));
      var arrays = string.Concat(table.Columns.Select((_, i) => $", ${i + 3}::text[]"));
      var names = string.Concat(table.Columns.Select((_, i) => $", c{i}"));
      await using var command = _command(
        $"UPDATE {_qualified} AS t SET {assignments}\n"
        + $"FROM unnest($1::uuid[], $2::text[]{arrays}) AS v(id, version{names})\n"
        + "WHERE t.id = v.id AND t.xmin = v.version::xid");
      command.Parameters.AddWithValue(updates.Select(u => u.Id).ToArray());
      command.Parameters.AddWithValue(updates.Select(u => u.Version).ToArray());
      for (var i = 0; i < table.Columns.Count; i++) {
        var column = i;
        command.Parameters.Add(new NpgsqlParameter {
          Value = updates.Select(u => (object?)u.Documents[column] ?? DBNull.Value).ToArray(),
          NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        });
      }
      return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private NpgsqlCommand _command(string sql) =>
      new(sql, connection) { CommandTimeout = run.CommandTimeoutSeconds };
  }

  /// <summary>The required-member check's counts: rows lacking a member, per check, with a few of their ids.</summary>
  private sealed class MissingTally(int checks) {
    public long[] Counts { get; } = new long[checks];
    public List<Guid>[] Samples { get; } = [.. Enumerable.Range(0, checks).Select(_ => new List<Guid>())];
    public long RowsMissing { get; private set; }

    /// <summary>Counts one row: column <c>i + 1</c> of <paramref name="reader"/> says whether check <c>i</c> found it lacking.</summary>
    public void Add(NpgsqlDataReader reader, Guid id) {
      var lacking = false;
      for (var i = 0; i < Counts.Length; i++) {
        if (reader.GetBoolean(i + 1)) {
          Counts[i]++;
          lacking = true;
          _sample(Samples[i], id);
        }
      }
      RowsMissing += lacking ? 1 : 0;
    }

    private static void _sample(List<Guid> sample, Guid id) {
      if (sample.Count < MISSING_SAMPLES) {
        sample.Add(id);
      }
    }
  }

  private sealed record ChunkRow(Guid Id, string? Version, string?[] Documents);

  private sealed record RowUpdate(Guid Id, string Version, string?[] Documents);

  /// <summary>What one table's scan changed and could not change.</summary>
  private sealed class ScanOutcome {
    private readonly Dictionary<(string Column, string Path, string From, string To), long> _renamed = [];
    private readonly List<string> _conflicts = [];
    private int _conflictCount;

    public long RowsChanged { get; set; }

    public IReadOnlyList<string> Conflicts => _conflictCount == 0 ? [] : _describeConflicts();

    public string Summary => string.Join("; ", _renamed
      .OrderBy(r => r.Key.Column, StringComparer.Ordinal).ThenBy(r => r.Key.Path, StringComparer.Ordinal)
      .ThenBy(r => r.Key.From, StringComparer.Ordinal)
      .Select(r => string.Create(CultureInfo.InvariantCulture,
        $"{r.Key.Column} {r.Key.Path}: {r.Key.From} -> {r.Key.To} in {r.Value} row(s)")));

    /// <summary>Counts each distinct rename once per row.</summary>
    public void Count(IEnumerable<(string Column, DeclaredKeyRename Rename)> renames) {
      foreach (var (column, rename) in renames.Distinct()) {
        var key = (column, rename.Path, rename.From, rename.To);
        _renamed[key] = _renamed.GetValueOrDefault(key) + 1;
      }
    }

    public void AddConflict(string where, Guid id, DeclaredKeyConflict conflict) {
      _conflictCount++;
      if (_conflicts.Count >= CONFLICT_SAMPLES) {
        return;
      }
      _conflicts.Add(conflict.Ambiguous
        ? $"{where} row {id}: {conflict.Path} holds \"{conflict.Stored}\", which matches more than one declared name ({conflict.Declared})"
        : $"{where} row {id}: {conflict.Path} holds both \"{conflict.Stored}\" and \"{conflict.Declared}\" with different values; the declared spelling is \"{conflict.Declared}\"");
    }

    private List<string> _describeConflicts() {
      var sb = new StringBuilder();
      sb.Append(CultureInfo.InvariantCulture,
        $"{_conflictCount} stored key(s) cannot be rewritten to their declared spelling, so those rows were left as they were: ");
      sb.AppendJoin("; ", _conflicts);
      if (_conflictCount > _conflicts.Count) {
        sb.Append(CultureInfo.InvariantCulture, $"; and {_conflictCount - _conflicts.Count} more");
      }
      sb.Append(". Keep the value under the declared name and remove the other key in each, then restart.");
      return [sb.ToString()];
    }
  }
}

/// <summary>Source-generated logging for the declared-spelling pass.</summary>
internal static partial class DeclaredKeySpellingLog {
  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Rewrote stored keys to their declared spelling in {Table}: {Rows} row(s) updated; {Changes}")]
  public static partial void Rewrote(ILogger logger, string table, long rows, string changes);

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "{Table}: settled, skipped; every stored key is in its declared spelling")]
  public static partial void Settled(ILogger logger, string table);

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "{Table}: table absent, no stored keys to bring to their declared spelling")]
  public static partial void Absent(ILogger logger, string table);

  [LoggerMessage(Level = LogLevel.Error,
    Message = "{Rows} stored document(s) in {Table} lack a required member: {Missing}. A reader that refuses a missing "
      + "required member fails these rows, and Entity Framework's mapped reader reads the member's default. Fill them "
      + "with [StoredForm(DefaultWhenMissing = ...)] or rebuild the perspective. The table is recorded as checked, so "
      + "this is said once: it is checked again when what its readers declare changes")]
  public static partial void RequiredMissing(ILogger logger, long rows, string table, string missing);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "{Phase} of {Schema} started: {Tables} perspective table(s) to look at")]
  public static partial void ScanStarted(ILogger logger, string phase, string schema, int tables);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "{Phase} of {Schema}: {Position}, {TableRows} row(s) read in it, {Rows} in all, {Elapsed} elapsed")]
  public static partial void ScanProgressed(
    ILogger logger, string phase, string schema, ScanPosition position, long tableRows, long rows, ProgressElapsed elapsed);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "{Phase} of {Schema} finished: {Scanned} table(s) scanned, {Rows} row(s) read, {Changed} row(s) "
      + "rewritten, in {Elapsed}")]
  public static partial void ScanFinished(ILogger logger, string phase, string schema, int scanned, long rows, long changed, ProgressElapsed elapsed);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "{Phase} of {Schema} finished: {CheckedTables} table(s) checked, {Rows} row(s) read, {Missing} row(s) "
      + "lacking a required member, in {Elapsed}")]
  public static partial void CheckFinished(ILogger logger, string phase, string schema, int checkedTables, long rows, long missing, ProgressElapsed elapsed);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Could not bring the stored keys of {Table} to their declared spelling; the next start tries again")]
  public static partial void TableFailed(ILogger logger, Exception exception, string table);
}

/// <summary>Where a scan is, as its progress line says it: <c>table 2 of 5 (schema.table)</c>, formatted only when written.</summary>
internal readonly struct ScanPosition(int position, int tables, string table) {
  /// <inheritdoc/>
  public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"table {position} of {tables} ({table})");
}

/// <summary>What every statement of one declared-key spelling pass runs with.</summary>
/// <param name="ConnectionFactory">Produces the connections the pass uses.</param>
/// <param name="Schema">The schema the tables live in, unquoted.</param>
/// <param name="CommandTimeoutSeconds">The timeout of each statement.</param>
/// <param name="BatchSize">Rows read per statement of a scan.</param>
/// <param name="TimeProvider">The clock the progress lines are paced on.</param>
/// <param name="Log">Where the pass reports what it did.</param>
internal sealed record DeclaredKeySpellingRun(
    Func<NpgsqlConnection> ConnectionFactory, string Schema, int CommandTimeoutSeconds, int BatchSize,
    TimeProvider TimeProvider, ILogger Log) {
  /// <summary>Checks the arguments and builds the run; no logger reports to nowhere.</summary>
  public static DeclaredKeySpellingRun Create(
      Func<NpgsqlConnection> connectionFactory, string schema, int commandTimeoutSeconds, int batchSize,
      TimeProvider timeProvider, ILogger? logger) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentException.ThrowIfNullOrWhiteSpace(schema);
    ArgumentNullException.ThrowIfNull(timeProvider);
    return new(connectionFactory, schema, commandTimeoutSeconds, batchSize, timeProvider, logger ?? NullLogger.Instance);
  }
}
