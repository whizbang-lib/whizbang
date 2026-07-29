using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Collective;

/// <summary>
/// Dapper-driver coordinator for applying a single <see cref="ICollectiveEvent"/> against a Postgres
/// perspective table — the Dapper counterpart of <c>CollectiveEventApplier&lt;TModel&gt;</c> (EF Core).
/// Composes the perspective's <see cref="ICollectiveSpec{TModel}"/> SET clause
/// (<see cref="DapperCollectiveSpecCompiler{TModel}"/>) with the resolver's scope filter
/// (<see cref="DapperCollectiveScopeFilterCompiler{TModel}"/>) into a single
/// <c>UPDATE &lt;table&gt; SET data = jsonb_set(...) WHERE scope-&gt;&gt;'…' = @…</c>, run over a connection
/// it opens (and disposes) from the supplied <see cref="IDbConnectionFactory"/>.
/// </summary>
/// <remarks>
/// Each apply opens its own short-lived connection from the factory — collective events are low-volume
/// (one per bulk operation), and a per-apply connection avoids sharing one connection across the
/// dispatcher's multi-model fan-out.
/// </remarks>
/// <typeparam name="TModel">The perspective model the collective event mutates.</typeparam>
/// <docs>fundamentals/messaging/collective-events</docs>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Matches the Whizbang.Data.EFCore.Postgres CollectiveEventApplier<TModel> pattern.")]
public sealed class DapperCollectiveEventApplier<TModel> where TModel : class {
  // Preserve PascalCase property names — they are the jsonb keys (matches CollectiveSettersRewriter).
  private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = null };

  /// <summary>
  /// Apply the collective event against the Dapper-backed perspective table. Returns the affected-row count.
  /// </summary>
  /// <param name="entry">Compile-time entry from <c>CollectiveApplyRegistry</c>.</param>
  /// <param name="handlerInstance">DI-resolved handler instance (the entry's <c>HandlerType</c>).</param>
  /// <param name="evt">The collective event being applied.</param>
  /// <param name="resolver">Scope resolver matching <paramref name="evt"/>'s scope kind.</param>
  /// <param name="connectionFactory">Factory the applier opens its own connection from.</param>
  /// <param name="tableName">The perspective table for <typeparamref name="TModel"/> (e.g. <c>wh_per_job</c>).</param>
  /// <param name="logger">Optional logger for transient-retry warnings (parity with the EF Core adapter).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public static async Task<int> ApplyAsync(
      CollectiveApplyEntry entry,
      object handlerInstance,
      ICollectiveEvent evt,
      ICollectiveScopeResolver resolver,
      IDbConnectionFactory connectionFactory,
      string tableName,
      IReadOnlyDictionary<Type, string> siblingTables,
      CollectiveApplyOptions options,
      ILogger? logger = null,
      CollectiveApplyHookRegistry? hookRegistry = null,
      Func<CancellationToken, ValueTask>? onBatchApplied = null,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(handlerInstance);
    ArgumentNullException.ThrowIfNull(evt);
    ArgumentNullException.ThrowIfNull(resolver);
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    ArgumentNullException.ThrowIfNull(siblingTables);
    ArgumentNullException.ThrowIfNull(options);

    if (entry.EventType != evt.GetType()) {
      throw new ArgumentException(
        $"Entry's EventType {entry.EventType.FullName} does not match the supplied event type {evt.GetType().FullName}. Registry lookup or dispatch routing is wrong.",
        nameof(entry));
    }
    if (entry.ModelType != typeof(TModel)) {
      throw new ArgumentException(
        $"Entry's ModelType {entry.ModelType.FullName} does not match TModel {typeof(TModel).FullName}. The dispatcher should fan out to DapperCollectiveEventApplier<{entry.ModelType.Name}> instead.",
        nameof(entry));
    }
    if (resolver.ScopeKind != evt.Scope.ScopeKind) {
      throw new ArgumentException(
        $"Resolver.ScopeKind '{resolver.ScopeKind}' does not match the event's Scope.ScopeKind '{evt.Scope.ScopeKind}'. The DI dispatch should have routed to a different resolver.",
        nameof(resolver));
    }

    using var _ = resolver.EnterContext(evt.Scope);

    // The query context lets the handler's Where reference sibling perspectives; the Dapper filter compiler
    // resolves their table names through it and emits a correlated EXISTS.
    var query = new DapperCollectiveQuery(siblingTables);
    if (entry.Invoker(handlerInstance, evt, query) is not ICollectiveSpec<TModel> spec) {
      throw new InvalidOperationException(
        $"Handler {entry.HandlerType.FullName}.{entry.MethodName} returned null or a non-{nameof(ICollectiveSpec<TModel>)}<{typeof(TModel).Name}> instance.");
    }

    // Resolve the apply-hook plan (store columns incl. the default updated_at/version stamping, model-field
    // setters, and cohort-WHERE modifiers) once for this apply. One ApplyTimestamp is shared across every batch.
    var hookPlan = CollectiveApplyHookPlanner.ResolveForEvent<TModel>(hookRegistry, evt);

    var setClause = DapperCollectiveSpecCompiler<TModel>.Compile(
      spec, _jsonOptions, parameterPrefix: "set",
      hookSetters: hookPlan.ModelFieldSetters, removedFields: hookPlan.RemovedModelFields);

    // Compose the effective WHERE. The resolver's scope envelope is ALWAYS computed and always binds (D0
    // safety on shared multi-tenant tables): Framework AND-composes it with the optional handler Where;
    // Custom AND-composes it with the mandatory handler cohort Where. A hook can refine (AndWhere) or replace
    // (ReplaceWhere) the cohort, but the scope envelope still binds — a hook never escapes its scope.
    var scopeFilter = resolver.ScopeFilter<TModel>(evt.Scope);
    var effectiveWhere = CollectiveWhereComposer.Compose(entry.ScopeHandling, scopeFilter, hookPlan.ComposeCohort(spec.Where));
    var whereClause = CollectivePredicateSqlCompiler<TModel>.Compile(
      effectiveWhere, parameterPrefix: "where", outerTableName: tableName);

    // §6: fold this handler's per-apply knob overrides onto the global default (0 = inherit).
    var effectiveOptions = options with {
      BatchSize = entry.BatchSizeOverride > 0 ? entry.BatchSizeOverride : options.BatchSize,
      StatementTimeoutSeconds = entry.StatementTimeoutSecondsOverride > 0
        ? entry.StatementTimeoutSecondsOverride
        : options.StatementTimeoutSeconds,
    };
    var batchSize = effectiveOptions.BatchSize > 0 ? effectiveOptions.BatchSize : CollectiveApplyOptions.Default.BatchSize;

    // §4 keyset batch + §5a exclusive advisory lock per (table, scope), mirroring EFCoreCollectiveAdapter.
    // The bare table name qualifies the outer row inside any correlated EXISTS.
    var selectSql = "SELECT id FROM " + tableName + " WHERE (" + whereClause.SqlFragment +
      ") AND id > @wb_lastid ORDER BY id LIMIT " + batchSize.ToString(CultureInfo.InvariantCulture);
    // The store-managed column writes (updated_at, the version bump, any developer audit columns) come from the
    // resolved apply-hook plan (default whizbang.timestamps: updated_at = ApplyTimestamp + a version bump). The
    // tail is rendered by the plan so both drivers produce it identically; each binds @wb_hookcol{i} its own way.
    var storeColumns = hookPlan.StoreColumns;
    var updateSql = "UPDATE " + tableName + " SET " + setClause.SqlFragment +
      hookPlan.RenderStoreColumnSetTail() + " WHERE id = ANY(@wb_ids)";
    var scopeKey = evt.Scope.ScopeKind + ":" + evt.Scope.ToString();
    long? lockKey = effectiveOptions.SerializeApplies ? CollectiveApplyLockKey.Compute(tableName, scopeKey) : null;

    var total = 0;
    var lastId = Guid.Empty;
    while (true) {
      var cursor = lastId;
      // Each batch is one short transaction. Transient concurrency errors (40P01 / 40001) are retried in-line
      // with jittered backoff so contention clears here, not at the __collective__ sink's attempt counter. Each
      // attempt opens a fresh connection (a rolled-back transaction leaves its connection unusable).
      var (count, maxId) = await PostgresDeadlockRetry.ExecuteAsync(
        () => _executeOneBatchAsync(connectionFactory, selectSql, updateSql, setClause.Parameters,
          whereClause.Parameters, effectiveOptions, lockKey, storeColumns, cursor, cancellationToken),
        maxAttempts: 5,
        logger: logger,
        cancellationToken: cancellationToken).ConfigureAwait(false);
      total += count;
      // Per-batch progress: the lease-renewal seam — see EFCoreCollectiveAdapter's twin note.
      if (onBatchApplied is not null) {
        await onBatchApplied(cancellationToken).ConfigureAwait(false);
      }
      if (count < batchSize || maxId is null) {
        break;
      }
      lastId = maxId.Value;
    }
    return total;
  }

  /// <summary>
  /// Runs ONE keyset batch in its own short transaction: <c>SET LOCAL statement_timeout</c> (when configured),
  /// the exclusive <c>pg_advisory_xact_lock</c> (when serializing), a bounded <c>SELECT id … LIMIT</c>, and an
  /// <c>UPDATE … WHERE id = ANY</c> of exactly those ids. Returns the batch count and the greatest id (next
  /// cursor). A fresh connection per call keeps retries clean.
  /// </summary>
  private static async Task<(int Count, Guid? MaxId)> _executeOneBatchAsync(
      IDbConnectionFactory connectionFactory, string selectSql, string updateSql,
      IReadOnlyDictionary<string, object?> setParameters, IReadOnlyDictionary<string, object?> whereParameters,
      CollectiveApplyOptions options, long? lockKey, IReadOnlyList<CollectiveStoreColumn> storeColumns,
      Guid lastId, CancellationToken cancellationToken) {
    var rawConnection = await connectionFactory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
    await using var connection = (DbConnection)rawConnection;
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    // SET LOCAL statement_timeout is the only form that survives PgBouncer transaction pooling, so a runaway
    // batch is cancelled by Postgres rather than left a zombie when the client gives up.
    await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

    if (options.StatementTimeoutSeconds is int secs && secs > 0) {
      await using var setCmd = connection.CreateCommand();
      setCmd.Transaction = tx;
      // 'secs' is a validated int (checked > 0 above); PostgreSQL SET LOCAL cannot be parameterized, so the
      // integer is concatenated directly — injection-safe (no string input reaches the command text).
#pragma warning disable S2077
      setCmd.CommandText = "SET LOCAL statement_timeout = " + (secs * 1000).ToString(CultureInfo.InvariantCulture);
#pragma warning restore S2077
      await setCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    if (lockKey is long key) {
      // Exclusive, transaction-scoped: released at this batch's commit (brief hold); serializes same-(table,
      // scope) collective applies across pods while disjoint scopes run concurrently.
      await using var lockCmd = connection.CreateCommand();
      lockCmd.Transaction = tx;
      lockCmd.CommandText = "SELECT pg_advisory_xact_lock(@wb_lock)";
      _addParameter(lockCmd, "wb_lock", key);
      await lockCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    var ids = new List<Guid>();
    await using (var selectCmd = connection.CreateCommand()) {
      selectCmd.Transaction = tx;
      selectCmd.CommandText = selectSql;
      _addParameters(selectCmd, whereParameters);
      _addParameter(selectCmd, "wb_lastid", lastId);
      await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        ids.Add(reader.GetGuid(0));
      }
    }

    if (ids.Count == 0) {
      await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
      return (0, null);
    }

    await using var updateCmd = connection.CreateCommand();
    updateCmd.Transaction = tx;
    updateCmd.CommandText = updateSql;
    _addParameters(updateCmd, setParameters);
    _addParameter(updateCmd, "wb_ids", ids.ToArray());
    // Store-column values from the apply-hook plan (e.g. updated_at = ApplyTimestamp). One per @wb_hookcol{i}.
    for (var i = 0; i < storeColumns.Count; i++) {
      _addParameter(updateCmd, "wb_hookcol" + i.ToString(CultureInfo.InvariantCulture), storeColumns[i].Value ?? DBNull.Value);
    }
    var count = await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    // ids came back ordered by id ASC → the last is the batch's greatest id, the next cursor.
    return (count, ids[^1]);
  }

  private static void _addParameters(DbCommand cmd, IReadOnlyDictionary<string, object?> parameters) {
    foreach (var (name, value) in parameters) {
      _addParameter(cmd, name, value ?? DBNull.Value);
    }
  }

  private static void _addParameter(DbCommand cmd, string name, object value) {
    var p = cmd.CreateParameter();
    p.ParameterName = name;
    p.Value = value;
    cmd.Parameters.Add(p);
  }
}
