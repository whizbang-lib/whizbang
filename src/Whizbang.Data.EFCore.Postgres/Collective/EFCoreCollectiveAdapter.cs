using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
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
public sealed partial class EFCoreCollectiveAdapter<TModel> where TModel : class {
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
      CollectiveApplyHookPlan<TModel> hookPlan,
      CollectiveApplyOptions options,
      string scopeKey,
      Guid collectiveEventId,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(spec);
    ArgumentNullException.ThrowIfNull(scopeFilter);
    ArgumentNullException.ThrowIfNull(hookPlan);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(scopeKey);

    var logger = dbContext.GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Collective.Apply");

    // Model-field (jsonb) setters = the spec's setters plus any the hooks added, minus any a hook removed.
    var assignments = CollectiveSettersRewriter.CollectAssignments(spec.Setters)
      .Concat(CollectiveSettersRewriter.FromHookSetters(hookPlan.ModelFieldSetters))
      .Where(a => !hookPlan.RemovedModelFields.Contains(a.PathName))
      .ToList();

    var entityType = dbContext.Model.FindEntityType(typeof(PerspectiveRow<TModel>))
      ?? throw new InvalidOperationException(
        $"PerspectiveRow<{typeof(TModel).Name}> is not mapped in the DbContext model.");
    var table = entityType.GetTableName()
      ?? throw new InvalidOperationException($"PerspectiveRow<{typeof(TModel).Name}> has no table name.");
    var schema = entityType.GetSchema();
    var qualifiedTable = schema is null ? "\"" + table + "\"" : "\"" + schema + "\".\"" + table + "\"";

    // Nested jsonb_set: jsonb_set(jsonb_set(data, @path0, @p0::jsonb), …). The path is bound as a text[]
    // parameter (no '{…}' brace literal) and the property name is parameterized, not concatenated. A computed
    // comparison setter substitutes to_jsonb((data->'X')::jsonb <op> @p::jsonb) for the plain @p::jsonb value —
    // the compared property is compile-time model metadata (a C# identifier), so it's embedded, not injected.
    var setExpr = new StringBuilder("data");
    for (var i = 0; i < assignments.Count; i++) {
      var idx = i.ToString(CultureInfo.InvariantCulture);
      var valueSql = assignments[i].Comparison is { } cmp
        ? "to_jsonb((data->'" + cmp.ComparedProperty + "')::jsonb " + cmp.SqlOperator + " @p" + idx + "::jsonb)"
        : "@p" + idx + "::jsonb";
      setExpr.Insert(0, "jsonb_set(")
        .Append(", @path").Append(idx).Append(", ").Append(valueSql).Append(')');
    }

    // Compile the predicate straight to SQL (no SELECT-id seq scan). The bare table name qualifies the outer
    // row inside any correlated EXISTS; the sibling table resolves via ICollectiveSiblingTableSource on the
    // EFCoreCollectiveQuery embedded in the predicate.
    var where = CollectivePredicateSqlCompiler<TModel>.Compile(
      scopeFilter, parameterPrefix: "where", outerTableName: table);

    // §7: the btree expression index the WHERE needs — the universal `((scope->>'t'))` tenant envelope
    // ANDed onto every apply — is created at SERVICE STARTUP by the schema generator (see
    // EFCoreServiceRegistrationGenerator._appendStandardIndexes), NOT here. Creating indexes inside the
    // apply hot path took a SHARE lock on the table on the first apply per process — unacceptable in a
    // live path in production. Cohort filters correlate by PK (id) so they need no extra index; the
    // compiler still records `where.ReferencedJsonPaths` as the compile-time basis for any future
    // per-property startup index, but nothing creates indexes at apply time anymore.

    var batchSize = options.BatchSize > 0 ? options.BatchSize : CollectiveApplyOptions.Default.BatchSize;

    // Keyset batch: select up to BatchSize ids past the cursor (bounded — never the whole cohort), then update
    // exactly those. `id AS "Value"` is EF's required scalar-projection column name. The bare table qualifies
    // the outer row inside any correlated EXISTS. The predicate re-evaluates against current state each batch —
    // safe because collective setters are constant (idempotent), and `id > @cursor` guarantees forward progress
    // even if a row falls out of the cohort after its update.
    var selectSql = "SELECT id AS \"Value\" FROM " + table + " WHERE (" + where.SqlFragment +
      ") AND id > @wb_lastid ORDER BY id LIMIT " + batchSize.ToString(CultureInfo.InvariantCulture);
    // The store-managed column writes (updated_at, the version bump, any developer audit columns) come from the
    // resolved apply-hook plan. By default the whizbang.timestamps hook contributes updated_at = ApplyTimestamp
    // and a version bump — a collective UPDATE that writes only `data` would leave those stale and break
    // change-detection (delta sync, downstream mirrors, "recently changed" reads) — and a consumer can override
    // or extend that stamping via hooks.
    var storeColumns = hookPlan.StoreColumns;
    var updateSql = "UPDATE " + qualifiedTable + " SET data = " + setExpr +
      hookPlan.RenderStoreColumnSetTail() + " WHERE id = ANY(@wb_ids)";

