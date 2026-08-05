using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// Default <see cref="ICollectiveReplayApplier"/>. Makes collective mutations survive a perspective
/// replay/rebuild by folding the persisted collective events back into each stream's per-row replay — the
/// rebuilder itself only replays a perspective's own <c>Apply()</c> events and never runs the set-based
/// collective SQL path, so without this the mutation would be lost on rebuild.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Interleave.</strong> Each collective event is its own single-event stream. During a rebuild this
/// loads the tenant's collective events for the model being rebuilt and merges them into the stream's event
/// list, then the runner's existing <c>OrderByMessageId</c> places them chronologically among the per-stream
/// events — exactly where they were applied live. Tenant scoping is essential: a collective for global template
/// G in tenant A must never fold into tenant B's row for the same G.
/// </para>
/// <para>
/// <strong>Apply.</strong> For each matching <c>[CollectiveApplyFor]</c> entry it invokes the handler to get the
/// spec and hands it to the per-model <see cref="ICollectiveInMemoryExecutor"/>, which evaluates the
/// self-referential <c>Where</c> and applies the setters to the one in-memory row. The <see cref="ICollectiveQuery"/>
/// it passes throws on use — replay-safe specs never call it (guaranteed by <c>WHIZ106</c>).
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CollectiveReplayRebuildIntegrationTests.cs:Rebuild_FoldsCollectiveEvent_OnlyIntoTheTargetRowAsync</tests>
public sealed class CollectiveReplayApplier : ICollectiveReplayApplier {
  private readonly IReadOnlyList<CollectiveApplyEntry> _entries;
  private readonly IServiceProvider _services;
  private readonly IEventStore _eventStore;
  private readonly IEventStoreQuery _eventStoreQuery;
  private readonly IReadOnlyList<ICollectiveInMemoryExecutor> _executors;

  /// <summary>Creates a replay applier.</summary>
  /// <param name="entries">The consumer's <c>CollectiveApplyRegistry.Entries</c>.</param>
  /// <param name="services">For resolving handler instances by <see cref="CollectiveApplyEntry.HandlerType"/>.</param>
  /// <param name="eventStore">Reads the collective events (deserialized) by stream id.</param>
  /// <param name="eventStoreQuery">Finds the collective event stream ids for a tenant.</param>
  /// <param name="executors">The per-model in-memory executors (one per <c>TModel</c>).</param>
  public CollectiveReplayApplier(
      IReadOnlyList<CollectiveApplyEntry> entries,
      IServiceProvider services,
      IEventStore eventStore,
      IEventStoreQuery eventStoreQuery,
      IReadOnlyList<ICollectiveInMemoryExecutor> executors) {
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(eventStore);
    ArgumentNullException.ThrowIfNull(eventStoreQuery);
    ArgumentNullException.ThrowIfNull(executors);
    _entries = entries;
    _services = services;
    _eventStore = eventStore;
    _eventStoreQuery = eventStoreQuery;
    _executors = executors;
  }

  /// <inheritdoc/>
  public async Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveForReplayAsync(
      Type modelType,
      IReadOnlyList<MessageEnvelope<IEvent>> streamEvents,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(modelType);
    ArgumentNullException.ThrowIfNull(streamEvents);

    // Only fold collectives during a re-derivation (rebuild/rewind). Live drain applies them once via the
    // set-based sink path; folding here too would double-apply.
    var mode = ProcessingModeAccessor.Current;
    if (mode is not (ProcessingMode.Rebuild or ProcessingMode.Replay) || streamEvents.Count == 0) {
      return streamEvents;
    }

    var collectiveTypes = _entries
      .Where(e => e.ModelType == modelType)
      .Select(e => e.EventType)
      .Distinct()
      .ToList();
    if (collectiveTypes.Count == 0) {
      return streamEvents;
    }

    var tenantId = streamEvents[0].GetCurrentScope()?.Scope?.TenantId;
    // The event store persists EventType via TypeNameFormatter ("Namespace.TypeName, AssemblyName"), not
    // Type.FullName — match that format so the filter finds the collective streams.
    var typeNames = collectiveTypes.Select(TypeNameFormatter.Format).ToList();

    var streamIds = await _toListAsync(
      _eventStoreQuery.Query
        .Where(r => typeNames.Contains(r.EventType) && r.Scope!.TenantId == tenantId)
        .Select(r => r.StreamId)
        .Distinct(),
      cancellationToken).ConfigureAwait(false);
    if (streamIds.Count == 0) {
      return streamEvents;
    }

    var merged = new List<MessageEnvelope<IEvent>>(streamEvents);
    foreach (var streamId in streamIds) {
      await foreach (var envelope in _eventStore
          .ReadPolymorphicAsync(streamId, null, collectiveTypes, cancellationToken)
          .ConfigureAwait(false)) {
        if (envelope.Payload is ICollectiveEvent) {
          merged.Add(envelope);
        }
      }
    }
    return merged.OrderByMessageId().ToList();
  }

