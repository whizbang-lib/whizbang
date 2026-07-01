using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
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

    var setClause = DapperCollectiveSpecCompiler<TModel>.Compile(spec, _jsonOptions, parameterPrefix: "set");

    // Compose the effective WHERE. The resolver's scope envelope is ALWAYS computed and always binds (D0
    // safety on shared multi-tenant tables): Framework AND-composes it with the optional handler Where;
    // Custom AND-composes it with the mandatory handler cohort Where. A handler refines within scope, never
    // escapes it.
    var scopeFilter = resolver.ScopeFilter<TModel>(evt.Scope);
    var effectiveWhere = CollectiveWhereComposer.Compose(entry.ScopeHandling, scopeFilter, spec.Where);
    var whereClause = CollectivePredicateSqlCompiler<TModel>.Compile(
      effectiveWhere, parameterPrefix: "where", outerTableName: tableName);

    var sql = $"UPDATE {tableName} SET {setClause.SqlFragment} WHERE {whereClause.SqlFragment}";

    // Transient concurrency errors (40P01 deadlock / 40001 serialization_failure) are expected when the
    // collective UPDATE overlaps per-row projection writes or another pod's collective UPDATE. Retry in-line
    // with jittered backoff so contention clears here rather than bubbling a failure up to the __collective__
    // sink's attempt counter — a deadlock is transient, not poison. Each attempt opens a fresh connection
    // (a rolled-back transaction leaves its connection unusable), which is why the open+execute is inside the
    // retried delegate.
    return await PostgresDeadlockRetry.ExecuteAsync(
      () => _executeUpdateAsync(connectionFactory, sql, setClause.Parameters, whereClause.Parameters, options, cancellationToken),
      maxAttempts: 5,
      cancellationToken: cancellationToken).ConfigureAwait(false);
  }

  private static async Task<int> _executeUpdateAsync(
      IDbConnectionFactory connectionFactory,
      string sql,
      IReadOnlyDictionary<string, object?> setParameters,
      IReadOnlyDictionary<string, object?> whereParameters,
      CollectiveApplyOptions options,
      CancellationToken cancellationToken) {
    var rawConnection = await connectionFactory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
    await using var connection = (DbConnection)rawConnection;
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    // Bound the apply by a server-side statement_timeout when configured: SET LOCAL inside a transaction is
    // the only form that survives PgBouncer transaction pooling, so a runaway UPDATE is cancelled by Postgres
    // rather than left as a zombie when the client gives up.
    if (options.StatementTimeoutSeconds is int secs && secs > 0) {
      await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
      await using (var setCmd = connection.CreateCommand()) {
        setCmd.Transaction = tx;
        setCmd.CommandText = "SET LOCAL statement_timeout = " + (secs * 1000).ToString(CultureInfo.InvariantCulture);
        await setCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      }
      await using var txCmd = connection.CreateCommand();
      txCmd.Transaction = tx;
      txCmd.CommandText = sql;
      _addParameters(txCmd, setParameters);
      _addParameters(txCmd, whereParameters);
      var txAffected = await txCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
      return txAffected;
    }

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    _addParameters(cmd, setParameters);
    _addParameters(cmd, whereParameters);
    return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  private static void _addParameters(DbCommand cmd, IReadOnlyDictionary<string, object?> parameters) {
    foreach (var (name, value) in parameters) {
      var p = cmd.CreateParameter();
      p.ParameterName = name;
      p.Value = value ?? (object)DBNull.Value;
      cmd.Parameters.Add(p);
    }
  }
}
