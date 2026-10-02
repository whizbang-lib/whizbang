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
/// <strong>Determinism, and its limit:</strong> the predicate is re-evaluated at apply time against
/// the projection state as it stands when the batch runs. Given a fixed order of applies the result
/// is determined by that order — but <strong>the order is not guaranteed between collectives</strong>,
/// and this paragraph used to claim otherwise.
/// </para>
/// <para>
/// Without an ordering key each collective event carries its own stream id and so becomes its own sink
/// stream, and sink streams on different instances apply in parallel. The per-scope advisory lock serializes two
/// applies to the same table and scope; it does not order them. Two collectives therefore apply in
/// whatever order they finish, not in commit order.
/// </para>
/// <para>
/// That is invisible to a collective whose setters are idempotent or commutative, and wrong for one
/// that expresses "latest wins" as a set-based flip -- <c>IsActive = (Id == e.Chosen)</c> across a
/// family of rows, where an older collective landing after a newer one leaves the wrong row active.
/// A consumer writing that shape sets <see cref="Whizbang.Core.Messaging.ICollectiveEvent.OrderingKey"/>:
/// collectives sharing a key in one scope share one sink stream, which the worker applies in commit order,
/// and a replay folds them in the same order.
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
public static partial class EFCoreCollectiveAdapter<TModel> where TModel : class {
  // The cast a bound JSON text parameter takes to become a jsonb value.
  private const string JSONB_CAST = "::jsonb";

  /// <summary>
  /// Execute the collective-event mutation as a keyset-batched set-based UPDATE, bounded by the apply
  /// <paramref name="options"/>. One raw <c>jsonb_set</c> path serves every mapping (complex-JSON, scalar/
  /// polymorphic, null setters) — the predicate is compiled straight to SQL (no id materialization), and the
  /// cohort is chunked by <see cref="CollectiveApplyOptions.BatchSize"/> so each batch is a short transaction:
  /// brief lock hold, and a per-batch <c>statement_timeout</c> (when configured) that Postgres enforces
  /// server-side (surviving PgBouncer pooling). Returns the total number of rows affected.
  /// </summary>
#pragma warning disable S107 // Apply-chain seam threads the full dispatch context (entry, session, hooks, per-batch callback) — a parameter object would ripple through every driver and fake for no call-site gain
  public static async Task<int> ExecuteAsync(
      DbContext dbContext,
      ICollectiveSpec<TModel> spec,
      Expression<Func<PerspectiveRow<TModel>, bool>> scopeFilter,
      CollectiveApplyHookPlan<TModel> hookPlan,
      CollectiveApplyOptions options,
      string scopeKey,
      Guid collectiveEventId,
      Func<CancellationToken, ValueTask>? onBatchApplied = null,
      CancellationToken cancellationToken = default) {
#pragma warning restore S107

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

    var (setList, setParameters) = _compileSetList(assignments);

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
    var updateSql = "UPDATE " + qualifiedTable + " SET " + setList +
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
      // A batch that does not get its lock inside the bounded wait waits again, renewing the lease through
      // onBatchApplied first, so waiting behind another batch neither loses the work nor counts an attempt (#964).
      var (count, maxId) = await CollectiveApplyContention.WaitingForTheLockAsync(
        () => PostgresDeadlockRetry.ExecuteAsync(
          () => dbContext.Database.CreateExecutionStrategy().ExecuteAsync(
            () => _executeOneBatchAsync(dbContext, selectSql, updateSql, setParameters, where, options, lockKey, storeColumns, lastCursor, cancellationToken)),
          maxAttempts: 5,
          logger: logger,
          cancellationToken: cancellationToken),
        options, onBatchApplied, cancellationToken).ConfigureAwait(false);
      total += count;
      batches++;
      // Per-batch progress: lets the caller renew its work lease DURING a long apply — without
      // this, an apply spanning many batches outlives the lease and the (idempotent) work is
      // redelivered. Invoked after every committed batch, including the final one.
      if (onBatchApplied is not null) {
        await onBatchApplied(cancellationToken).ConfigureAwait(false);
      }
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

  /// <summary>
  /// Compiles the setters to the UPDATE's <c>SET</c> list and the parameters it binds. Document setters become one
  /// nested <c>jsonb_set(jsonb_set(data, @path0, @p0::jsonb), …)</c>: the path is bound as a text[] parameter (no
  /// '{…}' brace literal) and the property name is parameterized, not concatenated. A computed comparison setter
  /// substitutes <c>to_jsonb((data-&gt;'X')::jsonb &lt;op&gt; @p::jsonb)</c> for the plain value — the compared property
  /// is compile-time model metadata (a C# identifier), so it's embedded, not injected. A setter on a
  /// <c>[PhysicalField]</c> assigns its column (and the document path too when the storage mode keeps both): from the
  /// typed parameter <c>@pc{i}</c> (an enumeration as its underlying number, a vector as a pgvector value), or, for a
  /// keyed array in a jsonb column, through the same upsert expression. A comparison over a physical field reads the
  /// column null-safely, and when no document path changes <c>data</c> is not assigned at all.
  /// </summary>
  private static (string SetList, List<KeyValuePair<string, object>> Parameters) _compileSetList(
      List<CollectiveSettersRewriter.CollectiveSetterAssignment> assignments) {
    var setExpr = new StringBuilder("data");
    var documentWrites = 0;
    var columns = new List<(string Column, string ValueSql)>();
    var parameters = new Dictionary<string, object>(StringComparer.Ordinal);
    // The value each property (and each jsonb column) holds so far in this spec: an element upsert starts from it,
    // so two upserts on one list compose instead of the second rewriting the stored list over the first.
    var assigned = new Dictionary<string, string>(StringComparer.Ordinal);
    var assignedColumns = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < assignments.Count; i++) {
      var a = assignments[i];
      var idx = i.ToString(CultureInfo.InvariantCulture);
      var target = CollectivePhysicalColumns.Resolve(typeof(TModel), a.PathName);
      // A computed comparison: the boolean over the compared property, as a column value and as a document value.
      var (columnComparison, documentComparison) = a.Comparison is { } cmp
        ? _compileComparison(cmp, a, idx, parameters)
        : ((string?)null, (string?)null);
      if (target is not { InDocument: false }) {
        if (a.Comparison is null) {
          parameters["p" + idx] = a.JsonValue;
        }
        var valueSql = a switch {
          { ElementKey: { } key } => CollectiveElementUpsertSql.ValueSql(
            a.PathName, key, "@p" + idx + JSONB_CAST, assigned.GetValueOrDefault(a.PathName)),
          { Comparison: not null } => documentComparison!,
          _ => "@p" + idx + JSONB_CAST,
        };
        // One key of the property's object, kept in the document too: jsonb_set's two-step path changes nothing
        // when the object is null or absent, as the column's key set does.
        parameters["path" + idx] = a.JsonbKey is { } jsonbKey ? new[] { a.PathName, jsonbKey } : new[] { a.PathName };  // text[] path
        assigned[a.PathName] = valueSql;
        setExpr.Insert(0, "jsonb_set(")
          .Append(", @path").Append(idx).Append(", ").Append(valueSql).Append(')');
        documentWrites++;
      }
      if (target is { } physical) {
        var columnSql = _columnValueSql(a, physical, idx, columnComparison, assignedColumns, parameters);
        assignedColumns[physical.ColumnName] = columnSql;
        columns.Add((physical.ColumnName, columnSql));
      }
    }
    var setList = CollectivePhysicalColumns.RenderSetList(documentWrites > 0 ? setExpr.ToString() : null, columns);
    return (setList, [.. parameters]);
  }

