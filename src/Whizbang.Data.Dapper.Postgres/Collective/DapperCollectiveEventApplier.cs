// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Core.ValueObjects;
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
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveApplierIntegrationTests.cs:ApplyAsync_TenantScoped_UpdatesOnlyInScopeRowsAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveApplierIntegrationTests.cs:ApplyAsync_CohortLargerThanBatchSize_UpdatesEveryRowAcrossBatchesAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveUnitTests.cs:Applier_EventTypeMismatch_ThrowsArgumentAsync</tests>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Matches the Whizbang.Data.EFCore.Postgres CollectiveEventApplier pattern.")]
public static class DapperCollectiveEventApplier<TModel> where TModel : class {

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
  /// <param name="collectiveEventId">The collective event's id: the apply's progress record is kept under it and the
  /// entry's <see cref="CollectiveApplyEntry.EntryKey"/>, so a retry resumes after the last committed batch and a finished
  /// apply is skipped (#1969). <see cref="Guid.Empty"/> (the default) records the progress under an id of its own, so
  /// that apply can never be resumed or skipped.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
#pragma warning disable S107 // Apply-chain seam threads the full dispatch context (entry, session, hooks, per-batch callback) — a parameter object would ripple through every driver and fake for no call-site gain
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
      Guid collectiveEventId = default,
      CancellationToken cancellationToken = default) {
#pragma warning restore S107

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
        $"Entry's EventType {TypeNameFormatter.DisplayName(entry.EventType)} does not match the supplied event type {TypeNameFormatter.DisplayName(evt.GetType())}. Registry lookup or dispatch routing is wrong.",
        nameof(entry));
    }
    if (entry.ModelType != typeof(TModel)) {
      throw new ArgumentException(
        $"Entry's ModelType {TypeNameFormatter.DisplayName(entry.ModelType)} does not match TModel {TypeNameFormatter.DisplayName(typeof(TModel))}. The dispatcher should fan out to DapperCollectiveEventApplier<{entry.ModelType.Name}> instead.",
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
        $"Handler {TypeNameFormatter.DisplayName(entry.HandlerType)}.{entry.MethodName} returned null or a non-{nameof(ICollectiveSpec<>)}<{typeof(TModel).Name}> instance.");
    }

    // Resolve the apply-hook plan (store columns incl. the default updated_at/version stamping, model-field
    // setters, and cohort-WHERE modifiers) once for this apply. One ApplyTimestamp is shared across every batch.
    var hookPlan = CollectiveApplyHookPlanner.ResolveForEvent<TModel>(hookRegistry, evt);

    var setClause = DapperCollectiveSpecCompiler<TModel>.Compile(
      spec, DapperCollectiveEventApplierJson.Options, parameterPrefix: "set",
      hookSetters: hookPlan.ModelFieldSetters, removedFields: hookPlan.RemovedModelFields);

    // Compose the effective WHERE. The resolver's scope envelope is ALWAYS computed and always binds (D0
    // safety on shared multi-tenant tables): Framework AND-composes it with the optional handler Where —
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

    // §4 keyset batch + §5a exclusive advisory lock per (table, scope), mirroring EFCoreCollectiveAdapter: one statement
    // per batch reads the apply's progress record, selects the next batch after it, moves the record and updates the
    // batch (#1969). The bare table name qualifies the outer row inside any correlated EXISTS. The progress table is
    // named bare, as the perspective table is: the driver's connections resolve both through the service's schema.
    // The store-managed column writes (updated_at, the version bump, any developer audit columns) come from the
    // resolved apply-hook plan (default whizbang.timestamps: updated_at = ApplyTimestamp + a version bump). The
    // tail is rendered by the plan so both drivers produce it identically; each binds @wb_hookcol{i} its own way. A
    // resumed apply stamps the apply's first timestamp, the one its record holds.
    // The scope's identity, not its kind. ToString() is the kind, so this line once read
    // "tenant:tenant" for every tenant and put them all on one lock per table.
    var scopeKey = evt.Scope.ScopeIdentity;
    long? lockKey = effectiveOptions.SerializeApplies ? CollectiveApplyLockKey.Compute(tableName, scopeKey) : null;
    var batch = new BatchPlan(
      CollectiveApplyProgress.Render(new CollectiveApplyBatch {
        Table = tableName,
        UpdateTarget = tableName,
        ProgressTable = CollectiveApplyProgress.TABLE,
        Where = whereClause.SqlFragment,
        SetList = setClause.SqlFragment + hookPlan.RenderStoreColumnSetTail(CollectiveApplyProgress.APPLIED_AT_SQL),
        BatchSize = batchSize,
        SerializedByTheApplyLock = lockKey is not null,
      }),
      tableName, setClause.Parameters, whereClause.Parameters, hookPlan.StoreColumns);
    var eventId = collectiveEventId == Guid.Empty ? (Guid)TrackedGuid.New() : collectiveEventId;
    var appliedAt = hookPlan.AppliedAt;

    // Each batch is one short transaction. Transient concurrency errors (40P01 / 40001) are retried in-line
    // with jittered backoff so contention clears here, not at the __collective__ sink's attempt counter. Each
    // attempt opens a fresh connection (a rolled-back transaction leaves its connection unusable).
    // A batch that does not get its lock inside the bounded wait waits again, renewing the lease through
    // onBatchApplied first (#964) — see EFCoreCollectiveAdapter's twin note. onBatchApplied is also the per-batch
    // lease-renewal seam after every committed batch.
    var (total, _) = await CollectiveApplyProgress.RunAsync(
      from => CollectiveApplyContention.WaitingForTheLockAsync(
        () => PostgresDeadlockRetry.ExecuteAsync(
          () => _executeOneBatchAsync(
            connectionFactory, batch, new CollectiveApplyCursor(eventId, entry.EntryKey, from, appliedAt),
            effectiveOptions, lockKey, cancellationToken),
          maxAttempts: 5,
          logger: logger,
          cancellationToken: cancellationToken),
        effectiveOptions, onBatchApplied, cancellationToken),
      onBatchApplied, cancellationToken).ConfigureAwait(false);
    return total;
  }

  // The batch statement, the table it updates (named when its apply lock is busy) and its fixed parameters.
  private sealed record BatchPlan(
    string Sql, string Table, IReadOnlyDictionary<string, object?> SetParameters,
    IReadOnlyDictionary<string, object?> WhereParameters, IReadOnlyList<CollectiveStoreColumn> StoreColumns);

  /// <summary>
  /// Runs ONE batch in its own short transaction: <c>SET LOCAL statement_timeout</c> (when configured), the exclusive
  /// <c>pg_advisory_xact_lock</c> (when serializing), and the batch statement, which selects up to <c>BatchSize</c> ids
  /// after the apply's progress record, moves the record and updates exactly those ids (#1969). A fresh connection per
  /// call keeps retries clean; a rolled-back attempt left the record where it was, so the retry selects the same ids.
  /// </summary>
  private static async Task<CollectiveApplyBatchResult> _executeOneBatchAsync(
      IDbConnectionFactory connectionFactory, BatchPlan batch, CollectiveApplyCursor cursor,
      CollectiveApplyOptions options, long? lockKey, CancellationToken cancellationToken) {
    var rawConnection = await connectionFactory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
    await using var connection = (DbConnection)rawConnection;
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    // SET LOCAL statement_timeout is the only form that survives PgBouncer transaction pooling, so a runaway
    // batch is canceled by Postgres rather than left a zombie when the client gives up.
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
      await _takeApplyLockAsync(connection, tx, key, batch.Table, options, cancellationToken).ConfigureAwait(false);
    }

    CollectiveApplyBatchResult result;
    await using (var batchCmd = connection.CreateCommand()) {
      batchCmd.Transaction = tx;
      batchCmd.CommandText = batch.Sql;
      _addParameters(batchCmd, batch.WhereParameters);
      _addParameters(batchCmd, batch.SetParameters);
      // Store-column values from the apply-hook plan (e.g. updated_at = ApplyTimestamp). One per @wb_hookcol{i}.
      for (var i = 0; i < batch.StoreColumns.Count; i++) {
        _addParameter(batchCmd, "wb_hookcol" + i.ToString(CultureInfo.InvariantCulture), batch.StoreColumns[i].Value ?? DBNull.Value);
      }
      CollectiveApplyProgress.Bind(batchCmd, cursor);
      await using var reader = await batchCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      result = await CollectiveApplyProgress.ReadAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    return result;
  }

  /// <summary>
  /// Takes this batch's exclusive apply lock, waiting no longer than the configured bound.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Transaction-scoped, so it releases at this batch's commit (brief hold); serializes same-(table,
  /// scope) collective applies across pods while disjoint scopes run concurrently.
  /// </para>
  /// <para>
  /// The wait is bounded, so contention fails in seconds with a name rather than sitting for the whole
  /// statement timeout and surfacing as an indistinguishable timeout. <c>lock_timeout</c> is LOCAL to
  /// this transaction and applies to the lock wait alone, so it does not shorten the statements that
  /// follow, and it is cleared once the lock is held.
  /// </para>
  /// </remarks>
  /// <exception cref="CollectiveApplyLockBusyException">
  /// Another batch holds the lock for this table and scope.
  /// </exception>
  private static async Task _takeApplyLockAsync(
      DbConnection connection, DbTransaction tx, long key, string table,
      CollectiveApplyOptions options, CancellationToken cancellationToken) {
    var waitSeconds = options.LockWaitSeconds is int configured && configured > 0 ? configured : 0;
    if (waitSeconds > 0) {
      await using var timeoutCmd = connection.CreateCommand();
      timeoutCmd.Transaction = tx;
      timeoutCmd.CommandText = "SELECT set_config('lock_timeout', @wb_lock_timeout, true)";
      _addParameter(timeoutCmd, "wb_lock_timeout", (waitSeconds * 1000).ToString(CultureInfo.InvariantCulture));
      await timeoutCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    await using (var lockCmd = connection.CreateCommand()) {
      lockCmd.Transaction = tx;
      lockCmd.CommandText = "SELECT pg_advisory_xact_lock(@wb_lock)";
      _addParameter(lockCmd, "wb_lock", key);
      try {
        await lockCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      } catch (Exception ex) when (CollectiveApplyContention.IsLockTimeout(ex)) {
        // Nothing is wrong with the event or the perspective: another batch holds the lock for this
        // table and scope. Named so a caller can tell it from a failed apply and neither count an
        // attempt nor drop the lease.
        throw new CollectiveApplyLockBusyException(table, waitSeconds, ex);
      }
    }

    if (waitSeconds > 0) {
      // The lock is held; the remaining statements wait for ordinary row locks on the usual terms.
      await using var clearCmd = connection.CreateCommand();
      clearCmd.Transaction = tx;
      clearCmd.CommandText = "SELECT set_config('lock_timeout', '0', true)";
      await clearCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
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

/// <summary>
/// One serializer configuration for every <c>TModel</c> instantiation: PascalCase property
/// names are preserved because they are the jsonb keys (matches CollectiveSettersRewriter).
/// </summary>
internal static class DapperCollectiveEventApplierJson {
  internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = null };
}
