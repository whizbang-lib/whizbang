using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// EF Core driver adapter for collective-event apply (Slice 6'). Takes
/// the perspective's <see cref="ICollectiveSpec{TModel}"/> mutation
/// description and the resolver's scope filter and produces a single
/// <c>ExecuteUpdateAsync</c> call against the perspective table whose
/// <c>WHERE</c> is exactly the scope predicate — nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline:
/// </para>
/// <list type="number">
///   <item><description>Resolve <c>dbContext.Set&lt;PerspectiveRow&lt;TModel&gt;&gt;()</c>.</description></item>
///   <item><description>Compose <c>Where(scopeFilter)</c> from the resolver. That's the SOLE <c>WHERE</c> — there is no matched-id membership clause; the event has no captured matched set (Slice 1' contract change).</description></item>
///   <item><description>Translate the perspective's <c>ICollectiveSetters</c> spec into <c>UpdateSettersBuilder&lt;PerspectiveRow&lt;TModel&gt;&gt;</c> shape via <see cref="CollectiveSettersRewriter"/>, which emits native nested <c>SetProperty(r =&gt; r.Data.&lt;Prop&gt;, value)</c> calls — EF Core 10 updates the <c>ComplexProperty().ToJson()</c> sub-properties directly.</description></item>
///   <item><description>Execute via <c>ExecuteUpdateAsync</c>.</description></item>
/// </list>
/// <para>
/// Returns the count of affected rows so the runner can log / surface
/// it as a metric.
/// </para>
/// <para>
/// <strong>Determinism:</strong> the predicate is re-evaluated at apply
/// time against the projection state at that point in the event sequence.
/// Because event-sourcing guarantees the projection state is fully
/// determined by the event log up to that point, the result is
/// deterministic — and reflects the logically correct outcome, not the
/// original execution's possibly-wrong (e.g. out-of-order delivery) one.
/// </para>
/// <para>
/// AOT: matches Whizbang.Data.EFCore.Postgres's established pattern of
/// suppressing IL2060/IL3050 — EF Core's query translation pipeline is
/// reflection-based by design.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The perspective model whose projection table is mutated.</typeparam>
/// <docs>fundamentals/messaging/collective-events</docs>
[SuppressMessage("AOT", "IL2060:MakeGenericMethod can break functionality when AOT compiling", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2070:UnrecognizedReflectionPattern", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL2075:UnrecognizedReflectionPattern", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Adapter is generic over TModel; static factory + execute methods match the pattern of EF Core's own generic-static helpers.")]
public sealed class EFCoreCollectiveAdapter<TModel> where TModel : class {
  /// <summary>
  /// Execute the collective-event mutation as a keyset-batched set-based UPDATE, bounded by the apply
  /// <paramref name="options"/>. One raw <c>jsonb_set</c> path serves every mapping (complex-JSON, scalar/
  /// polymorphic, null setters) — the predicate is compiled straight to SQL (no id materialization), and the
  /// cohort is chunked by <see cref="CollectiveApplyOptions.BatchSize"/> so each batch is a short transaction:
  /// brief lock hold, and a per-batch <c>statement_timeout</c> (when configured) that Postgres enforces
  /// server-side (surviving PgBouncer pooling). Returns the total number of rows affected.
  /// </summary>
  public static async Task<int> ExecuteAsync(
      DbContext dbContext,
      ICollectiveSpec<TModel> spec,
      Expression<Func<PerspectiveRow<TModel>, bool>> scopeFilter,
      CollectiveApplyOptions options,
      string scopeKey,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(spec);
    ArgumentNullException.ThrowIfNull(scopeFilter);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(scopeKey);

    var assignments = CollectiveSettersRewriter.CollectAssignments(spec.Setters);

    var entityType = dbContext.Model.FindEntityType(typeof(PerspectiveRow<TModel>))
      ?? throw new InvalidOperationException(
        $"PerspectiveRow<{typeof(TModel).Name}> is not mapped in the DbContext model.");
    var table = entityType.GetTableName()
      ?? throw new InvalidOperationException($"PerspectiveRow<{typeof(TModel).Name}> has no table name.");
    var schema = entityType.GetSchema();
    var qualifiedTable = schema is null ? "\"" + table + "\"" : "\"" + schema + "\".\"" + table + "\"";

    // Nested jsonb_set: jsonb_set(jsonb_set(data, @path0, @p0::jsonb), …). The path is bound as a text[]
    // parameter (no '{…}' brace literal) and the property name is parameterized, not concatenated.
    var setExpr = new StringBuilder("data");
    for (var i = 0; i < assignments.Count; i++) {
      var idx = i.ToString(CultureInfo.InvariantCulture);
      setExpr.Insert(0, "jsonb_set(")
        .Append(", @path").Append(idx).Append(", @p").Append(idx).Append("::jsonb)");
    }

    // Compile the predicate straight to SQL (no SELECT-id seq scan). The bare table name qualifies the outer
    // row inside any correlated EXISTS; the sibling table resolves via ICollectiveSiblingTableSource on the
    // EFCoreCollectiveQuery embedded in the predicate.
    var where = CollectivePredicateSqlCompiler<TModel>.Compile(
      scopeFilter, parameterPrefix: "where", outerTableName: table);

    // §7: ensure a btree expression index exists for each jsonb path the WHERE filters on (incl. scope->>'t')
    // before scanning — otherwise the keyset SELECT seq-scans every batch. Once-per-process, best-effort.
    if (options.EnsureIndexes) {
      await CollectiveIndexEnsurer.EnsureAsync(dbContext, where.ReferencedJsonPaths, cancellationToken)
        .ConfigureAwait(false);
    }

    var batchSize = options.BatchSize > 0 ? options.BatchSize : CollectiveApplyOptions.Default.BatchSize;

    // Keyset batch: select up to BatchSize ids past the cursor (bounded — never the whole cohort), then update
    // exactly those. `id AS "Value"` is EF's required scalar-projection column name. The bare table qualifies
    // the outer row inside any correlated EXISTS. The predicate re-evaluates against current state each batch —
    // safe because collective setters are constant (idempotent), and `id > @cursor` guarantees forward progress
    // even if a row falls out of the cohort after its update.
    var selectSql = "SELECT id AS \"Value\" FROM " + table + " WHERE (" + where.SqlFragment +
      ") AND id > @wb_lastid ORDER BY id LIMIT " + batchSize.ToString(CultureInfo.InvariantCulture);
    var updateSql = "UPDATE " + qualifiedTable + " SET data = " + setExpr + " WHERE id = ANY(@wb_ids)";

    // Per-(table,scope) exclusive advisory lock so collective applies to the same table+scope serialize
    // (across pods) instead of convoying; disjoint scopes hash to different keys and run concurrently.
    long? lockKey = options.SerializeApplies ? CollectiveApplyLockKey.Compute(table, scopeKey) : null;

    var total = 0;
    var lastId = Guid.Empty;
    while (true) {
      var lastCursor = lastId;
      var (count, maxId) = await PostgresDeadlockRetry.ExecuteAsync(
        () => _executeOneBatchAsync(dbContext, selectSql, updateSql, assignments, where, options, lockKey, lastCursor, cancellationToken),
        maxAttempts: 5,
        cancellationToken: cancellationToken).ConfigureAwait(false);
      total += count;
      if (count < batchSize || maxId is null) {
        break;
      }
      lastId = maxId.Value;
    }
    return total;
  }

  /// <summary>
  /// Runs ONE keyset batch in its own short transaction: sets a transaction-local <c>statement_timeout</c> via
  /// <c>set_config(…, true)</c> when configured (the <c>SET LOCAL</c> equivalent — the only form that survives
  /// PgBouncer pooling), selects up to <c>BatchSize</c> ids past the cursor (ordered, so the last is the
  /// next cursor — Postgres uuid order, no client-side compare and no <c>max(uuid)</c> aggregate), and updates
  /// exactly those ids. On failure the <c>await using</c> transaction rolls back before the retry re-attempts
  /// the same batch (idempotent for the same cursor).
  /// </summary>
  private static async Task<(int Count, Guid? MaxId)> _executeOneBatchAsync(
      DbContext dbContext, string selectSql, string updateSql,
      IReadOnlyList<CollectiveSettersRewriter.CollectiveSetterAssignment> assignments,
      CollectivePredicateSqlCompiler<TModel>.CompiledWhereClause where,
      CollectiveApplyOptions options, long? lockKey, Guid lastId, CancellationToken cancellationToken) {

    await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

    if (options.StatementTimeoutSeconds is int secs && secs > 0) {
      await dbContext.Database.ExecuteSqlRawAsync(
        "SELECT set_config('statement_timeout', @wb_stmt_timeout, true)",
        new[] { _param("wb_stmt_timeout", (secs * 1000).ToString(CultureInfo.InvariantCulture)) },
        cancellationToken).ConfigureAwait(false);
    }

    if (lockKey is long key) {
      // Exclusive, transaction-scoped: released at this batch's commit (brief hold), so standard applies and
      // the next collective batch proceed between batches; blocks other collective applies to the same key.
      await dbContext.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(@wb_lock)",
        new[] { _param("wb_lock", key) }, cancellationToken).ConfigureAwait(false);
    }

    var selectParams = new List<Npgsql.NpgsqlParameter>(where.Parameters.Count + 1);
    foreach (var (name, value) in where.Parameters) {
      selectParams.Add(_param(name, value ?? (object)DBNull.Value));
    }
    selectParams.Add(_param("wb_lastid", lastId));
    var ids = await dbContext.Database.SqlQueryRaw<Guid>(selectSql, selectParams.Cast<object>().ToArray())
      .ToListAsync(cancellationToken).ConfigureAwait(false);

    if (ids.Count == 0) {
      await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
      return (0, null);
    }

    var updateParams = new List<Npgsql.NpgsqlParameter>((assignments.Count * 2) + 1);
    for (var i = 0; i < assignments.Count; i++) {
      var idx = i.ToString(CultureInfo.InvariantCulture);
      updateParams.Add(_param("path" + idx, new[] { assignments[i].PathName }));  // text[] path
      updateParams.Add(_param("p" + idx, assignments[i].JsonValue));             // JSON text, cast ::jsonb
    }
    updateParams.Add(_param("wb_ids", ids.ToArray()));  // uuid[]
    var count = await dbContext.Database.ExecuteSqlRawAsync(updateSql, updateParams, cancellationToken).ConfigureAwait(false);

    await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    // ids came back ordered by id ASC, so the last one is the batch's greatest id — the next cursor.
    return (count, ids[^1]);
  }

  private static Npgsql.NpgsqlParameter _param(string name, object value) => new(name, value);
}