  // The new value of a physical column: a computed comparison's boolean, the same keyed upsert a document path uses
  // (over the jsonb column, from its value so far in this spec), one key of a jsonb column set in place, a whole jsonb
  // value bound as jsonb, a typed pgvector parameter, or the column scalar (an enumeration as its underlying number).
  private static string _columnValueSql(
      CollectiveSettersRewriter.CollectiveSetterAssignment a, PerspectivePhysicalField physical, string idx,
      string? columnComparison, Dictionary<string, string> assignedColumns, Dictionary<string, object> parameters) {
    if (columnComparison is not null) {
      return columnComparison;
    }
    var source = assignedColumns.GetValueOrDefault(physical.ColumnName) ?? CollectivePhysicalColumns.Quote(physical.ColumnName);
    if (a.ElementKey is { } key) {
      parameters["p" + idx] = a.JsonValue;  // the element, shared with the document path when both are written
      return CollectiveElementUpsertSql.ValueSql(a.PathName, key, "@p" + idx + JSONB_CAST, source);
    }
    if (a.JsonbKey is { } jsonbKey) {
      // One key of the stored object, set from the column's value so far in this spec; the other keys stay.
      parameters["p" + idx] = a.JsonValue;
      parameters["kpath" + idx] = new[] { jsonbKey };
      return CollectivePhysicalColumns.JsonbKeySetSql(source, "@kpath" + idx, "@p" + idx + JSONB_CAST);
    }
    if (physical.IsJsonbColumn) {
      // The whole value, as the JSON the persistence profile writes, cast to jsonb; null clears the column, as the
      // per-event write of a null property does.
      parameters["pc" + idx] = a.IsNull ? DBNull.Value : a.JsonValue;
      return "@pc" + idx + JSONB_CAST;
    }
    object? value = physical.IsVector
      ? _vector(a.Value)
      : CollectivePhysicalColumns.ColumnValue(physical, a.Value);
    parameters["pc" + idx] = value ?? DBNull.Value;
    return "@pc" + idx;
  }

  // The per-event upsert binds a vector column as a pgvector value; the collective binds the same.
  private static Pgvector.Vector? _vector(object? value) => value is float[] vector ? new Pgvector.Vector(vector) : null;

