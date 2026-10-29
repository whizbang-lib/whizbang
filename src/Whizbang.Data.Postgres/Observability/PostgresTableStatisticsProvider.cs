// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres.Observability;

/// <summary>
/// PostgreSQL implementation of <see cref="ITableStatisticsProvider"/>, shared by both data drivers.
/// </summary>
/// <remarks>
/// <para>
/// Every table in the schema whose name starts with <c>wh_</c> is reported: the framework's own tables
/// and every perspective table, found in the catalog on each read rather than listed, so a perspective
/// added in a release is reported from its first start. A partitioned table is reported once, under its
/// own name, with its partitions summed.
/// </para>
/// <para>
/// Everything about the tables comes from one catalog statement per read: sizes, the split into heap,
/// TOAST and indexes, live rows and the bloat ratio. Nothing reads a table's rows, so the cost depends on
/// how many tables and indexes the schema has, not on how much they hold.
/// </para>
/// </remarks>
/// <docs>operations/observability/metrics#table-statistics</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresTableStatisticsProviderTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperTableStatisticsTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Observability/StatementStatisticsCapabilityTests.cs</tests>
public sealed partial class PostgresTableStatisticsProvider : ITableStatisticsProvider {
  /// <summary>
  /// The size, split, rows and bloat of every <c>wh_</c> table in the schema, with the tables past the
  /// cap summed under <see cref="TableStatisticsOptions.OTHER_TABLES"/>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>tracked</c> is every ordinary or partitioned table named <c>wh_</c> (the underscore escaped, so
  /// it is not a wildcard); a partition is left out because its parent carries it. <c>leaves</c> is
  /// what holds each table's storage: the table itself, or a partitioned table's leaf partitions.
  /// </para>
  /// <para>
  /// TOAST is measured with its own index; heap is the table's size without TOAST, so heap, TOAST and
  /// indexes add up exactly to <c>pg_total_relation_size</c>. Every size function is wrapped in
  /// <c>COALESCE</c> because each answers null for a relation dropped while the statement runs, which
  /// a perspective table being retired can be.
  /// </para>
  /// <para>
  /// Live rows are the planner's count (<c>reltuples</c>), which <c>ANALYZE</c> and <c>VACUUM</c>
  /// refresh. A table never analyzed has -1 there, meaning unknown, so the server's running count of
  /// live rows answers for it instead.
  /// </para>
  /// <para>
  /// The bloat ratio is computed set-based, one pass over the statistics views for all tables. A
  /// per-table lateral read of <c>pg_stat_user_tables</c> evaluated that view once per table, which
  /// cost three times as much at 500 tables.
  /// </para>
  /// </remarks>
  private const string STORAGE_SQL = """
    WITH tracked AS (
      SELECT c.oid, c.relname, c.relkind
      FROM pg_catalog.pg_class c
      JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
      WHERE n.nspname = COALESCE(@schema, current_schema())
        AND c.relkind IN ('r', 'p')
        AND NOT c.relispartition
        AND c.relname LIKE 'wh\_%'
    ),
    leaves AS (
      SELECT t.oid AS table_oid, t.relname, t.oid AS leaf
      FROM tracked t
      WHERE t.relkind = 'r'
      UNION ALL
      SELECT t.oid, t.relname, p.relid
      FROM tracked t
      CROSS JOIN LATERAL pg_catalog.pg_partition_tree(t.oid) p
      WHERE t.relkind = 'p' AND p.isleaf
    ),
    measured AS (
      SELECT l.table_oid, l.relname,
             COALESCE(pg_catalog.pg_table_size(l.leaf), 0) AS table_bytes,
             COALESCE(pg_catalog.pg_total_relation_size(NULLIF(c.reltoastrelid, 0)), 0) AS toast_bytes,
             COALESCE(pg_catalog.pg_indexes_size(l.leaf), 0) AS index_bytes,
             CASE WHEN c.reltuples >= 0 THEN c.reltuples::bigint ELSE COALESCE(s.n_live_tup, 0) END AS live_rows
      FROM leaves l
      JOIN pg_catalog.pg_class c ON c.oid = l.leaf
      LEFT JOIN pg_catalog.pg_stat_user_tables s ON s.relid = l.leaf
    ),
    per_table AS (
      SELECT table_oid, relname,
             sum(table_bytes - toast_bytes)::bigint AS heap_bytes,
             sum(toast_bytes)::bigint AS toast_bytes,
             sum(index_bytes)::bigint AS index_bytes,
             sum(live_rows)::bigint AS live_rows
      FROM measured
      GROUP BY table_oid, relname
    ),
    widths AS (
      SELECT ps.tablename, sum(ps.avg_width) + 28 AS expected
      FROM pg_catalog.pg_stats ps
      WHERE ps.schemaname = COALESCE(@schema, current_schema())
        AND ps.tablename LIKE 'wh\_%'
        AND NOT ps.inherited
      GROUP BY ps.tablename
    ),
    bloat AS (
      SELECT t.oid AS table_oid,
             (pg_catalog.pg_relation_size(t.oid)::numeric / s.n_live_tup)
             / GREATEST(COALESCE(w.expected, 28), 1) AS ratio
      FROM tracked t
      JOIN pg_catalog.pg_stat_user_tables s ON s.relid = t.oid
      LEFT JOIN widths w ON w.tablename = t.relname
      WHERE t.relkind = 'r' AND s.n_live_tup > @bloat_floor
    ),
    ranked AS (
      SELECT p.*,
             row_number() OVER (ORDER BY p.heap_bytes + p.toast_bytes + p.index_bytes DESC, p.relname) AS size_rank
      FROM per_table p
    )
    SELECT CASE WHEN r.size_rank <= @cap THEN r.relname ELSE @other END AS table_name,
           sum(r.heap_bytes)::bigint AS heap_bytes,
           sum(r.toast_bytes)::bigint AS toast_bytes,
           sum(r.index_bytes)::bigint AS index_bytes,
           sum(r.live_rows)::bigint AS live_rows,
           max(b.ratio)::float8 AS bloat_ratio
    FROM ranked r
    LEFT JOIN bloat b ON b.table_oid = r.table_oid AND r.size_rank <= @cap
    GROUP BY 1
    """;