    // Per-(table,scope) exclusive advisory lock so collective applies to the same table+scope serialize
    // (across pods) instead of convoying; disjoint scopes hash to different keys and run concurrently.
    long? lockKey = options.SerializeApplies ? CollectiveApplyLockKey.Compute(table, scopeKey) : null;

    // Child span of the "Collective Dispatch" span (via Activity.Current): shows the per-model apply — which
    // table, how many rows, how many batches — so a slow apply is pinpointable to a table/batch, not just an
    // event. The parent span carries the event type/namespace; this one carries the physical detail.
    using var applyActivity = WhizbangActivitySource.Tracing.StartActivity("Collective Apply", ActivityKind.Client);
    if (applyActivity is not null) {
      applyActivity.SetTag("whizbang.collective.model_type", typeof(TModel).Name);
      applyActivity.SetTag("whizbang.collective.table", table);
      applyActivity.SetTag("whizbang.collective.event_id", collectiveEventId);
      applyActivity.SetTag("whizbang.collective.batch_size", batchSize);
    }

    var total = 0;
    var batches = 0;
    var lastId = Guid.Empty;
    while (true) {
      var lastCursor = lastId;
      // The per-batch transaction must run inside the DbContext's execution strategy: a DbContext configured
      // with EnableRetryOnFailure (NpgsqlRetryingExecutionStrategy — the norm in production) forbids
      // a user-initiated BeginTransaction outside strategy.ExecuteAsync (the transaction must be one retriable
      // unit). CreateExecutionStrategy() returns the configured strategy (a no-op non-retrying one when
      // retries are off, e.g. tests), so this is correct either way. PostgresDeadlockRetry stays the outer
      // backstop for 40P01/40001 when the configured strategy doesn't cover them.
      var (count, maxId) = await PostgresDeadlockRetry.ExecuteAsync(
        () => dbContext.Database.CreateExecutionStrategy().ExecuteAsync(
          () => _executeOneBatchAsync(dbContext, selectSql, updateSql, assignments, where, options, lockKey, storeColumns, lastCursor, cancellationToken)),
        maxAttempts: 5,
        logger: logger,
        cancellationToken: cancellationToken).ConfigureAwait(false);
      total += count;
      batches++;
      if (count < batchSize || maxId is null) {
        break;
      }
      lastId = maxId.Value;
    }
    if (applyActivity is not null) {
      applyActivity.SetTag("whizbang.collective.affected_rows", total);
      applyActivity.SetTag("whizbang.collective.batches", batches);
    }
    if (logger is not null) {
      LogCollectiveApplyCompleted(logger, collectiveEventId, table, total, batches);
    }
    return total;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Collective apply {CollectiveEventId} on {Table} updated {AffectedRows} rows in {Batches} batch(es)")]
  private static partial void LogCollectiveApplyCompleted(ILogger logger, Guid CollectiveEventId, string Table, int AffectedRows, int Batches);

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
      List<CollectiveSettersRewriter.CollectiveSetterAssignment> assignments,
      CollectivePredicateSqlCompiler<TModel>.CompiledWhereClause where,
      CollectiveApplyOptions options, long? lockKey, IReadOnlyList<CollectiveStoreColumn> storeColumns,
      Guid lastId, CancellationToken cancellationToken) {

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

    var updateParams = new List<Npgsql.NpgsqlParameter>((assignments.Count * 2) + 1 + storeColumns.Count);
    for (var i = 0; i < assignments.Count; i++) {
      var idx = i.ToString(CultureInfo.InvariantCulture);
      updateParams.Add(_param("path" + idx, new[] { assignments[i].PathName }));  // text[] path
      updateParams.Add(_param("p" + idx, assignments[i].JsonValue));             // JSON text, cast ::jsonb
    }
    updateParams.Add(_param("wb_ids", ids.ToArray()));  // uuid[]
    // Store-column values from the apply-hook plan (e.g. updated_at = ApplyTimestamp). One value per @wb_hookcol{i}.
    for (var i = 0; i < storeColumns.Count; i++) {
      updateParams.Add(_param("wb_hookcol" + i.ToString(CultureInfo.InvariantCulture), storeColumns[i].Value ?? DBNull.Value));
    }
    var count = await dbContext.Database.ExecuteSqlRawAsync(updateSql, updateParams, cancellationToken).ConfigureAwait(false);

    await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    // ids came back ordered by id ASC, so the last one is the batch's greatest id — the next cursor.
    return (count, ids[^1]);
  }

  private static Npgsql.NpgsqlParameter _param(string name, object value) => new(name, value);
}