  // A computed comparison setter's boolean, as the column value and as the document value. Over a physical field it
  // reads the column null-safely and binds the compared value typed (@pc{i}); otherwise it compares the document's
  // jsonb and binds the value's JSON (@p{i}).
  private static (string Column, string Document) _compileComparison(
      CollectiveSettersRewriter.CollectiveComputedComparison cmp, CollectiveSettersRewriter.CollectiveSetterAssignment a,
      string idx, Dictionary<string, object> parameters) {
    if (CollectivePhysicalColumns.Resolve(typeof(TModel), cmp.ComparedProperty) is { } compared) {
      CollectivePhysicalColumns.EnsureComparable(typeof(TModel), compared);
      parameters["pc" + idx] = CollectivePhysicalColumns.ColumnValue(compared, a.Value) ?? DBNull.Value;
      var column = CollectivePhysicalColumns.NullSafeComparison(
        CollectivePhysicalColumns.Quote(compared.ColumnName), cmp.SqlOperator, "@pc" + idx);
      return (column, "to_jsonb(" + column + ")");
    }
    parameters["p" + idx] = a.JsonValue;
    var comparison = "(data->'" + cmp.ComparedProperty + "')" + JSONB_CAST + " " + cmp.SqlOperator + " @p" + idx + JSONB_CAST;
    return ("(" + comparison + ")", "to_jsonb(" + comparison + ")");
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
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Runs one bounded batch in its own transaction: the two statements, the compiled setters and predicate, the apply options, the advisory lock key, the store columns and the cursor it resumes from. The Dapper applier's equivalent takes the same shape, and the two are read side by side.")]
  private static async Task<(int Count, Guid? MaxId)> _executeOneBatchAsync(
      DbContext dbContext, string selectSql, string updateSql,
      List<KeyValuePair<string, object>> setParameters,
      CollectivePredicateSqlCompiler<TModel>.CompiledWhereClause where,
      CollectiveApplyOptions options, long? lockKey, IReadOnlyList<CollectiveStoreColumn> storeColumns,
      Guid lastId, CancellationToken cancellationToken) {

    await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

    if (options.StatementTimeoutSeconds is int secs && secs > 0) {
      await dbContext.Database.ExecuteSqlRawAsync(
        "SELECT set_config('statement_timeout', @wb_stmt_timeout, true)",
        [_param("wb_stmt_timeout", (secs * 1000).ToString(CultureInfo.InvariantCulture))],
        cancellationToken).ConfigureAwait(false);
    }

    if (lockKey is long key) {
      // Exclusive, transaction-scoped: released at this batch's commit (brief hold), so standard applies and
      // the next collective batch proceed between batches; blocks other collective applies to the same key.
      //
      // The wait is bounded, so contention fails in seconds with a name rather than sitting for the whole
      // statement timeout and surfacing as an indistinguishable timeout. lock_timeout is LOCAL to this
      // transaction and applies to the lock wait alone, so it does not shorten the statements that follow.
      if (options.LockWaitSeconds is int waitSeconds && waitSeconds > 0) {
        await dbContext.Database.ExecuteSqlRawAsync(
          "SELECT set_config('lock_timeout', @wb_lock_timeout, true)",
          [_param("wb_lock_timeout", (waitSeconds * 1000).ToString(CultureInfo.InvariantCulture))],
          cancellationToken).ConfigureAwait(false);
        try {
          await dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(@wb_lock)",
            [_param("wb_lock", key)], cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) when (CollectiveApplyContention.IsLockTimeout(ex)) {
          // Nothing is wrong with the event or the perspective: another batch holds the lock for this
          // table and scope. Named so a caller can tell it from a failed apply and neither count an
          // attempt nor drop the lease.
          throw new CollectiveApplyLockBusyException(CollectiveApplyContention.TableOf(selectSql), waitSeconds, ex);
        }
        // The lock is held; the remaining statements wait for ordinary row locks on the usual terms.
        await dbContext.Database.ExecuteSqlRawAsync(
          "SELECT set_config('lock_timeout', '0', true)", [], cancellationToken).ConfigureAwait(false);
      } else {
        await dbContext.Database.ExecuteSqlRawAsync(
          "SELECT pg_advisory_xact_lock(@wb_lock)",
          [_param("wb_lock", key)], cancellationToken).ConfigureAwait(false);
      }
    }

    var selectParams = new List<Npgsql.NpgsqlParameter>(where.Parameters.Count + 1);
    foreach (var (name, value) in where.Parameters) {
      selectParams.Add(_param(name, value ?? (object)DBNull.Value));
    }
    selectParams.Add(_param("wb_lastid", lastId));
    var ids = await dbContext.Database.SqlQueryRaw<Guid>(selectSql, [.. selectParams.Cast<object>()])
      .ToListAsync(cancellationToken).ConfigureAwait(false);

    if (ids.Count == 0) {
      await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
      return (0, null);
    }

    var updateParams = new List<Npgsql.NpgsqlParameter>(setParameters.Count + 1 + storeColumns.Count);
    foreach (var (name, value) in setParameters) {
      updateParams.Add(_param(name, value));  // text[] paths, JSON text cast ::jsonb, typed column values
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