  /// <inheritdoc/>
  public object ApplyInMemory(Type modelType, object currentModel, Guid streamId, IEvent collectiveEvent) {
    ArgumentNullException.ThrowIfNull(modelType);
    ArgumentNullException.ThrowIfNull(currentModel);
    ArgumentNullException.ThrowIfNull(collectiveEvent);

    if (collectiveEvent is not ICollectiveEvent evt) {
      return currentModel;
    }

    var eventType = collectiveEvent.GetType();
    var executor = _resolveExecutor(modelType);
    var model = currentModel;

    // Every [CollectiveApplyFor] entry for this (event, model) runs — mirrors the live dispatcher, which fans
    // out to all matching entries (e.g. two applies on one model).
    foreach (var entry in _entries) {
      if (entry.ModelType != modelType || entry.EventType != eventType) {
        continue;
      }
      var handler = _services.GetRequiredService(entry.HandlerType);
      var spec = entry.Invoker(handler, evt, _NoReplayQuery.Instance);
      model = executor.ApplyToRow(spec, model, streamId);
    }
    return model;
  }

  private ICollectiveInMemoryExecutor _resolveExecutor(Type modelType) {
    foreach (var e in _executors) {
      if (e.ModelType == modelType) {
        return e;
      }
    }
    throw new InvalidOperationException(
      $"No ICollectiveInMemoryExecutor registered for ModelType='{modelType.FullName}'. " +
      $"AddCollectiveExecutorEFCore<{modelType.Name}>()/AddCollectiveExecutorDapper<{modelType.Name}>() registers it " +
      "alongside the SQL executor — required so this model's collective events survive a perspective rebuild.");
  }

  // Core's rebuilder uses the same shape (its ToListAsync helper is internal to Core); replicate it so the
  // event-store IQueryable materializes for both the EF (IAsyncEnumerable) and Dapper (in-memory) providers.
  private static async Task<List<T>> _toListAsync<T>(IQueryable<T> source, CancellationToken ct) {
    var list = new List<T>();
    if (source is IAsyncEnumerable<T> asyncEnumerable) {
      await foreach (var item in asyncEnumerable.WithCancellation(ct).ConfigureAwait(false)) {
        list.Add(item);
      }
    } else {
      list.AddRange(source);
    }
    return list;
  }

  // A collective apply reaching replay is guaranteed self-referential by WHIZ106, so it never queries siblings.
  // If one does, fail loudly rather than silently produce a wrong rebuild.
  private sealed class _NoReplayQuery : ICollectiveQuery {
    public static readonly _NoReplayQuery Instance = new();
    public IQueryable<PerspectiveRow<TOther>> Of<TOther>() where TOther : class =>
      throw new NotSupportedException(
        "A collective apply reached the in-memory replay path but called ICollectiveQuery.Of<>() — an apply-time " +
        "cross-perspective query. Such specs are not replayable (the WHIZ106 analyzer should have failed the build). " +
        "Resolve the cross-query before event creation and carry the result on the event.");
  }
}
