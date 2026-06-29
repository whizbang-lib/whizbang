using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

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
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(handlerInstance);
    ArgumentNullException.ThrowIfNull(evt);
    ArgumentNullException.ThrowIfNull(resolver);
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

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

    if (entry.Invoker(handlerInstance, evt) is not ICollectiveSpec<TModel> spec) {
      throw new InvalidOperationException(
        $"Handler {entry.HandlerType.FullName}.{entry.MethodName} returned null or a non-{nameof(ICollectiveSpec<TModel>)}<{typeof(TModel).Name}> instance.");
    }

    var setClause = DapperCollectiveSpecCompiler<TModel>.Compile(spec, _jsonOptions, parameterPrefix: "set");
    var scopeFilter = resolver.ScopeFilter<TModel>(evt.Scope);
    var whereClause = DapperCollectiveScopeFilterCompiler<TModel>.Compile(scopeFilter, parameterPrefix: "where");

    var sql = $"UPDATE {tableName} SET {setClause.SqlFragment} WHERE {whereClause.SqlFragment}";

    var rawConnection = await connectionFactory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
    await using var connection = (DbConnection)rawConnection;
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    _addParameters(cmd, setClause.Parameters);
    _addParameters(cmd, whereClause.Parameters);
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
