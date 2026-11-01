// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// <strong>Interleave.</strong> A collective event is its own single-event stream unless it carries an ordering
/// key, in which case it shares its key's stream. During a rebuild this loads the tenant's collective events for
/// the model being rebuilt and merges them into the stream's event list. The stream's own events keep the order the
/// runner gave them (its replay order); each collective goes in before the first of them it was committed before, by
/// commit sequence when both carry one and by message id otherwise. The collectives are taken in message-id order,
/// and those sharing a stream are put in commit order among themselves, the order the live sink applied them in. Tenant scoping is essential: a collective for global template
/// G in tenant A must never fold into tenant B's row for the same G.
/// </para>
/// <para>
/// <strong>Archived collectives.</strong> A collective stream archived whole has no row in the hot store. When the
/// driver hands the applier its database, the tenant's archived streams of every registered collective type are listed
/// from the stream lifecycle ledger as well, and read through the event store's archive seam like the hot ones (#1932).
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
  private readonly Func<IReadOnlyList<string>, string?, CancellationToken, Task<IReadOnlyList<Guid>>>? _archivedStreams;
  private readonly IReadOnlyList<string> _collectiveStreamTypes;

  /// <summary>Creates a replay applier that finds collective streams through the hot event store only.</summary>
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
      IReadOnlyList<ICollectiveInMemoryExecutor> executors)
    : this(entries, services, eventStore, eventStoreQuery, executors, archivedStreams: null) {
  }

  /// <summary>
  /// Creates a replay applier that also finds the collective streams archived whole (#1932). Such a stream has no hot
  /// row, so it is listed from the stream lifecycle ledger in <paramref name="archive"/>'s database and read through
  /// <paramref name="eventStore"/>, whose archive seam serves its events. Both Postgres drivers register the applier
  /// this way.
  /// </summary>
  /// <param name="entries">The consumer's <c>CollectiveApplyRegistry.Entries</c>.</param>
  /// <param name="services">For resolving handler instances by <see cref="CollectiveApplyEntry.HandlerType"/>.</param>
  /// <param name="eventStore">Reads the collective events (deserialized) by stream id, archived ones included.</param>
  /// <param name="eventStoreQuery">Finds the hot collective event stream ids for a tenant.</param>
  /// <param name="executors">The per-model in-memory executors (one per <c>TModel</c>).</param>
  /// <param name="archive">The service's database, whose ledger lists the archived streams; null finds the hot
  /// streams only.</param>
  /// <docs>fundamentals/messaging/collective-events</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CollectiveReplayRebuildIntegrationTests.cs:Rebuild_FoldsACollectiveWhoseStreamIsArchivedWhole_ReadingItThroughTheArchiveAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperArchivedCollectiveReplayTests.cs:AddCollectiveEventsDapper_TheReplayReadsTheTenantsArchivedCollectiveStreams_ListedFromTheLedgerAsync</tests>
  public CollectiveReplayApplier(
      IReadOnlyList<CollectiveApplyEntry> entries,
      IServiceProvider services,
      IEventStore eventStore,
      IEventStoreQuery eventStoreQuery,
      IReadOnlyList<ICollectiveInMemoryExecutor> executors,
      PostgresArchiveConnection? archive)
    : this(entries, services, eventStore, eventStoreQuery, executors,
        archivedStreams: archive is null
          ? null
          : (streamTypes, tenant, ct) => ArchivedCollectiveStreams.ListAsync(archive, streamTypes, tenant, ct)) {
  }

  /// <summary>Creates a replay applier whose archived collective streams come from <paramref name="archivedStreams"/>.</summary>
  /// <param name="entries">The consumer's <c>CollectiveApplyRegistry.Entries</c>.</param>
  /// <param name="services">For resolving handler instances by <see cref="CollectiveApplyEntry.HandlerType"/>.</param>
  /// <param name="eventStore">Reads the collective events (deserialized) by stream id.</param>
  /// <param name="eventStoreQuery">Finds the hot collective event stream ids for a tenant.</param>
  /// <param name="executors">The per-model in-memory executors (one per <c>TModel</c>).</param>
  /// <param name="archivedStreams">Lists the archived streams of the given stream types in a tenant; null lists none.</param>
  internal CollectiveReplayApplier(
      IReadOnlyList<CollectiveApplyEntry> entries,
      IServiceProvider services,
      IEventStore eventStore,
      IEventStoreQuery eventStoreQuery,
      IReadOnlyList<ICollectiveInMemoryExecutor> executors,
      Func<IReadOnlyList<string>, string?, CancellationToken, Task<IReadOnlyList<Guid>>>? archivedStreams) {
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
    _archivedStreams = archivedStreams;
    // Every registered collective type, not only one model's: collectives sharing an ordering key share a stream, and
    // the ledger records a stream's type as its first event's, which can be another model's collective. A registered
    // collective's type is a concrete type a generated registration names with typeof, so it always has a full name.
    _collectiveStreamTypes = [.. entries.Select(e => TypeNameFormatter.FormatClrTypeName(e.EventType)).Distinct(StringComparer.Ordinal)];
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

    var tenantId = streamEvents[0].GetCurrentScope()?.Scope.TenantId;
    // The event store persists EventType via TypeNameFormatter ("Namespace.TypeName, AssemblyName"), not
    // Type.FullName — match that format so the filter finds the collective streams.
    var typeNames = collectiveTypes.ConvertAll(TypeNameFormatter.Format);

    // Each collective's stream and commit position: the stream says which collectives share an ordering key, the
    // position says the order the live sink applied them in.
    var positions = await _toListAsync(
      CollectivePositions(_eventStoreQuery.Query, typeNames, tenantId),
      cancellationToken).ConfigureAwait(false);
    var streamIds = positions.Select(p => p.StreamId).Distinct().ToList();
    if (_archivedStreams is not null) {
      // A collective stream archived whole has no hot row; the ledger lists it, and the read below goes through the
      // archive. A stream the hot store already named is read once.
      var named = new HashSet<Guid>(streamIds);
      streamIds.AddRange((await _archivedStreams(_collectiveStreamTypes, tenantId, cancellationToken).ConfigureAwait(false))
        .Where(named.Add));
    }
    if (streamIds.Count == 0) {
      return streamEvents;
    }

    var allCollectives = new List<MessageEnvelope<IEvent>>();
    var byStream = new List<List<MessageEnvelope<IEvent>>>();
    foreach (var streamId in streamIds) {
      var collectives = new List<MessageEnvelope<IEvent>>();
      await foreach (var envelope in _eventStore
          .ReadPolymorphicAsync(streamId, null, collectiveTypes, cancellationToken)
          .Where(e => e.Payload is ICollectiveEvent)
          .ConfigureAwait(false)) {
        collectives.Add(envelope);
      }
      allCollectives.AddRange(collectives);
      byStream.Add(collectives);
    }

    var orderedCollectives = allCollectives.OrderByMessageId().ToList();
    _putSharedStreamsInCommitOrder(orderedCollectives, byStream, positions);
    return _mergeIntoStreamOrder(streamEvents, orderedCollectives);
  }

  /// <summary>
  /// Merges the collectives into the stream's own events without reordering the stream's events: the runner hands
  /// them over already in replay order (origin, then commit, then id), and re-sorting the whole list by id folded a
  /// stream whose first event carried its highest id with that event last, so the rebuild's write carried a stale
  /// commit sequence and the store's ordering guard refused it (#1346). A collective goes before the first stream
  /// event it precedes: by commit sequence when both carry one, by message id otherwise.
  /// </summary>
  private static List<MessageEnvelope<IEvent>> _mergeIntoStreamOrder(
      IReadOnlyList<MessageEnvelope<IEvent>> streamEvents, List<MessageEnvelope<IEvent>> collectives) {
    var merged = new List<MessageEnvelope<IEvent>>(streamEvents.Count + collectives.Count);
    var next = 0;
    foreach (var streamEvent in streamEvents) {
      while (next < collectives.Count && _precedes(collectives[next], streamEvent)) {
        merged.Add(collectives[next++]);
      }
      merged.Add(streamEvent);
    }
    merged.AddRange(collectives.Skip(next));
    return merged;
  }

  /// <summary>Whether <paramref name="collective"/> was committed before <paramref name="streamEvent"/>.</summary>
  private static bool _precedes(MessageEnvelope<IEvent> collective, MessageEnvelope<IEvent> streamEvent) =>
    collective.LocalCommitSequence is long collectiveSequence && streamEvent.LocalCommitSequence is long streamSequence
      ? collectiveSequence < streamSequence
      : collective.MessageId.Value.CompareTo(streamEvent.MessageId.Value) < 0;

  /// <summary>
  /// Collectives that share an ordering key share a stream, and the live sink applied that stream in commit order
  /// (<c>wh_collective_sink_queue</c>: <c>commit_sequence</c>, an unstamped event last, <c>event_id</c> breaking the
  /// tie). An event id is minted before commit, so two of them can have ids in the opposite order; left in id order
  /// the replay would fold the earlier commit last and end where live did not. They change places among themselves
  /// only, in the slots their ids gave them, so every other event keeps its place in the fold.
  /// </summary>
  private static void _putSharedStreamsInCommitOrder(
      List<MessageEnvelope<IEvent>> ordered, List<List<MessageEnvelope<IEvent>>> byStream, List<CollectivePosition> positions) {
    var commitSequence = new Dictionary<Guid, long?>();
    foreach (var position in positions) {
      commitSequence.TryAdd(position.EventId, position.CommitSequence);
    }

    foreach (var collectives in byStream.Where(c => c.Count > 1)) {
      var members = new HashSet<Guid>(collectives.Select(e => e.MessageId.Value));
      var slots = new List<int>();
      for (var i = 0; i < ordered.Count; i++) {
        if (members.Contains(ordered[i].MessageId.Value)) {
          slots.Add(i);
        }
      }
      var inCommitOrder = collectives
        .OrderBy(e => commitSequence.GetValueOrDefault(e.MessageId.Value) is null)
        .ThenBy(e => commitSequence.GetValueOrDefault(e.MessageId.Value))
        // Postgres orders a uuid by its text; .NET's Guid order differs, so the tie-break compares the text too.
        .ThenBy(e => e.MessageId.Value.ToString(), StringComparer.Ordinal)
        .ToList();
      for (var i = 0; i < slots.Count; i++) {
        ordered[slots[i]] = inCommitOrder[i];
      }
    }
  }

  /// <summary>Where one collective sits: its stream, its id and its commit sequence.</summary>
  internal sealed record CollectivePosition(Guid StreamId, Guid EventId, long? CommitSequence);

  /// <summary>
  /// The event-store read that finds every collective of <paramref name="typeNames"/> in one tenant, and where each
  /// one sits.
  /// </summary>
  /// <remarks>
  /// A provider translates <c>Scope.TenantId</c> to <c>scope ->> 't'</c> with the key as a literal, which is the
  /// expression <c>idx_event_store_type_tenant</c> is built on, and the type list to <c>event_type = ANY(...)</c>,
  /// the index's leading key. A key bound as a parameter could never match that index, so the read would scan
  /// every row of the requested types. Kept as its own member so the emitted SQL and its plan can be pinned.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectivePositionQueryPlanTests.cs:CollectivePositions_SendsTheTenantKeyAsALiteralAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectivePositionQueryPlanTests.cs:CollectivePositions_OnALargeStore_ReadsTheTypeAndTenantIndexAsync</tests>
  internal static IQueryable<CollectivePosition> CollectivePositions(
      IQueryable<EventStoreRecord> events, List<string> typeNames, string? tenantId) =>
    events
      .Where(r => typeNames.Contains(r.EventType) && r.Scope!.TenantId == tenantId)
      .Select(r => new CollectivePosition(r.StreamId, r.Id, r.CommitSequence));

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
      var spec = entry.Invoker(handler, evt, NoReplayQuery.Instance);
      model = executor.ApplyToRow(spec, model, streamId);
    }
    return model;
  }

  /// <inheritdoc/>
  public IReadOnlyList<string> CollectiveEventTypeNamesFor(string modelTypeName) {
    ArgumentNullException.ThrowIfNull(modelTypeName);
    var name = modelTypeName.StartsWith("global::", StringComparison.Ordinal) ? modelTypeName["global::".Length..] : modelTypeName;
    // A registered collective's model is the concrete model type a generated registration names with
    // typeof, so it always has a CLR full name; there is no unnamed model to skip.
    return [.. _entries
      .Where(e => string.Equals(TypeNameFormatter.FormatClrTypeName(e.ModelType).Replace('+', '.'), name, StringComparison.Ordinal))
      .Select(e => TypeNameFormatter.Format(e.EventType))
      .Distinct(StringComparer.Ordinal)];
  }

  private ICollectiveInMemoryExecutor _resolveExecutor(Type modelType) {
    foreach (var e in _executors) {
      if (e.ModelType == modelType) {
        return e;
      }
    }
    throw new InvalidOperationException(
      $"No ICollectiveInMemoryExecutor registered for ModelType='{TypeNameFormatter.DisplayName(modelType)}'. " +
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
  private sealed class NoReplayQuery : ICollectiveQuery {
    public static readonly NoReplayQuery Instance = new();
    public IQueryable<PerspectiveRow<TOther>> Of<TOther>() where TOther : class =>
      throw new NotSupportedException(
        "A collective apply reached the in-memory replay path but called ICollectiveQuery.Of<>() — an apply-time " +
        "cross-perspective query. Such specs are not replayable (the WHIZ106 analyzer should have failed the build). " +
        "Resolve the cross-query before event creation and carry the result on the event.");
  }
}
