using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// EF Core driver implementation of <see cref="ICollectiveEventExecutor"/>.
/// Closes over <typeparamref name="TModel"/> at construction time and
/// delegates to the generic
/// <see cref="CollectiveEventApplier{TModel}.ApplyAsync"/>. Consumers
/// register one instance per <c>TModel</c> in DI; the worker filters
/// the resulting <see cref="System.Collections.Generic.IEnumerable{T}"/>
/// by <see cref="ModelType"/> at dispatch time.
/// </summary>
/// <typeparam name="TModel">The perspective model this executor mutates.</typeparam>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveEventExecutorTests.cs:ModelType_ReportsTheClosedGenericArgumentAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveEventExecutorTests.cs:ApplyAsync_NonDbContextSession_ThrowsArgumentExceptionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveEventExecutorTests.cs:ApplyAsync_NullDbContext_ThrowsArgumentNullAsync</tests>
public sealed class EFCoreCollectiveEventExecutor<TModel> : ICollectiveEventExecutor
    where TModel : class {

  private readonly CollectiveApplyOptions _options;
  private readonly CollectiveApplyHookRegistry? _hookRegistry;

  /// <summary>Creates the executor with the apply policy (batching + statement_timeout) and the collective
  /// apply-hook registry. DI supplies both; the parameterless defaults are used by tests (a null registry falls
  /// back to the framework defaults, i.e. the whizbang.timestamps stamping).</summary>
  public EFCoreCollectiveEventExecutor(
      CollectiveApplyOptions? options = null, CollectiveApplyHookRegistry? hookRegistry = null) {
    _options = options ?? CollectiveApplyOptions.Default;
    _hookRegistry = hookRegistry;
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
      Func<CancellationToken, ValueTask>? onBatchApplied = null,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(dbContextOrSession);

    if (dbContextOrSession is not DbContext dbContext) {
      throw new ArgumentException(
        $"EFCoreCollectiveEventExecutor<{typeof(TModel).Name}> requires a DbContext but received '{dbContextOrSession.GetType().FullName}'. The worker dispatch routed an EF executor at a non-EF session — driver registration is wrong.",
        nameof(dbContextOrSession));
    }

    return CollectiveEventApplier<TModel>.ApplyAsync(
      entry, handlerInstance, evt, resolver, dbContext,
      collectiveEventId, _options, _hookRegistry, onBatchApplied, cancellationToken);
  }
}
