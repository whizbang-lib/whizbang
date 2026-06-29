using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Dapper.Postgres.Collective;

/// <summary>
/// Dapper driver implementation of <see cref="ICollectiveEventExecutor"/>. Closes over
/// <typeparamref name="TModel"/> and the perspective table name at construction, casts the dispatched
/// session to an <see cref="IDbConnectionFactory"/>, and delegates to
/// <see cref="DapperCollectiveEventApplier{TModel}.ApplyAsync"/>. Register one per <c>TModel</c> in DI.
/// </summary>
/// <remarks>
/// The table name is supplied at registration because Dapper has no entity model to derive it from
/// (the EF executor reads it from <c>DbContext.Set&lt;PerspectiveRow&lt;TModel&gt;&gt;()</c>). Pass the
/// generated <c>wh_per_*</c> table name for the model.
/// </remarks>
/// <typeparam name="TModel">The perspective model this executor mutates.</typeparam>
/// <docs>fundamentals/messaging/collective-events</docs>
public sealed class DapperCollectiveEventExecutor<TModel> : ICollectiveEventExecutor
    where TModel : class {
  private readonly string _tableName;

  /// <summary>Create the executor for <typeparamref name="TModel"/> targeting <paramref name="tableName"/>.</summary>
  /// <param name="tableName">The perspective table for the model (e.g. <c>wh_per_job</c>).</param>
  public DapperCollectiveEventExecutor(string tableName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    _tableName = tableName;
  }

  /// <inheritdoc />
  public Type ModelType => typeof(TModel);

  /// <inheritdoc />
  public Task<int> ApplyAsync(
      CollectiveApplyEntry entry,
      object handlerInstance,
      ICollectiveEvent evt,
      ICollectiveScopeResolver resolver,
      object dbContextOrSession,
      Guid collectiveEventId,
      CancellationToken cancellationToken) {

    ArgumentNullException.ThrowIfNull(dbContextOrSession);

    if (dbContextOrSession is not IDbConnectionFactory connectionFactory) {
      throw new ArgumentException(
        $"DapperCollectiveEventExecutor<{typeof(TModel).Name}> requires an IDbConnectionFactory but received '{dbContextOrSession.GetType().FullName}'. The worker dispatch routed a Dapper executor at a non-Dapper session — driver registration is wrong.",
        nameof(dbContextOrSession));
    }

    return DapperCollectiveEventApplier<TModel>.ApplyAsync(
      entry, handlerInstance, evt, resolver, connectionFactory, _tableName, cancellationToken);
  }
}
