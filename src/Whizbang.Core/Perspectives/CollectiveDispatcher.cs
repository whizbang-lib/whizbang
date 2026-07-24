using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

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
///   sp.GetServices&lt;ICollectiveEventExecutor&gt;().ToList(),
///   sp.GetService&lt;EventCategoryMetrics&gt;()));
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
  private readonly EventCategoryMetrics? _metrics;

  /// <summary>
  /// Creates a dispatcher.
  /// </summary>
  /// <param name="services">For resolving handler instances by <see cref="CollectiveApplyEntry.HandlerType"/>.</param>
  /// <param name="entries">Compile-time registry from <c>CollectiveApplyRegistry.Entries</c>.</param>
  /// <param name="resolvers">All <see cref="ICollectiveScopeResolver"/> instances registered in DI.</param>
  /// <param name="executors">All <see cref="ICollectiveEventExecutor"/> instances registered in DI (one per <c>TModel</c>).</param>
  /// <param name="metrics">Optional cross-cutting metrics. When null, recording is a no-op.</param>
  public CollectiveDispatcher(
      IServiceProvider services,
      IReadOnlyList<CollectiveApplyEntry> entries,
      IReadOnlyList<ICollectiveScopeResolver> resolvers,
      IReadOnlyList<ICollectiveEventExecutor> executors,
      EventCategoryMetrics? metrics = null) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(resolvers);
    ArgumentNullException.ThrowIfNull(executors);
    _services = services;
    _entries = entries;
    _resolvers = resolvers;
    _executors = executors;
    _metrics = metrics;
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
    var eventTypeName = eventType.FullName ?? eventType.Name;
    var eventNamespace = eventType.Namespace ?? string.Empty;

    // OTel span for the whole fan-out: this is what surfaces a single slow collective event in a trace
    // (the metrics tag event_type/namespace too, but a span shows the duration + causal parent). Tagged the
    // same way as the metrics so trace and metric views line up. Child spans (per-model apply, per-batch) are
    // created by the driver adapters and nest under this via Activity.Current.
    using var activity = WhizbangActivitySource.Tracing.StartActivity("Collective Dispatch", ActivityKind.Internal);
    if (activity is not null) {
      activity.SetTag(TAG_EVENT_TYPE, eventTypeName);
      activity.SetTag(TAG_EVENT_NAMESPACE, eventNamespace);
      activity.SetTag(TAG_SCOPE_KIND, evt.Scope.ScopeKind);
      activity.SetTag(TAG_EVENT_ID, collectiveEventId);
    }

    var matchingEntries = _entries.Where(e => e.EventType == eventType).ToList();
    if (matchingEntries.Count == 0) {
      activity?.SetTag(TAG_HANDLER_COUNT, 0);
      // No perspective subscribed — not an error. Still increment the
      // dispatched counter so dashboards see the producer activity even
      // when consumers haven't caught up.
      _metrics?.Dispatched.Add(1,
        new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.CATEGORY, EventCategoryMetrics.Categories.COLLECTIVE),
        new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_TYPE, eventTypeName),
        new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_NAMESPACE, eventNamespace),
        new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.SCOPE_KIND, evt.Scope.ScopeKind));
      return new CollectiveDispatchResult(HandlerCount: 0, AffectedRowCount: 0);
    }

    var sw = _metrics is null ? null : Stopwatch.StartNew();

    ICollectiveScopeResolver resolver;
    try {
      // Resolver lookup happens once per dispatch (every entry that
      // matched the event type shares the event's scope kind).
      resolver = _resolveResolver(evt.Scope.ScopeKind);
    } catch (InvalidOperationException) {
      _metrics?.Errors.Add(1,
        new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.CATEGORY, EventCategoryMetrics.Categories.COLLECTIVE),
        new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_TYPE, eventTypeName),
        new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_NAMESPACE, eventNamespace),
        new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.ERROR_CLASS, EventCategoryMetrics.ErrorClasses.RESOLVER_MISSING));
      throw;
    }

    _metrics?.Dispatched.Add(1,
      new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.CATEGORY, EventCategoryMetrics.Categories.COLLECTIVE),
      new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_TYPE, eventTypeName),
      new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_NAMESPACE, eventNamespace),
      new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.SCOPE_KIND, evt.Scope.ScopeKind));

    var totalAffectedRows = 0;
    try {
      foreach (var entry in matchingEntries) {
        ICollectiveEventExecutor executor;
        try {
          executor = _resolveExecutor(entry.ModelType);
        } catch (InvalidOperationException) {
          _metrics?.Errors.Add(1,
            new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.CATEGORY, EventCategoryMetrics.Categories.COLLECTIVE),
            new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_TYPE, eventTypeName),
            new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_NAMESPACE, eventNamespace),
            new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.ERROR_CLASS, EventCategoryMetrics.ErrorClasses.EXECUTOR_MISSING));
          throw;
        }

        var handler = _services.GetRequiredService(entry.HandlerType);
        var affected = await executor.ApplyAsync(
          entry, handler, evt, resolver, dbContextOrSession, collectiveEventId, cancellationToken)
          .ConfigureAwait(false);
        totalAffectedRows += affected;

        _metrics?.Fanout.Record(affected,
          new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.CATEGORY, EventCategoryMetrics.Categories.COLLECTIVE),
          new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_TYPE, eventTypeName),
          new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_NAMESPACE, eventNamespace),
          new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.MODEL_TYPE, entry.ModelType.Name));
      }
    } catch (Exception ex) when (ex is not InvalidOperationException && ex is not OperationCanceledException) {
      activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
      _metrics?.Errors.Add(1,
        new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.CATEGORY, EventCategoryMetrics.Categories.COLLECTIVE),
        new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_TYPE, eventTypeName),
        new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_NAMESPACE, eventNamespace),
        new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.ERROR_CLASS, EventCategoryMetrics.ErrorClasses.SQL_EXCEPTION));
      throw;
    } finally {
      if (sw is not null) {
        sw.Stop();
        _metrics?.DispatchDuration.Record(sw.Elapsed.TotalMilliseconds,
          new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.CATEGORY, EventCategoryMetrics.Categories.COLLECTIVE),
          new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_TYPE, eventTypeName),
          new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_NAMESPACE, eventNamespace));
      }
    }

    if (activity is not null) {
      activity.SetTag(TAG_HANDLER_COUNT, matchingEntries.Count);
      activity.SetTag(TAG_AFFECTED_ROWS, totalAffectedRows);
    }

    return new CollectiveDispatchResult(
      HandlerCount: matchingEntries.Count,
      AffectedRowCount: totalAffectedRows);
  }

  // Span tag keys — mirror the EventCategoryMetrics dimensions so trace + metric views line up.
  private const string TAG_EVENT_TYPE = "whizbang.collective.event_type";
  private const string TAG_EVENT_NAMESPACE = "whizbang.collective.event_namespace";
  private const string TAG_SCOPE_KIND = "whizbang.collective.scope_kind";
  private const string TAG_EVENT_ID = "whizbang.collective.event_id";
  private const string TAG_HANDLER_COUNT = "whizbang.collective.handler_count";
  private const string TAG_AFFECTED_ROWS = "whizbang.collective.affected_rows";

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
