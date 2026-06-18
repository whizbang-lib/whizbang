using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Default <see cref="ICollectiveDispatcher"/>. Composes the four
/// moving parts from Slices 4–7a + 7b's executor layer into one call:
/// </summary>
/// <list type="number">
///   <item><description>Find the right <see cref="ICollectiveScopeResolver"/> by <see cref="ICollectiveScope.ScopeKind"/>. A missing resolver for the event's scope kind is a configuration bug — throws <see cref="InvalidOperationException"/>.</description></item>
///   <item><description>Find every <see cref="CollectiveApplyEntry"/> whose <see cref="CollectiveApplyEntry.EventType"/> matches the event's runtime type. Zero matches is a no-op (producers may emit events before a consumer perspective is deployed).</description></item>
///   <item><description>For each entry: find the <see cref="ICollectiveEventExecutor"/> by <see cref="ICollectiveEventExecutor.ModelType"/> (missing executor is a configuration bug — throws), resolve the handler instance from <see cref="IServiceProvider"/>, and call <see cref="ICollectiveEventExecutor.ApplyAsync"/>.</description></item>
///   <item><description>Aggregate the affected-row counts into a single <see cref="CollectiveDispatchResult"/>.</description></item>
/// </list>
/// <remarks>
/// <para>
/// Constructor-injectable. Wire up in <c>IServiceCollection</c>:
/// </para>
/// <code>
/// services.AddSingleton&lt;ICollectiveDispatcher&gt;(sp =&gt; new CollectiveDispatcher(
///   sp,
///   CollectiveApplyRegistry.Entries,
///   sp.GetServices&lt;ICollectiveScopeResolver&gt;().ToList(),
///   sp.GetServices&lt;ICollectiveEventExecutor&gt;().ToList()));
/// </code>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveDispatcherTests.cs:DispatchAsync_OneEntry_InvokesExecutorOnceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveDispatcherTests.cs:DispatchAsync_TwoEntriesSameEventDifferentModels_FansOutAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveDispatcherTests.cs:DispatchAsync_NoMatchingEntry_ReturnsZeroAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveDispatcherTests.cs:DispatchAsync_NoResolverForScopeKind_ThrowsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveDispatcherTests.cs:DispatchAsync_NoExecutorForModelType_ThrowsAsync</tests>
public sealed class CollectiveDispatcher : ICollectiveDispatcher {
  private readonly IServiceProvider _services;
  private readonly IReadOnlyList<CollectiveApplyEntry> _entries;
  private readonly IReadOnlyList<ICollectiveScopeResolver> _resolvers;
  private readonly IReadOnlyList<ICollectiveEventExecutor> _executors;

  /// <summary>
  /// Creates a dispatcher.
  /// </summary>
  /// <param name="services">For resolving handler instances by <see cref="CollectiveApplyEntry.HandlerType"/>.</param>
  /// <param name="entries">Compile-time registry from <c>CollectiveApplyRegistry.Entries</c>.</param>
  /// <param name="resolvers">All <see cref="ICollectiveScopeResolver"/> instances registered in DI.</param>
  /// <param name="executors">All <see cref="ICollectiveEventExecutor"/> instances registered in DI (one per <c>TModel</c>).</param>
  public CollectiveDispatcher(
      IServiceProvider services,
      IReadOnlyList<CollectiveApplyEntry> entries,
      IReadOnlyList<ICollectiveScopeResolver> resolvers,
      IReadOnlyList<ICollectiveEventExecutor> executors) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(resolvers);
    ArgumentNullException.ThrowIfNull(executors);
    _services = services;
    _entries = entries;
    _resolvers = resolvers;
    _executors = executors;
  }

  /// <inheritdoc />
  public async Task<CollectiveDispatchResult> DispatchAsync(
      ICollectiveEvent evt,
      Guid collectiveEventId,
      object dbContextOrSession,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(evt);
    ArgumentNullException.ThrowIfNull(dbContextOrSession);

    var eventType = evt.GetType();
    var matchingEntries = _entries.Where(e => e.EventType == eventType).ToList();
    if (matchingEntries.Count == 0) {
      // No perspective subscribed — not an error.
      return new CollectiveDispatchResult(HandlerCount: 0, AffectedRowCount: 0);
    }

    // Resolver lookup happens once per dispatch (every entry that
    // matched the event type shares the event's scope kind).
    var resolver = _resolveResolver(evt.Scope.ScopeKind);

    var totalAffectedRows = 0;
    foreach (var entry in matchingEntries) {
      var executor = _resolveExecutor(entry.ModelType);
      var handler = _services.GetRequiredService(entry.HandlerType);
      var affected = await executor.ApplyAsync(
        entry, handler, evt, resolver, dbContextOrSession, collectiveEventId, cancellationToken)
        .ConfigureAwait(false);
      totalAffectedRows += affected;
    }

    return new CollectiveDispatchResult(
      HandlerCount: matchingEntries.Count,
      AffectedRowCount: totalAffectedRows);
  }

  private ICollectiveScopeResolver _resolveResolver(string scopeKind) {
    foreach (var r in _resolvers) {
      if (r.ScopeKind == scopeKind) {
        return r;
      }
    }
    throw new InvalidOperationException(
      $"No ICollectiveScopeResolver registered for ScopeKind='{scopeKind}'. Register one in DI for this scope kind, or stop emitting collective events with this scope.");
  }

  private ICollectiveEventExecutor _resolveExecutor(Type modelType) {
    foreach (var e in _executors) {
      if (e.ModelType == modelType) {
        return e;
      }
    }
    throw new InvalidOperationException(
      $"No ICollectiveEventExecutor registered for ModelType='{modelType.FullName}'. Register an EFCoreCollectiveEventExecutor<{modelType.Name}> (or driver-equivalent) for each TModel that has a [CollectiveApplyFor] handler.");
  }
}