  /// <summary>
  /// The indexes on the schema's <c>wh_</c> tables with no scans since the statistics were last reset, with their
  /// size, the largest past the cap summed under <see cref="TableStatisticsOptions.OTHER_TABLES"/>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>pg_stat_user_indexes</c> lists the indexes that hold storage, which for a partitioned table are its partitions'
  /// indexes. Each is rolled up to the index it was created from (<c>pg_partition_root</c>, null for an index not in a
  /// partition tree), so an index on a partitioned table is one row whose scans and bytes are its partitions' summed.
  /// </para>
  /// <para>
  /// A unique, primary-key or exclusion index enforces a rule whether or not a query reads it, so it is left out. The
  /// table filter is on the table the rolled-up index belongs to, with the underscore escaped.
  /// </para>
  /// </remarks>
  private const string UNUSED_INDEXES_SQL = """
    WITH leaves AS (
      SELECT COALESCE(pg_catalog.pg_partition_root(s.indexrelid), s.indexrelid) AS index_oid,
             COALESCE(s.idx_scan, 0) AS scans,
             COALESCE(pg_catalog.pg_relation_size(s.indexrelid), 0) AS bytes
      FROM pg_catalog.pg_stat_user_indexes s
      WHERE s.schemaname = COALESCE(@schema, current_schema())
    ),
    per_index AS (
      SELECT index_oid, sum(scans) AS scans, sum(bytes)::bigint AS bytes
      FROM leaves
      GROUP BY index_oid
    ),
    unused AS (
      SELECT ic.relname AS index_name, tc.relname AS table_name, p.bytes,
             row_number() OVER (ORDER BY p.bytes DESC, ic.relname) AS size_rank
      FROM per_index p
      JOIN pg_catalog.pg_index x ON x.indexrelid = p.index_oid
      JOIN pg_catalog.pg_class ic ON ic.oid = p.index_oid
      JOIN pg_catalog.pg_class tc ON tc.oid = x.indrelid
      WHERE p.scans = 0
        AND NOT x.indisunique
        AND NOT x.indisexclusion
        AND tc.relname LIKE 'wh\_%'
    )
    SELECT CASE WHEN size_rank <= @cap THEN index_name ELSE @other END AS index_name,
           CASE WHEN size_rank <= @cap THEN table_name ELSE @other END AS table_name,
           sum(bytes)::bigint AS bytes
    FROM unused
    GROUP BY 1, 2
    """;

  /// <summary>
  /// Tables with no more live rows than this report no bloat ratio: with few rows the per-row average
  /// is dominated by page overhead and reads as alarming for a table measured in kilobytes.
  /// </summary>
  private const int BLOAT_FLOOR_ROWS = 1000;

  /// <summary>The extension statement statistics are read from, as the capability registry identifies it.</summary>
  private static readonly CapabilityId _statementStatistics = new(PostgresExtensionCatalog.PROVIDER, "pg_stat_statements");

  private readonly Func<CancellationToken, ValueTask<NpgsqlConnection>> _openConnectionAsync;
  private readonly string? _schema;
  private readonly int _maxTrackedTables;
  private readonly ICapabilityRegistry? _capabilities;
  private readonly string _capabilityScope = string.Empty;

  /// <summary>Reads the statistics of the schema's tables through connections the caller opens.</summary>
  /// <param name="openConnectionAsync">Opens a connection the provider disposes.</param>
  /// <param name="schema">
  /// The schema, unquoted. Null reads the connection's current schema, which is where a driver that
  /// resolves its tables through the search path keeps them.
  /// </param>
  /// <param name="maxTrackedTables">
  /// The most tables reported by name, at least 1; see <see cref="TableStatisticsOptions.MaxTrackedTables"/>.
  /// </param>
  /// <param name="capabilities">
  /// The capability registry the readings that need an extension consult (#1754): statement statistics are read only
  /// while <c>pg_stat_statements</c> is Ready in <paramref name="capabilityScope"/>. Null reads none of them.
  /// </param>
  /// <param name="capabilityScope">
  /// The scope the database is probed under (the driver's name for it); required with <paramref name="capabilities"/>.
  /// </param>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxTrackedTables"/> is less than 1.</exception>
  /// <exception cref="ArgumentException">
  /// <paramref name="capabilities"/> is given without a <paramref name="capabilityScope"/>.
  /// </exception>
  public PostgresTableStatisticsProvider(
      Func<CancellationToken, ValueTask<NpgsqlConnection>> openConnectionAsync,
      string? schema = "public",
      int maxTrackedTables = TableStatisticsOptions.DEFAULT_MAX_TRACKED_TABLES,
      ICapabilityRegistry? capabilities = null,
      string? capabilityScope = null) {
    ArgumentNullException.ThrowIfNull(openConnectionAsync);
    ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedTables, 1);
    _openConnectionAsync = openConnectionAsync;
    _schema = schema;
    _maxTrackedTables = maxTrackedTables;
    if (capabilities is not null) {
      ArgumentException.ThrowIfNullOrWhiteSpace(capabilityScope);
      _capabilities = capabilities;
      _capabilityScope = capabilityScope;
    }
  }

  /// <summary>Reads the statistics of the schema's tables through a data source.</summary>
  /// <param name="dataSource">The data source connections are opened from.</param>
  /// <param name="schema">The schema, unquoted; null reads the connection's current schema.</param>
  /// <param name="maxTrackedTables">
  /// The most tables reported by name, at least 1; see <see cref="TableStatisticsOptions.MaxTrackedTables"/>.
  /// </param>
  /// <param name="capabilities">The capability registry the readings that need an extension consult; null reads none of them.</param>
  /// <param name="capabilityScope">The scope the database is probed under; required with <paramref name="capabilities"/>.</param>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxTrackedTables"/> is less than 1.</exception>
  /// <exception cref="ArgumentException">
  /// <paramref name="capabilities"/> is given without a <paramref name="capabilityScope"/>.
  /// </exception>
  public PostgresTableStatisticsProvider(
      NpgsqlDataSource dataSource,
      string? schema = "public",
      int maxTrackedTables = TableStatisticsOptions.DEFAULT_MAX_TRACKED_TABLES,
      ICapabilityRegistry? capabilities = null,
      string? capabilityScope = null)
    : this(dataSource.OpenConnectionAsync, schema, maxTrackedTables, capabilities, capabilityScope) {
  }

  /// <inheritdoc />
  /// <remarks><c>current_database()</c> on a connection the provider opens: the database every other reading reads.</remarks>
  public async Task<string?> GetDatabaseNameAsync(CancellationToken ct = default) {
    await using var connection = await _openConnectionAsync(ct).ConfigureAwait(false);
    await using var cmd = new NpgsqlCommand("SELECT current_database()", connection);
    return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyDictionary<string, TableStorageStatistics>> GetTableStorageAsync(CancellationToken ct = default) {
    var results = new Dictionary<string, TableStorageStatistics>(StringComparer.Ordinal);

    await using var connection = await _openConnectionAsync(ct).ConfigureAwait(false);
    await using var cmd = new NpgsqlCommand(STORAGE_SQL, connection);
    _addSchema(cmd);
    cmd.Parameters.AddWithValue("cap", _maxTrackedTables);
    cmd.Parameters.AddWithValue("other", TableStatisticsOptions.OTHER_TABLES);
    cmd.Parameters.AddWithValue("bloat_floor", BLOAT_FLOOR_ROWS);

    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      results[reader.GetString(0)] = new TableStorageStatistics(
        HeapBytes: reader.GetInt64(1),
        ToastBytes: reader.GetInt64(2),
        IndexBytes: reader.GetInt64(3),
        LiveRows: reader.GetInt64(4),
        BloatRatio: reader.IsDBNull(5) ? null : reader.GetDouble(5));
    }

    return results;
  }

  /// <inheritdoc />
  /// <remarks>One catalog statement over the index statistics; nothing reads an index or a table.</remarks>
  public async Task<IReadOnlyDictionary<string, UnusedIndex>> GetUnusedIndexesAsync(CancellationToken ct = default) {
    var results = new Dictionary<string, UnusedIndex>(StringComparer.Ordinal);

    await using var connection = await _openConnectionAsync(ct).ConfigureAwait(false);
    await using var cmd = new NpgsqlCommand(UNUSED_INDEXES_SQL, connection);
    _addSchema(cmd);
    cmd.Parameters.AddWithValue("cap", _maxTrackedTables);
    cmd.Parameters.AddWithValue("other", TableStatisticsOptions.OTHER_TABLES);

    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      results[reader.GetString(0)] = new UnusedIndex(reader.GetString(1), reader.GetInt64(2));
    }

    return results;
  }

  /// <inheritdoc />
  /// <remarks>
  /// <para>
  /// Heap bytes per live row over the width those rows should need. The expected width comes from
  /// <c>pg_stats.avg_width</c> (planner statistics, already maintained by autoanalyze) plus per-tuple
  /// overhead, so this costs nothing beyond a catalog read, unlike pgstattuple, which is exact but
  /// scans the table and needs an extension that managed Postgres may not allow.
  /// </para>
  /// <para>
  /// Around 1.0 means the heap is about the size its rows need. A sustained large multiple means space
  /// that cannot be used: dead tuples awaiting vacuum, or (the case autovacuum can never fix) bytes left
  /// behind by a dropped column, which persist in every row written before the drop until the table is
  /// rewritten.
  /// </para>
  /// <para>
  /// Tables with 1,000 live rows or fewer are excluded: with few rows the per-row average is dominated
  /// by page overhead and reports alarming ratios for tables measured in kilobytes. A partitioned table
  /// and the tables summed past the cap report none, because a ratio cannot be added up.
  /// </para>
  /// <para>Read from <see cref="GetTableStorageAsync"/>, so it covers the same tables.</para>
  /// </remarks>
  public async Task<IReadOnlyDictionary<string, double>> GetTableBloatRatiosAsync(CancellationToken ct = default) {
    var storage = await GetTableStorageAsync(ct).ConfigureAwait(false);
    return storage
      .Where(table => table.Value.BloatRatio.HasValue)
      .ToDictionary(table => table.Key, table => table.Value.BloatRatio.GetValueOrDefault(), StringComparer.Ordinal);
  }

  /// <inheritdoc />
  /// <remarks>Read from <see cref="GetTableStorageAsync"/>, so it covers the same tables, capped the same way.</remarks>
  public async Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) {
    var storage = await GetTableStorageAsync(ct).ConfigureAwait(false);
    return storage.ToDictionary(table => table.Key, table => table.Value.TotalBytes, StringComparer.Ordinal);
  }

  /// <inheritdoc />
  /// <remarks>
  /// <para>
  /// The window is claimed in <c>wh_unique_emission_claims</c> on the same connection the sample reads through,
  /// so the claim and the events are in the same schema.
  /// </para>
  /// <para>
  /// <c>TABLESAMPLE SYSTEM</c> reads whole pages, each with the requested probability, of three tables, each sampled
  /// on its own at the same share: <c>wh_event_body</c>, joined to <c>wh_event_store</c> for the type, for the
  /// payload and metadata; <c>wh_event_store</c> for the pointer row every event keeps, measured as the whole row;
  /// and <c>wh_outbox</c>, for the metadata its rows holding an event carry inline (a command is no event type's).
  /// <c>pg_column_size</c> is the size the row stores, after compression, which is what the disk pays for. A stored
  /// type name carrying its assembly version is the same type as one without, so names are normalized before the
  /// types are ranked, by every part together, and the smallest folded under
  /// <see cref="TableStatisticsOptions.OTHER_EVENT_TYPES"/>.
  /// </para>
  /// </remarks>
  /// <exception cref="ArgumentOutOfRangeException">
  /// The share is not greater than 0 and at most 100, or the cap is less than 1.
  /// </exception>
  public async Task<IReadOnlyDictionary<string, EventTypeBytes>?> SampleEventTypeBytesAsync(
      EventTypeSampleRequest request, CancellationToken ct = default) {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.SamplePercent);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(request.SamplePercent, 100d);
    ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxEventTypes, 1);

    await using var connection = await _openConnectionAsync(ct).ConfigureAwait(false);
    var quotedSchema = _schema is null
      ? await _currentSchemaAsync(connection, ct).ConfigureAwait(false)
      : SqlText.Identifier(_schema);
    if (!await PhysicalColumnFill.TryClaimAsync(connection, quotedSchema, request.ClaimKey, ct).ConfigureAwait(false)) {
      return null;
    }

    var perType = new Dictionary<string, PartSums>(StringComparer.Ordinal);
    // One statement, three samples: each table is sampled on its own at the same share, so each part is extrapolated
    // from its own table. The second column says which table a row came from.
    await using var cmd = new NpgsqlCommand($"""
      SELECT es.event_type, {BODY_SOURCE},
             count(*)::bigint,
             COALESCE(sum(pg_catalog.pg_column_size(b.event_data)), 0)::bigint,
             COALESCE(sum(pg_catalog.pg_column_size(b.metadata)), 0)::bigint
      FROM {quotedSchema}.wh_event_body b TABLESAMPLE SYSTEM (@percent)
      JOIN {quotedSchema}.wh_event_store es ON es.event_id = b.event_id
      GROUP BY es.event_type
      UNION ALL
      SELECT es.event_type, {POINTER_SOURCE},
             count(*)::bigint,
             COALESCE(sum(pg_catalog.pg_column_size(es.*)), 0)::bigint,
             0::bigint
      FROM {quotedSchema}.wh_event_store es TABLESAMPLE SYSTEM (@percent)
      GROUP BY es.event_type
      UNION ALL
      SELECT o.message_type, {OUTBOX_SOURCE},
             count(*)::bigint,
             COALESCE(sum(pg_catalog.pg_column_size(o.metadata)), 0)::bigint,
             0::bigint
      FROM {quotedSchema}.wh_outbox o TABLESAMPLE SYSTEM (@percent)
      WHERE o.is_event
      GROUP BY o.message_type
      """, connection);
    cmd.Parameters.AddWithValue("percent", request.SamplePercent);
    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      var type = EventTypeMatchingHelper.NormalizeTypeName(reader.GetString(0));
      if (!perType.TryGetValue(type, out var sums)) {
        sums = new PartSums();
        perType[type] = sums;
      }
      sums.Add(reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }

    return _fold(perType, request);
  }

  /// <summary>The largest types by bytes under their names, the rest summed under the other name.</summary>
  private static Dictionary<string, EventTypeBytes> _fold(Dictionary<string, PartSums> perType, EventTypeSampleRequest request) {
    var folded = new Dictionary<string, PartSums>(StringComparer.Ordinal);
    var rank = 0;
    foreach (var (type, sums) in perType
        .OrderByDescending(t => t.Value.TotalBytes)
        .ThenBy(t => t.Key, StringComparer.Ordinal)) {
      var name = rank++ < request.MaxEventTypes ? type : TableStatisticsOptions.OTHER_EVENT_TYPES;
      if (!folded.TryGetValue(name, out var total)) {
        total = new PartSums();
        folded[name] = total;
      }
      total.Add(sums);
    }
    return folded.ToDictionary(t => t.Key, t => t.Value.ToBytes(request), StringComparer.Ordinal);
  }

  private const int BODY_SOURCE = 0;
  private const int POINTER_SOURCE = 1;
  private const int OUTBOX_SOURCE = 2;

  /// <summary>One event type's sampled rows and bytes, per part, as the sample reads them.</summary>
  private sealed class PartSums {
    private long _bodyRows;
    private long _data;
    private long _metadata;
    private long _pointerRows;
    private long _pointer;
    private long _outboxRows;
    private long _outbox;

    /// <summary>Every sampled byte of the type, which is what types are ranked by.</summary>
    public long TotalBytes => _data + _metadata + _pointer + _outbox;

    /// <summary>Adds one row of the sample: <paramref name="source"/> says which table it read.</summary>
    public void Add(int source, long rows, long bytes, long metadataBytes) {
      if (source == BODY_SOURCE) {
        _bodyRows += rows;
        _data += bytes;
        _metadata += metadataBytes;
      } else if (source == POINTER_SOURCE) {
        _pointerRows += rows;
        _pointer += bytes;
      } else {
        _outboxRows += rows;
        _outbox += bytes;
      }
    }

    /// <summary>Adds another type's sums, for folding it under the other name.</summary>
    public void Add(PartSums other) {
      Add(BODY_SOURCE, other._bodyRows, other._data, other._metadata);
      Add(POINTER_SOURCE, other._pointerRows, other._pointer, 0);
      Add(OUTBOX_SOURCE, other._outboxRows, other._outbox, 0);
    }

    public EventTypeBytes ToBytes(EventTypeSampleRequest request) => new(
      Data: _part(_bodyRows, _data, request),
      Metadata: _part(_bodyRows, _metadata, request),
      PointerRow: _part(_pointerRows, _pointer, request),
      OutboxMetadata: _part(_outboxRows, _outbox, request));

    /// <summary>A part's mean and extrapolated total, or nothing where the sample read no rows of it.</summary>
    private static EventPartBytes _part(long rows, long bytes, EventTypeSampleRequest request) =>
      rows == 0 ? default : new(rows, (double)bytes / rows, request.Extrapolate(bytes));
  }

  /// <summary>The connection's current schema, quoted, where the provider was given none.</summary>
  private static async Task<string> _currentSchemaAsync(NpgsqlConnection connection, CancellationToken ct) {
    await using var cmd = new NpgsqlCommand("SELECT pg_catalog.quote_ident(current_schema())", connection);
    return (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
  }

  /// <summary>Adds the schema as text, so that null reaches the statement as a typed null.</summary>
  private void _addSchema(NpgsqlCommand cmd) =>
    cmd.Parameters.Add(new NpgsqlParameter("schema", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)_schema ?? DBNull.Value });

  /// <summary>A table name qualified by the schema, or bare to resolve through the search path.</summary>
  private string _qualified(string table) =>
    _schema is null ? table : SqlText.Identifier(_schema) + "." + table;

  /// <inheritdoc />
  public async Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default) {
    var results = new Dictionary<string, long>();

    await using var connection = await _openConnectionAsync(ct);

    // Schema-qualified for multi-schema deployments, or bare where the search path decides.
    // 162: the inbox depth gauge counts unprocessed rows, and processed_at is work state.
    var inboxTable = _qualified("wh_inbox_state");
    var outboxTable = _qualified("wh_outbox");
    var deadLettersTable = _qualified("wh_dead_letters");

    // Dead letters are a queue like any other, sliced by status because the three
    // populations mean three different things to an operator: held is quarantine awaiting
    // a verdict, pending is the recovery backlog, failed is the operator-decision pile.
    // Emitted even at zero: "no quarantine" must be a positively-reported value, not an
    // absent series (#683: only hold TRANSITIONS were counted, so a standing five-figure
    // held population was invisible while the services that happened to be transitioning
    // were the only ones charted). Recovered rows are receipts, not depth.
    await using var cmd = new NpgsqlCommand($"""
      SELECT 'inbox' as queue_name, COUNT(*) as depth FROM {inboxTable} WHERE processed_at IS NULL
      UNION ALL
      SELECT 'outbox', COUNT(*) FROM {outboxTable} WHERE processed_at IS NULL
      UNION ALL
      SELECT 'dead_letters_held', COUNT(*) FROM {deadLettersTable} WHERE recovery_status = 2
      UNION ALL
      SELECT 'dead_letters_pending', COUNT(*) FROM {deadLettersTable} WHERE recovery_status = 0 AND recovered_at IS NULL
      UNION ALL
      SELECT 'dead_letters_failed', COUNT(*) FROM {deadLettersTable} WHERE recovery_status = 4
      """, connection);

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      results[reader.GetString(0)] = reader.GetInt64(1);
    }

    return results;
  }

  /// <inheritdoc/>
  /// <remarks>
  /// Read from the same catalog view the sizes come from, so this costs another cheap statement on
  /// the maintenance cadence rather than anything that touches the tables themselves.
  /// </remarks>
  public async Task<IReadOnlyDictionary<string, TableScanStatistics>> GetTableScanStatisticsAsync(
      CancellationToken ct = default) {
    var statistics = new Dictionary<string, TableScanStatistics>(StringComparer.Ordinal);

    await using var connection = await _openConnectionAsync(ct);
    await using var cmd = new NpgsqlCommand("""
      SELECT relname,
             COALESCE(seq_scan, 0) AS seq_scans,
             COALESCE(seq_tup_read, 0) AS seq_rows,
             COALESCE(idx_scan, 0) AS idx_scans
      FROM pg_stat_user_tables
      WHERE schemaname = COALESCE(@schema, current_schema())
      """, connection);

    _addSchema(cmd);

    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      statistics[reader.GetString(0)] =
        new TableScanStatistics(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    return statistics;
  }

  /// <summary>
  /// Adds the document filters one recorded statement names, to the table it reads.
  /// </summary>
  /// <remarks>
  /// Separated from the read so that what it understands can be established without a database.
  /// What it reads is the tag the naming interceptor writes, because a recorded statement has had
  /// its constants replaced by placeholders and a JSON key is a constant; see
  /// <see cref="DocumentFilterNaming"/>.
  /// </remarks>
  /// <param name="collected">The filters found so far, by table.</param>
  /// <param name="statement">The recorded statement text.</param>
  /// <param name="calls">How many times it ran.</param>
  /// <param name="meanMilliseconds">Its mean execution time.</param>
  internal static void Collect(
      Dictionary<string, List<ExpensivePredicate>> collected,
      string statement, long calls, double meanMilliseconds) {
    var table = DocumentFilterNaming.PerspectiveTable().Match(statement);
    if (!table.Success) {
      return;
    }

    var name = table.Value.ToLowerInvariant();
    if (!collected.TryGetValue(name, out var predicates)) {
      predicates = [];
      collected[name] = predicates;
    }

    foreach (var (document, field) in DocumentFilterNaming.FiltersIn(statement)) {
      // The same field named twice in one statement is one filter, and the same field across
      // statements is still one thing to promote, so the dearest sighting is the one kept -- which
      // is the first seen, because the statements arrive dearest first.
      if (!predicates.Exists(p => p.Document == document && p.Field == field)) {
        predicates.Add(new ExpensivePredicate(document, field, calls, meanMilliseconds));
      }
    }
  }

  /// <inheritdoc/>
  /// <remarks>
  /// <para>
  /// Read only while the capability registry has <c>pg_stat_statements</c> Ready in this database's scope (#1754):
  /// installed and loaded at startup. Any other state, or no registry, answers empty without opening a connection,
  /// because a deployment may have done neither, and naming the filter is an improvement on the advice rather than a
  /// condition of it, so its absence is not a fault.
  /// </para>
  /// <para>
  /// The state is the one the probe recorded at startup (or at the registry's last refresh). A read that fails while
  /// the capability reads Ready, because the extension was dropped since, is a fault and is not swallowed.
  /// </para>
  /// </remarks>
  /// <docs>operations/infrastructure/capabilities#readers</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/StatementStatisticsCapabilityTests.cs</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/TableStatisticsProviderTests.cs:ATaggedStatementIsReadBackFromTheStatisticsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/TableStatisticsProviderTests.cs:StatementStatisticsAreEmptyRatherThanFatalWhenNotCollectedAsync</tests>
  public async Task<IReadOnlyDictionary<string, IReadOnlyList<ExpensivePredicate>>> GetExpensivePredicatesAsync(
      CancellationToken ct = default) {
    var byTable = new Dictionary<string, IReadOnlyList<ExpensivePredicate>>(StringComparer.Ordinal);
    if (_capabilities?.GetState(_statementStatistics, _capabilityScope) != CapabilityState.Ready) {
      return byTable;
    }

    await using var connection = await _openConnectionAsync(ct);
    await using var cmd = new NpgsqlCommand("""
      SELECT query, calls, mean_exec_time
      FROM pg_stat_statements
      WHERE query ILIKE '%wh\_per\_%' AND query LIKE '%->%'
      ORDER BY mean_exec_time DESC
      LIMIT 200
      """, connection);

    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
    var collected = new Dictionary<string, List<ExpensivePredicate>>(StringComparer.Ordinal);

    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      var statement = reader.GetString(0);
      var calls = reader.GetInt64(1);
      var mean = reader.GetDouble(2);

      Collect(collected, statement, calls, mean);
    }

    foreach (var (table, predicates) in collected) {
      byTable[table] = predicates;
    }

    return byTable;
  }
}
