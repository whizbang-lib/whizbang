// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Events.System;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Perspectives;

// The runner's log lines are the ones the generated runners wrote before this code moved into Whizbang.Core (#2469),
// call for call, so moving it changes no level, template or guard. Turning them into LoggerMessage delegates is its
// own change.
#pragma warning disable CA1848 // Use LoggerMessage delegates
#pragma warning disable CA1873 // Avoid potentially expensive logging argument evaluation

/// <summary>
/// The runner logic every perspective shares (#2401, #2469): reads, folds, writes, rewinds, rebuilds and snapshots a
/// perspective's rows. Each perspective's generated runner derives from it and supplies only what differs between
/// perspectives (<see cref="RunnerTraits"/>, <see cref="ApplyEventCore"/>, <see cref="CreateEmptyModel"/> and the
/// optional overrides), so a runner does not repeat this code and one copy serves every consumer.
/// </summary>
/// <typeparam name="TPerspective">The perspective class.</typeparam>
/// <typeparam name="TModel">The perspective's model (read model) type.</typeparam>
/// <remarks>
/// <para>
/// Derived only by the runners the perspective runner generator emits: it is public so those runners, compiled into the
/// consumer's assembly, can derive from it, not as an extension point for hand-written runners.
/// </para>
/// <para>
/// Unit of Work Pattern with Pure Functions:
/// - Reads events in UUID7 order (time-ordered)
/// - Applies ALL events to in-memory model using pure Apply() functions
/// - Pure functions: synchronous, deterministic, no I/O or side effects
/// - Saves model + checkpoint ONCE at batch boundary/failure/end
/// - On failure: saves checkpoint at last successful event
/// </para>
/// <para>
/// This ensures:
/// - Deterministic replay (pure functions guarantee same result)
/// - Atomic writes (model + checkpoint saved together)
/// - Partial progress on failure (checkpoint up to last success)
/// - No inline saves during event processing
/// - Compile-time purity enforcement via Roslyn analyzer
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/RunnerBase/RunnerBaseEntryPointTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/RunnerBase/RunnerBaseLiveApplyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/RunnerBase/RunnerBaseReplayTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveRunnerGeneratedSizeTests.cs:ASecondPerspective_AddsOnlyItsOwnRunner_NoRunnerBaseIsGeneratedAsync</tests>
public abstract class PerspectiveRunnerBase<TPerspective, TModel> : IPerspectiveRunner
    where TPerspective : class
    where TModel : class {
  /// <summary>
  /// What a perspective's runner fixes at generation time: the facts the shared logic branches on. Each generated
  /// runner holds one, built from its perspective's declarations.
  /// </summary>
  protected sealed class RunnerTraits {
    /// <summary>The event types the perspective folds, the list every read of its streams asks for.</summary>
    public required global::System.Type[] EventTypes { get; init; }

    /// <summary>
    /// The perspective's CLR type name, which the missing-row probe asks the archived-row policy registry under (#1739).
    /// </summary>
    public required string ArchivedStreamRowsName { get; init; }

    /// <summary>
    /// The model's [InheritScope] OnCreate fields. Defaults to every field (63), so creation records ownership.
    /// </summary>
    public global::Whizbang.Core.Lenses.ScopeFields InheritScopeOnCreate { get; init; } = (global::Whizbang.Core.Lenses.ScopeFields)63;

    /// <summary>The model's [InheritScope] Always fields. Defaults to none (0), so scope is set once, at creation.</summary>
    public global::Whizbang.Core.Lenses.ScopeFields InheritScopeAlways { get; init; }

    /// <summary>
    /// True when the perspective applies at least one [Ephemeral] event (its streams are ephemeral). Gates the rewind
    /// fallback: an ephemeral stream's consumed bodies are reaped, so a full replay-from-zero would read reaped/absent
    /// bodies and silently corrupt the model. Also selects the ephemeral snapshot cadence.
    /// </summary>
    public bool IsEphemeral { get; init; }

    /// <summary>
    /// False for a perspective that applies only ephemeral events and does not opt in with [KeepEphemeralRewindFloor]
    /// (#2295). It keeps no rewind floor: the runner is given no snapshot store, so it writes no snapshot, live or
    /// bootstrapped, and a late event that would need a floor is dropped as an ephemeral one is.
    /// </summary>
    public bool KeepsRewindFloor { get; init; } = true;

    /// <summary>
    /// True for a perspective marked [OverwriteOnly] (#1869), whose every Apply builds the row from its event alone, so a
    /// rewind applies only the stream's last event (_replayLastEventOnlyAsync).
    /// </summary>
    public bool IsOverwriteOnly { get; init; }

    /// <summary>True when the perspective implements IPerspectiveScopeFor and decides the scope an IScopeEvent sets.</summary>
    public bool HasScopeInterface { get; init; }

    /// <summary>
    /// True when the write strips the model's promoted fields in place (a Split class model that cannot be copied), so
    /// its snapshot is serialized before the write (issue #983).
    /// </summary>
    public bool SnapshotsBeforeWrite { get; init; }
  }

  /// <inheritdoc/>
  public Type PerspectiveType => typeof(TPerspective);

  private readonly RunnerTraits _traits;

  /// <summary>
  /// The event types this perspective folds. The resurrection-on-wake probe asks the event store
  /// about these only (issue #696): history of other contracts on a shared stream is not a reaped
  /// row. Same list the polymorphic reads use.
  /// </summary>
  private readonly global::System.Type[] _handledEventTypes;

  /// <summary>
  /// Applies <paramref name="event"/> to <paramref name="currentModel"/> through the interface that lists the event:
  /// the perspective's generated, AOT-compatible switch (no reflection). Returns the model and the action the Apply
  /// asked for.
  /// </summary>
  [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Overridden only by generated C# runners, whose Apply dispatch names the event @event (#2469)")]
  protected abstract (TModel?, global::Whizbang.Core.Perspectives.ModelAction) ApplyEventCore(
      TPerspective perspective,
      TModel? currentModel,
      IEvent @event);

  /// <summary>
  /// Creates an empty model for a new stream.
  /// Initializes the model with the stream ID set to the stream key property.
  /// Direct construction (no Activator, no reflection): the initializer is
  /// computed at generation time from the model's shape, so strongly-typed
  /// stream ids construct through their From(Guid) factory and required
  /// members are explicitly initialized. AOT-safe.
  /// </summary>
  protected abstract TModel CreateEmptyModel(Guid streamId);

  /// <summary>True for an event whose Apply can do nothing but purge the row (#1151). None by default.</summary>
  [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Overridden only by generated C# runners, whose Apply dispatch names the event @event (#2469)")]
  protected virtual bool IsUnconditionalPurge(global::Whizbang.Core.IEvent @event) => false;

  /// <summary>
  /// True for an event whose Apply returns the model, the only kind that can decide the row alone (#1869). An Apply that
  /// returns an action or an ApplyResult can purge, and a stream holding such an event is replayed in full. None by
  /// default.
  /// </summary>
  [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Overridden only by generated C# runners, whose Apply dispatch names the event @event (#2469)")]
  protected virtual bool DecidesTheRowAlone(global::Whizbang.Core.IEvent @event) => false;

  /// <summary>
  /// The physical column values of <paramref name="model"/>, keyed by column name, for a model with [PhysicalField] or
  /// [VectorField] properties; null for a model with none, which is written without them. Vector fields are converted
  /// from float[] to Pgvector.Vector at generation time (no reflection).
  /// </summary>
  protected virtual global::System.Collections.Generic.Dictionary<string, object?>? PhysicalFieldValues(TModel model) => null;

  /// <summary>
  /// The model as its document is written: for a Split model, with its promoted fields stripped (their values live only
  /// in their columns); the model itself for every other one. Called only when <see cref="PhysicalFieldValues"/> is not
  /// null, after the values are taken.
  /// </summary>
  protected virtual TModel StripPhysicalFields(TModel model) => model;

  private readonly IServiceProvider _serviceProvider;
  // The clock the PERF line measures a write with: the host's, else the system's (a stopwatch, as before #2469).
  private readonly TimeProvider _timeProvider;
  private readonly ILogger _logger;
  private readonly IEventStore _eventStore;
  private readonly IPerspectiveStore<TModel> _perspectiveStore;
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IOptionsMonitor<TracingOptions>? _tracingOptions;
  private readonly IPerspectiveSnapshotStore? _snapshotStore;
  private readonly IOptions<PerspectiveSnapshotOptions>? _snapshotOptions;
  // Closes the rewind-vs-live race window where same-pod Apply paths race on
  // wh_per_* row writes. See IPerspectiveApplyCoordinator for the full rationale
  // and the production bulk-import 4-lost-increments incident that motivated it.
  private readonly global::Whizbang.Core.Perspectives.IPerspectiveApplyCoordinator? _applyCoordinator;
  // Optional: folds collective events back into replay/rebuild so their set-based mutations survive a rebuild.
  // Null when no driver registered it (or the model has no [CollectiveApplyFor] handler) - then the runner
  // behaves exactly as before. See ICollectiveReplayApplier.
  private readonly global::Whizbang.Core.Perspectives.ICollectiveReplayApplier? _collectiveReplayApplier;
  // Events folded since each stream's last snapshot, per stream: one runner prepares many pairs of a batch (#2246), and
  // a single count added one stream's events to another's and snapshotted the wrong stream at the wrong time.
  private readonly global::System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _eventsSinceLastSnapshot = new();

  private int _eventsSinceSnapshot(Guid streamId) => _eventsSinceLastSnapshot.GetValueOrDefault(streamId);

  /// <summary>Creates the runner of one perspective, with that perspective's <paramref name="traits"/>.</summary>
  protected PerspectiveRunnerBase(
      RunnerTraits traits,
      IServiceProvider serviceProvider,
      ILogger logger,
      IEventStore eventStore,
      IPerspectiveStore<TModel> perspectiveStore,
      IServiceScopeFactory scopeFactory,
      IOptionsMonitor<TracingOptions>? tracingOptions = null,
      IPerspectiveSnapshotStore? snapshotStore = null,
      IOptions<PerspectiveSnapshotOptions>? snapshotOptions = null,
      global::Whizbang.Core.Perspectives.IPerspectiveApplyCoordinator? applyCoordinator = null,
      global::Whizbang.Core.Perspectives.ICollectiveReplayApplier? collectiveReplayApplier = null) {
    ArgumentNullException.ThrowIfNull(traits);
    _traits = traits;
    _handledEventTypes = traits.EventTypes;
    ArgumentNullException.ThrowIfNull(serviceProvider);
    _serviceProvider = serviceProvider;
    _timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
    ArgumentNullException.ThrowIfNull(eventStore);
    _eventStore = eventStore;
    ArgumentNullException.ThrowIfNull(perspectiveStore);
    _perspectiveStore = perspectiveStore;
    ArgumentNullException.ThrowIfNull(scopeFactory);
    _scopeFactory = scopeFactory;
    _tracingOptions = tracingOptions;
    // #2295: a perspective that keeps no rewind floor takes no snapshots and restores from none.
    // The framework's null default (IsConfigured false) is no store: a rewind asking it for a floor threw, so a service
    // with no snapshot store failed every rewind its drain path ran (#2366).
    _snapshotStore = traits.KeepsRewindFloor && snapshotStore is { IsConfigured: true } ? snapshotStore : null;
    _snapshotOptions = snapshotOptions;
    _applyCoordinator = applyCoordinator;
    _collectiveReplayApplier = collectiveReplayApplier;
  }

  /// <inheritdoc/>
  public async Task<PerspectiveCursorCompletion> RunAsync(
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      CancellationToken cancellationToken = default) {

    var events = await ReadEventsAsync(streamId, lastProcessedEventId, cancellationToken);

    // Delegate to RunWithEventsAsync
    return await RunWithEventsAsync(streamId, perspectiveName, lastProcessedEventId, events!, cancellationToken);
  }

  /// <summary>
  /// The read <see cref="RunAsync"/> makes before it applies: the stream's events after the cursor, of the types this
  /// perspective folds. A set-based batch reads a claimed pair's events through it, then prepares them (#2246).
  /// </summary>
  public async Task<IReadOnlyList<MessageEnvelope<IEvent>>?> ReadEventsAsync(
      Guid streamId,
      Guid? lastProcessedEventId,
      CancellationToken cancellationToken = default) {

    // Build event types list for polymorphic deserialization
    var eventTypes = _handledEventTypes;

    // Read events from event store
    var events = new List<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>>();
    await foreach (var envelope in _eventStore.ReadPolymorphicAsync(
        streamId, lastProcessedEventId, eventTypes, cancellationToken)) {
      events.Add(envelope);
    }
    return events;
  }

  /// <summary>
  /// The events <see cref="ReadEventsAsync"/> would read, selected from a stream's events a claimed set-based batch read
  /// once for all its pairs (#2246): the ones of the types this perspective folds, in the order they came.
  /// </summary>
  public global::System.Collections.Generic.IReadOnlyList<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>>? SelectEvents(
      global::System.Collections.Generic.IReadOnlyList<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>> streamEvents) {
    return [.. streamEvents.Where(envelope => global::System.Array.IndexOf(_handledEventTypes, envelope.Payload.GetType()) >= 0)];
  }

  /// <summary>
  /// Rebuild-only replay that honors re-key upcasters. Reads the physical stream's events
  /// (upcasted on the polymorphic read seam), partitions them by the row each folds onto
  /// (<see cref="global::Whizbang.Core.Perspectives.RebuildRowRouting"/>), and applies each partition onto its own
  /// row via <see cref="RunWithEventsAsync"/>, a re-keyed one via <see cref="RunRekeyedRowAsync"/>. Only an
  /// event an upcaster re-keyed (<see cref="global::Whizbang.Core.Messaging.EventRekeys"/>) leaves
  /// the physical stream; an event whose payload merely names another stream stays on the stream it is
  /// stored on, as live apply and the rewind keep it (#1798). Returns
  /// one completion per target stream. When no upcaster re-keys, every event maps to
  /// <paramref name="physicalStreamId"/> - a single partition that is byte-for-byte the old
  /// rebuild path. The live drain (<see cref="RunAsync"/>/<see cref="RunWithEventsAsync"/>) is
  /// never routed through here.
  /// </summary>
  public async Task<IReadOnlyList<PerspectiveCursorCompletion>> RunRebuildAsync(
      Guid physicalStreamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {

    var eventTypes = _handledEventTypes;

    // Read + upcast the physical stream's events (the decorator applies upcasters on this seam).
    var events = new List<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>>();
    await foreach (var envelope in _eventStore.ReadPolymorphicAsync(
        physicalStreamId, null, eventTypes, cancellationToken)) {
      events.Add(envelope);
    }

    // Every path keys a row by the stream an event is stored on; only a genuine re-key, an upcaster that moved the
    // event's [StreamId] as it was read, routes it elsewhere (#1798). The routing and the merge of a re-keyed row are
    // the shared RebuildRowRouting's, so every runner decides them the same way.
    var partitions = global::Whizbang.Core.Perspectives.RebuildRowRouting.Partition(physicalStreamId, events);

    // A re-keyed row is folded from its own stream and every source re-keyed onto it (#1820). Inside a rebuild run
    // the run collects the sources and folds the row once it has read them all; called on its own, this stream is
    // the only source known.
    var ledger = global::Whizbang.Core.Perspectives.RebuildRekeyLedger.Current;
    var completions = new List<PerspectiveCursorCompletion>(partitions.Count);
    foreach (var partition in partitions) {
      if (!partition.IsRekeyed) {
        completions.Add(await RunWithEventsAsync(partition.Row, perspectiveName, null, partition.Events, cancellationToken));
      } else if (ledger is not null) {
        ledger.Add(partition);
      } else {
        completions.Add(await RunRekeyedRowAsync(partition.Row, perspectiveName, [partition.Events], cancellationToken));
      }
    }

    // No events, or every one re-keyed onto a row the run folds later: a single no-op completion for the physical
    // stream keeps the rebuilder's one-completion-per-stream contract.
    if (completions.Count == 0) {
      return [
        new PerspectiveCursorCompletion {
          StreamId = physicalStreamId,
          PerspectiveName = perspectiveName,
          PerspectiveType = typeof(TPerspective),
          LastEventId = Guid.Empty,
          Status = PerspectiveProcessingStatus.None,
          RowChanged = false,  // no events folded onto a row here, so no row was touched
        }
      ];
    }
    return completions;
  }

  /// <summary>
  /// Rebuilds a stream archived through a prefix without reading the archive (#1931): restores the stream's newest
  /// snapshot and replays the hot events after it, through the rewind's replay. Null when there is no snapshot store, no
  /// snapshot, or one this build cannot read, so the rebuild skips the stream. The newest snapshot is live after a prefix
  /// archive (which refuses to reach it) and, after a NewGeneration append (#1734), when it sits at the archive's last
  /// version; one strictly inside the archive is not, and the stream is skipped.
  /// </summary>
  public async Task<IReadOnlyList<PerspectiveCursorCompletion>?> RunRebuildFromLatestSnapshotAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
    if (_snapshotStore is null) {
      return null;
    }
    // #1963: the newest snapshot with its collectives mark, so the replay folds exactly the collectives past it.
    // #1734: only a live snapshot (no archived event after it) can start a rebuild that reads no archived data.
    var latest = await _snapshotStore.GetLatestSnapshotFloorAsync(streamId, perspectiveName, cancellationToken);
    if (latest?.IsLive != true) {
      return null;
    }
    var snapshotModel = _fromSnapshotJson(latest.SnapshotData);
    if (snapshotModel is null) {
      return null;
    }

    // The replay assumes the apply lock is held, as the rewind's caller holds it.
    await using IAsyncDisposable? __applyLock = _applyCoordinator is null
        ? null
        : await _applyCoordinator.AcquireAsync(streamId, perspectiveName, cancellationToken).ConfigureAwait(false);
    var completion = await _replayCheckedAsync(
        streamId, perspectiveName, snapshotModel, latest.SnapshotData,
        new ReplayFrom(latest.SnapshotEventId, ReadThroughArchiveAfterVersion: null, latest.CollectivesThroughSequence), cancellationToken);
    return [completion];
  }

  /// <summary>
  /// Folds a row that receives re-keyed events (#1820): reads the row's own stream (upcasted), keeps the events no
  /// upcaster moved away, merges them with every re-keyed source in commit order and folds the result from an empty
  /// model, through the same <see cref="RunWithEventsAsync"/> every other path uses.
  /// </summary>
  public async Task<PerspectiveCursorCompletion> RunRekeyedRowAsync(
      Guid rowStreamId,
      string perspectiveName,
      IReadOnlyList<IReadOnlyList<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>>> rekeyedSources,
      CancellationToken cancellationToken = default) {

    var eventTypes = _handledEventTypes;

    var own = new List<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>>();
    await foreach (var envelope in _eventStore.ReadPolymorphicAsync(rowStreamId, null, eventTypes, cancellationToken)) {
      own.Add(envelope);
    }
    var sources = new List<IReadOnlyList<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>>>(rekeyedSources.Count + 1) {
      global::Whizbang.Core.Perspectives.RebuildRowRouting.StaysOn(rowStreamId, own)
    };
    sources.AddRange(rekeyedSources);
    return await RunWithEventsAsync(
        rowStreamId, perspectiveName, null, global::Whizbang.Core.Perspectives.RebuildRowRouting.Merge(sources), cancellationToken);
  }

  /// <inheritdoc/>
  public async Task<PerspectiveCursorCompletion> RunWithEventsAsync(
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      IReadOnlyList<MessageEnvelope<IEvent>> events,
      CancellationToken cancellationToken = default) {

    // Same-pod Apply serialization (rewind-vs-live race fix). The await-using
    // null-pattern is no-op when the coordinator isn't registered - preserves
    // the existing optional-DI contract for hosts that haven't wired Whizbang's
    // core registrations yet.
    await using IAsyncDisposable? __applyLock = _applyCoordinator is null
        ? null
        : await _applyCoordinator.AcquireAsync(streamId, perspectiveName, cancellationToken).ConfigureAwait(false);

    return await _runWithEventsLockedAsync(
        streamId, perspectiveName, lastProcessedEventId, events, firePreLifecycle: true, [], cancellationToken);
  }

  /// <summary>
  /// The per-pair path, ASSUMING the apply lock is already held. Two callers: <see cref="RunWithEventsAsync"/>, which
  /// takes the lock, and <see cref="CompleteAsync"/>, whose caller holds the locks of its whole batch (#2246).
  /// </summary>
  /// <param name="firePreLifecycle">
  /// Whether the pre-perspective stages fire. False when a prepare already fired them for this batch, which hands its
  /// pending detached stages in <paramref name="backgroundTasks"/>.
  /// </param>
  private async Task<PerspectiveCursorCompletion> _runWithEventsLockedAsync(
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      IReadOnlyList<MessageEnvelope<IEvent>> events,
      bool firePreLifecycle,
      List<Task> backgroundTasks,
      CancellationToken cancellationToken) {

    events = _orderForApply(events);

    // Lost-update guard (issue #928). The apply reads the row, folds the batch in memory and writes the
    // whole row back; a writer that commits in between (a collective apply, a second apply of this
    // stream) used to be overwritten silently. The write now lands only on the row version this attempt
    // read, and a store that sees the row moved refuses it with PerspectiveRowConflictException: re-read,
    // re-fold, write again. Bounded, because a row that moves under every attempt is a failure to report,
    // not a loop to spin in. Detached lifecycle tasks one attempt started are awaited by the attempt that
    // completes, so none is left unobserved.
    // <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
    // A conflict is retried until the last attempt, whose conflict the filter logs and declines, so it propagates as
    // thrown (#2469).
    var attempt = 0;
    while (true) {
      attempt++;
      try {
        return await _runWithEventsAttemptAsync(
            streamId, perspectiveName, lastProcessedEventId, events, firePreLifecycle && attempt == 1, backgroundTasks, cancellationToken);
      } catch (global::Whizbang.Core.Perspectives.PerspectiveRowConflictException conflict)
          when (_retriesApplyConflict(conflict, perspectiveName, streamId, attempt)) {
        // Re-read and re-apply onto the moved row.
      }
    }
  }

  /// <summary>
  /// Whether a live apply whose write was refused because the row moved runs again (#928): true, logged at Debug, until
  /// the last attempt; then false, logged at Warning, and the conflict propagates to the worker's failure path.
  /// </summary>
  private bool _retriesApplyConflict(
      global::Whizbang.Core.Perspectives.PerspectiveRowConflictException conflict, string perspectiveName, Guid streamId, int attempt) {
    if (attempt < MAX_ROW_CONFLICT_ATTEMPTS) {
      _logger.LogDebug(
          conflict,
          "Row for {PerspectiveName} stream {StreamId} changed after it was read (attempt {Attempt} of {MaxAttempts}); re-reading and re-applying",
          perspectiveName,
          streamId,
          attempt,
          MAX_ROW_CONFLICT_ATTEMPTS
      );
      return true;
    }
    _logger.LogWarning(
        conflict,
        "Row for {PerspectiveName} stream {StreamId} changed under each of {MaxAttempts} apply attempts; giving up so the batch is retried through the failure path. Nothing stale was written",
        perspectiveName,
        streamId,
        MAX_ROW_CONFLICT_ATTEMPTS
    );
    return false;
  }

  /// <summary>
  /// Ordering invariant, at the apply boundary. A rebuild folds the whole stream it read, so it takes the replay
  /// order the rewind uses (origin, then stream version, then commit, then id; #1352): sorting it by id applied a
  /// stream's first committed event last whenever that event carried the highest id, and checkpointed on it (#1331).
  /// The live drain keeps id order; an inversion there is the rewind's to correct.
  /// </summary>
  private static IReadOnlyList<MessageEnvelope<IEvent>> _orderForApply(IReadOnlyList<MessageEnvelope<IEvent>> events) {
    if (events.Count > 1) {
      events = global::Whizbang.Core.Messaging.ProcessingModeAccessor.Current is global::Whizbang.Core.Messaging.ProcessingMode.Rebuild
          ? global::Whizbang.Core.Perspectives.ReplayEventOrder.Order(events)
          : [.. events.OrderByMessageId()];
    }
    return events;
  }

  /// <summary>
  /// How many times one batch is re-read and re-applied when its write is refused because the row moved.
  /// </summary>
  private const int MAX_ROW_CONFLICT_ATTEMPTS = 5;

  /// <summary>
  /// How many catch-up passes a rewind makes over events appended while it applies, before it stops and leaves the
  /// rest to the next claim (v0.688).
  /// </summary>
  private const int MAX_REWIND_CATCH_UP_ITERATIONS = 100;

  /// <summary>
  /// One read-fold-write attempt of <see cref="RunWithEventsAsync"/>. Throws
  /// <see cref="global::Whizbang.Core.Perspectives.PerspectiveRowConflictException"/> when the write is refused;
  /// the caller re-runs it. The pre-perspective lifecycle stages fire on the first attempt only: they announce
  /// the batch, and a retry re-folds the same batch.
  /// </summary>
  /// <remarks>
  /// Built from the same pieces as the set-based split (#2246): <see cref="_readBatchAsync"/> and
  /// <see cref="_foldAsync"/> are what <see cref="PrepareAsync"/> runs, <see cref="_finishApplyAsync"/> is what
  /// <see cref="CompleteAsync"/> runs, and only the write between them differs.
  /// </remarks>
  private async Task<PerspectiveCursorCompletion> _runWithEventsAttemptAsync(
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      IReadOnlyList<MessageEnvelope<IEvent>> events,
      bool firePreLifecycle,
      List<Task> backgroundTasks,
      CancellationToken cancellationToken) {

    var read = await _readBatchAsync(streamId, perspectiveName, lastProcessedEventId, events, backgroundTasks, preparing: false, batchState: null, cancellationToken);
    if (read.Completion is { } decided) {
      return decided;
    }
    var state = read.State!;

    try {
      await _foldAsync(state, firePreLifecycle, cancellationToken);
      var rowChanged = false;  // True once this batch actually wrote or purged the row
      if (state.EventsProcessed > 0) {
        await _beginRowWriteAsync(state, cancellationToken);
        rowChanged = await _writeRowAsync(state, cancellationToken);
      }
      return await _finishApplyAsync(state, rowChanged, cancellationToken);
    } catch (Exception ex) when (ex is not global::Whizbang.Core.Perspectives.PerspectiveRowConflictException) {
      // A refused write (the row moved) is not a partial failure: nothing was written and the caller re-runs
      // the whole batch onto the moved row, so it passes straight through.
      await _savePartialProgressAsync(state, ex, cancellationToken);
      // Re-throw - let PerspectiveWorker catch and convert to failure
      throw;
    }
  }

  /// <summary>
  /// The first half of a set-based write (#2246): the read and the fold <see cref="RunWithEventsAsync"/> makes, with
  /// the row write prepared for the batch's write sink instead of made. The caller holds the pair's apply lock until
  /// <see cref="CompleteAsync"/> returns (<see cref="global::Whizbang.Core.Perspectives.PerspectiveApplyLockSet"/>), so
  /// this takes none.
  /// </summary>
  /// <remarks>
  /// A pair the batch write does not take is handed back with a reason, before anything is folded where the reason is
  /// known from the read (a rewind, an evicted or purged row), so the per-pair path that completes it fires the
  /// pre-perspective stages once. A purge records its marker here, before the batch removes the row, as the per-pair
  /// purge does.
  /// </remarks>
  /// <docs>fundamentals/perspectives/write-modes#prepare-and-commit</docs>
  public async Task<PreparedPerspectiveApply> PrepareAsync(
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      IReadOnlyList<MessageEnvelope<IEvent>> events,
      global::Whizbang.Core.Perspectives.IPerspectiveBatchState? batchState,
      CancellationToken cancellationToken = default) {

    events = _orderForApply(events);
    var read = await _readBatchAsync(streamId, perspectiveName, lastProcessedEventId, events, [], preparing: true, batchState, cancellationToken);
    if (read.State is null) {
      return new Prepared(this, streamId, perspectiveName, lastProcessedEventId, events, null, read.PerPair, null, read.Completion);
    }
    var state = read.State;

    try {
      await _foldAsync(state, firePreLifecycle: true, cancellationToken);
      global::Whizbang.Core.Perspectives.PendingPerspectiveWrite? write = null;
      if (state.EventsProcessed > 0) {
        await _beginRowWriteAsync(state, cancellationToken);
        if (state.RowWrite == RowWriteKind.Purge) {
          write = _perspectiveStore.PreparePurge(streamId);
          if (write is not null) {
            // Record the purge FIRST, then let the batch remove the row (as the per-pair purge does).
            await _markPurgedAsync(streamId, perspectiveName, state.PurgeEventId, cancellationToken);
          }
        } else if (state.RowWrite == RowWriteKind.Upsert) {
          write = await _prepareRowWriteAsync(state, cancellationToken);
        }
        if (write is null && state.RowWrite != RowWriteKind.None) {
          // The store cannot write this row set-based: the per-pair path writes it, re-reading the row, so nothing
          // prepared here is kept but the detached stages already started.
          state.DisposeSnapshots();
          return new Prepared(
              this, streamId, perspectiveName, lastProcessedEventId, events, null,
              global::Whizbang.Core.Perspectives.PerspectivePerPairReason.WriteNotPreparable, state, null);
        }
      }
      return new Prepared(
          this, streamId, perspectiveName, lastProcessedEventId, events, write,
          global::Whizbang.Core.Perspectives.PerspectivePerPairReason.None, state, null);
    } catch (Exception ex) when (ex is not global::Whizbang.Core.Perspectives.PerspectiveRowConflictException) {
      await _savePartialProgressAsync(state, ex, cancellationToken);
      throw;
    }
  }

  /// <summary>
  /// The second half of a set-based write (#2246): given the outcome of the write <see cref="PrepareAsync"/> prepared,
  /// does what follows the write on the per-pair path and returns the same completion. The caller still holds the
  /// pair's apply lock, so this takes none, and a pair it reruns goes through the lock-free per-pair core.
  /// </summary>
  /// <remarks>
  /// Written finishes the pair. Refused is the per-pair path's refused write, which rewinds the stream (#1608). A
  /// conflict reruns the batch on the moved row with the per-pair retries, and a row the database rejected reruns it
  /// per-pair too, where it fails exactly as it would have (or takes the store's fallback write).
  /// </remarks>
  /// <docs>fundamentals/perspectives/write-modes#prepare-and-commit</docs>
  public async Task<PerspectiveCursorCompletion> CompleteAsync(
      PreparedPerspectiveApply prepared,
      global::Whizbang.Core.Perspectives.PerspectiveWriteOutcome? outcome,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(prepared);
    if (prepared is not Prepared mine || !ReferenceEquals(mine.Runner, this)) {
      throw new ArgumentException(
          "A prepared perspective apply is completed by the runner that prepared it.", nameof(prepared));
    }
    if (mine.Decided is { } decided) {
      return decided;
    }
    var state = mine.State;
    if (mine.RunsPerPair) {
      // A pair handed back before its fold has fired nothing yet; one handed back after it has fired its
      // pre-perspective stages, and its detached ones are still to be awaited.
      return await _runWithEventsLockedAsync(
          mine.StreamId, mine.PerspectiveName, mine.LastProcessedEventId, mine.Events,
          firePreLifecycle: state is null, state?.BackgroundTasks ?? [], cancellationToken);
    }
    if (mine.Write is not null && outcome is null) {
      throw new ArgumentNullException(nameof(outcome), "A prepared write is completed with the outcome of its write.");
    }
    var status = mine.Write is null ? (global::Whizbang.Core.Perspectives.PerspectiveWriteStatus?)null : outcome!.Value.Status;
    if (status is global::Whizbang.Core.Perspectives.PerspectiveWriteStatus.Conflict
        or global::Whizbang.Core.Perspectives.PerspectiveWriteStatus.Failed) {
      state!.DisposeSnapshots();
      if (status == global::Whizbang.Core.Perspectives.PerspectiveWriteStatus.Failed) {
        _logger.LogWarning(
            outcome!.Value.Error,
            "[diag.write.failed] {PerspectiveName} stream {StreamId}: the database rejected the row in a set-based write; applying the batch per-pair",
            mine.PerspectiveName,
            mine.StreamId);
      } else {
        _logger.LogDebug(
            "Row for {PerspectiveName} stream {StreamId} changed after it was read for a set-based write; re-reading and re-applying per-pair",
            mine.PerspectiveName,
            mine.StreamId);
      }
      return await _runWithEventsLockedAsync(
          mine.StreamId, mine.PerspectiveName, mine.LastProcessedEventId, mine.Events,
          firePreLifecycle: false, state.BackgroundTasks, cancellationToken);
    }

    try {
      return await _finishApplyAsync(
          state!, rowChanged: status == global::Whizbang.Core.Perspectives.PerspectiveWriteStatus.Written, cancellationToken);
    } catch (Exception ex) when (ex is not global::Whizbang.Core.Perspectives.PerspectiveRowConflictException) {
      await _savePartialProgressAsync(state!, ex, cancellationToken);
      throw;
    }
  }

  /// <summary>
  /// A pair <see cref="PrepareAsync"/> prepared, with the state <see cref="CompleteAsync"/> needs: the fold, or the
  /// completion already decided (a batch the row already holds).
  /// </summary>
  private sealed class Prepared(
      PerspectiveRunnerBase<TPerspective, TModel> runner,
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      IReadOnlyList<MessageEnvelope<IEvent>> events,
      global::Whizbang.Core.Perspectives.PendingPerspectiveWrite? write,
      global::Whizbang.Core.Perspectives.PerspectivePerPairReason perPairReason,
      ApplyState? state,
      PerspectiveCursorCompletion? decided) : PreparedPerspectiveApply(streamId, perspectiveName, lastProcessedEventId, events, write, perPairReason) {
    public PerspectiveRunnerBase<TPerspective, TModel> Runner { get; } = runner;
    public ApplyState? State { get; } = state;
    public PerspectiveCursorCompletion? Decided { get; } = decided;
  }

  /// <summary>What a batch's row write does.</summary>
  private enum RowWriteKind {
    /// <summary>Nothing: no Apply produced a model, or a purged stream stayed purged.</summary>
    None,
    /// <summary>Remove the row an Apply purged.</summary>
    Purge,
    /// <summary>Write the folded model.</summary>
    Upsert,
  }

  /// <summary>
  /// One apply of one batch, from the row it read to the write it makes: what the per-pair attempt keeps in locals,
  /// kept here so a prepare can hand it to its completion (#2246). Fields, so the fold can alias them by reference.
  /// </summary>
  private sealed class ApplyState {
    public Guid StreamId;
    public string PerspectiveName = string.Empty;
    public Guid? LastProcessedEventId;
    public IReadOnlyList<MessageEnvelope<IEvent>> Events = [];
    public List<Task> BackgroundTasks = [];
    public TPerspective Perspective = null!;
    // The batch a prepare reads from (#2246), or null on the per-pair path.
    public global::Whizbang.Core.Perspectives.IPerspectiveBatchState? BatchState;

    // The read.
    public CollectiveFenceRead? CollectiveFence;
    public global::Whizbang.Core.Perspectives.PerspectiveApplyRead ApplyRead = global::Whizbang.Core.Perspectives.PerspectiveApplyRead.Unchecked;
    public global::Whizbang.Core.Perspectives.PerspectiveRowVersion RowVersion;
    public bool ModelLoadedFromDb;
    public bool StartedPurged;
    public bool Rebuilding;
    public global::Whizbang.Core.Lenses.PerspectiveMetadata? ExistingMetadata;
    // The row as stored, kept so a rebuild whose write is refused can tell an already-correct row from a stale one (#2252).
    public TModel? StoredModel;

    // The fold.
    public int EventsProcessed;
    public Guid? LastSuccessfulEventId;
    public string? LastSuccessfulEventType;
    public DateTime? LastSuccessfulEventAt;
    public List<MessageEnvelope<IEvent>> ProcessedEvents = [];
    public TModel? UpdatedModel;
    public bool HasWrittenUpdate;
    public bool CollectiveInFlight;
    public bool PendingPurge;
    public Guid? PurgeEventId;
    public string? PurgeEventType;
    public bool Resurrected;
    public int PurgedEventsSkipped;
    public PerspectiveScope? LastScope;
    public PerspectiveScope? ScopeEventScope;
    public JsonDocument? ModelAtLastEvent;

    // The write.
    public RowWriteKind RowWrite;
    public long SaveStartTicks;
    public bool SnapshotDue;
    public JsonDocument? SnapshotBeforeWrite;
    public long? CheckpointCommitSequence;
    public global::Whizbang.Core.Lenses.ScopeWrite ScopeWrite;
    // #2252: a rebuild's fold, taken before the write strips a Split class model's promoted fields; null for any other
    // apply. Taken for the per-pair write and a prepared one alike, since both strip the model the same way.
    public JsonDocument? FoldBeforeWrite;

    /// <summary>Releases the serialized models this apply took for a snapshot or a comparison it will no longer make.</summary>
    public void DisposeSnapshots() {
      SnapshotBeforeWrite?.Dispose();
      SnapshotBeforeWrite = null;
      ModelAtLastEvent?.Dispose();
      ModelAtLastEvent = null;
      FoldBeforeWrite?.Dispose();
      FoldBeforeWrite = null;
    }
  }

  /// <summary>
  /// What reading a batch decided: an apply to fold (<see cref="State"/>), a completion already reached (a rewind the
  /// read started, or a batch the row already holds), or, when preparing, a hand-back to the per-pair path.
  /// </summary>
  private sealed record BatchRead(
      ApplyState? State,
      PerspectiveCursorCompletion? Completion,
      global::Whizbang.Core.Perspectives.PerspectivePerPairReason PerPair) {
    public static implicit operator BatchRead(ApplyState state) =>
        new(state, null, global::Whizbang.Core.Perspectives.PerspectivePerPairReason.None);
    public static implicit operator BatchRead(PerspectiveCursorCompletion completion) =>
        new(null, completion, global::Whizbang.Core.Perspectives.PerspectivePerPairReason.None);
    public static implicit operator BatchRead(global::Whizbang.Core.Perspectives.PerspectivePerPairReason perPair) =>
        new(null, null, perPair);
  }

  /// <summary>The row as an apply reads it: its version, scope and metadata, and the model.</summary>
  private readonly record struct RowLoad(
      global::Whizbang.Core.Perspectives.PerspectiveApplyRead ApplyRead,
      TModel? Model,
      global::Whizbang.Core.Lenses.PerspectiveMetadata? Metadata);

  /// <summary>
  /// Reads the row a live apply folds onto: the one place it is read, and the seam a batched state source replaces
  /// with one read for every stream of a batch (#2246).
  /// </summary>
  /// <remarks>
  /// The row's version and metadata are read FIRST, then the model. The model can then never be older than the
  /// version the write is checked against: a writer that commits between the two reads moves the version and the
  /// write is refused, never landed. A store that tracks no versions returns Unchecked and the reads are exactly the
  /// ones the runner always made: the metadata is read on its own, and only for a row that exists outside a rebuild.
  /// </remarks>
  private async Task<RowLoad> _loadRowForApplyAsync(
      Guid streamId,
      string perspectiveName,
      bool rebuilding,
      global::Whizbang.Core.Perspectives.IPerspectiveBatchState? batchState,
      CancellationToken cancellationToken) {
    // A set-based batch's state source (#2246): no read for a stream it knows is new, and its one read per perspective
    // table for the rest. Whatever it cannot answer is read per stream, below.
    if (batchState is not null && !rebuilding) {
      if (batchState.IsKnownNew(streamId, perspectiveName)) {
        return new RowLoad(
            new global::Whizbang.Core.Perspectives.PerspectiveApplyRead(global::Whizbang.Core.Perspectives.PerspectiveRowVersion.Absent, null),
            null,
            null);
      }
      if (await batchState.ReadRowAsync(_perspectiveStore, streamId, perspectiveName, cancellationToken) is { } batched) {
        return new RowLoad(batched.ApplyRead, batched.Model, batched.Model is null ? null : batched.ApplyRead.Metadata);
      }
    }
    var applyRead = await _perspectiveStore.ReadForApplyAsync(streamId, cancellationToken);
    var rowVersion = applyRead.Version;

    // Load current model or create new one. A checked read that found no row needs no model read.
    var currentModel = rowVersion.State == global::Whizbang.Core.Perspectives.PerspectiveRowVersionState.Absent
        ? null
        : await _perspectiveStore.GetByStreamIdAsync(
            streamId,
            cancellationToken
        );
    if (currentModel == null || rebuilding) {
      return new RowLoad(applyRead, currentModel, null);
    }
    var existingMetadata = rowVersion.IsChecked
        ? applyRead.Metadata
        : await _perspectiveStore.GetMetadataByStreamIdAsync(streamId, cancellationToken);
    return new RowLoad(applyRead, currentModel, existingMetadata);
  }

  /// <summary>
  /// Reads the row and decides what the batch does before it is folded: re-fold the stream (an evicted row whose
  /// stream woke, or an event below the row's position), report a batch the row already holds, or fold. When
  /// <paramref name="preparing"/>, the decisions that re-fold are handed back to the per-pair path instead of run.
  /// </summary>
  private async Task<BatchRead> _readBatchAsync(
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      IReadOnlyList<MessageEnvelope<IEvent>> events,
      List<Task> backgroundTasks,
      bool preparing,
      global::Whizbang.Core.Perspectives.IPerspectiveBatchState? batchState,
      CancellationToken cancellationToken) {

    // #1963: when this apply may end in a snapshot of a row with collectives, the collective fence is read BEFORE the row,
    // and read again after the write; the snapshot records the first read's mark only when no collective of the row moved
    // across both reads, and is deferred otherwise.
    var collectiveFence = await _readCollectiveFenceBeforeLiveReadAsync(streamId, events, cancellationToken);

    // A rebuild re-derives the row from its events (see below), so it reads no recorded position.
    var rebuilding = global::Whizbang.Core.Messaging.ProcessingModeAccessor.Current
        is global::Whizbang.Core.Messaging.ProcessingMode.Rebuild;
    var loaded = await _loadRowForApplyAsync(streamId, perspectiveName, rebuilding, batchState, cancellationToken);
    var applyRead = loaded.ApplyRead;
    var rowVersion = applyRead.Version;
    var currentModel = loaded.Model;

    // Track whether the stream already exists in the DB. When it does not, the pre-created
    // empty default below is scaffolding for Apply input only - it must NOT be persisted
    // unless Apply actually returns a model. Otherwise ApplyResult.None() on a new stream
    // would insert a phantom default row.
    var modelLoadedFromDb = currentModel != null;
    // The row as stored, kept so a rebuild whose write is refused can tell an already-correct row from a stale one (#2252).
    var storedModel = rebuilding && modelLoadedFromDb ? currentModel : null;

    // Purge stays purged (issue #1027): true when the row is missing BECAUSE it was purged.
    var startedPurged = false;

    // Create new model if none exists (null from DB)
    if (currentModel == null) {
      // Resurrection-on-wake (perspective row retention, issue #1483): for a perspective whose rows
      // can be evicted (a row TTL, a row cap, or a stream-group membership the cascade evicts through)
      // a missing row is ambiguous: a brand-new stream OR an evicted row whose stream just woke. The row
      // is the fold of ALL its events, so applying only this batch onto a fresh model would silently
      // build a partial row nothing ever repairs. Probe the log for pre-batch history OF THE EVENT TYPES
      // THIS PERSPECTIVE FOLDS: that means evicted-and-woken. The probe is perspective-aware on purpose
      // (issue #696): a stream shared by several contracts holds history this perspective never folded,
      // and "any earlier event" read that as an evicted row on every first contact. A perspective whose
      // rows nothing can evict never probes, so it pays nothing. A row an archive dropped or moved out of its table
      // (#1739, [ArchivedStreamRows] Drop or Archive) is missing the same way when its stream comes back, and is rebuilt
      // the same way.
      //
      // The rebuild is the rewind CORE (latest snapshot + the events after it; the incoming events are
      // already stored, so the replay includes them). The eviction kept exactly that snapshot. The core
      // variant is required because RunAsync already holds the apply lock the public RewindAndRunAsync
      // acquires. An ephemeral perspective's history before its snapshot is reaped, so it rebuilds only
      // from a snapshot floor it can READ; with none, nothing can rebuild the row and it starts fresh from
      // this batch, with a warning, because the earlier state is gone. A floor written in an older snapshot
      // format is the same case (issue #1506): the rewind could not restore it, would find no floor, and
      // would drop this batch as a late straggler, leaving the row missing until the next event.
      if (events.Count > 0
          && (global::Whizbang.Core.Perspectives.PerspectiveRowEviction.CanEvict(typeof(TModel))
              || global::Whizbang.Core.Archival.ArchivedStreamRowPolicies.RemovesRows(_traits.ArchivedStreamRowsName))
          && await _hasHistoryBeforeAsync(streamId, perspectiveName, events[0].MessageId.Value, batchState, cancellationToken)) {
        if (preparing) {
          return global::Whizbang.Core.Perspectives.PerspectivePerPairReason.MissingRowWithHistory;
        }
        var floor = !_traits.IsEphemeral
            ? (State: EvictedRowFloor.Readable, SnapshotEventId: Guid.Empty)
            : await _evictedRowFloorAsync(streamId, perspectiveName, events[0].MessageId.Value, cancellationToken);
        if (floor.State == EvictedRowFloor.Readable) {
          _logger.LogInformation(
              "Row for stream {StreamId} in {PerspectiveName} is missing but the stream has history: resurrecting via re-fold",
              streamId,
              perspectiveName
          );
          return await _rewindAndRunCoreAsync(
              streamId, perspectiveName, events[0].MessageId.Value, null, cancellationToken);
        }
        if (floor.State == EvictedRowFloor.Stale) {
          _logger.LogWarning(
              "Row for stream {StreamId} in {PerspectiveName} was evicted and its ephemeral history is reaped; its snapshot at {SnapshotEventId} has a stale serialization version and cannot be restored, so starting it fresh from event {EventId}",
              streamId,
              perspectiveName,
              floor.SnapshotEventId,
              events[0].MessageId.Value
          );
        } else {
          _logger.LogWarning(
              "Row for stream {StreamId} in {PerspectiveName} was evicted and its ephemeral history is reaped with no snapshot to restore from; starting it fresh from event {EventId}",
              streamId,
              perspectiveName,
              events[0].MessageId.Value
          );
        }
      }

      // Purge stays purged (issue #1027). A missing row is otherwise a new stream; when the stream carries a
      // purge marker it is a purged one, and this batch is skipped unless an Apply resurrects it. Consulted
      // only here, on a missing row, and a marker exists only after a purge, so a stream that was never purged
      // (new or live) is never affected and the steady state pays nothing.
      // <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
      startedPurged = events.Count > 0 && await _isPurgedForApplyAsync(streamId, perspectiveName, batchState, cancellationToken);
      if (startedPurged && preparing) {
        return global::Whizbang.Core.Perspectives.PerspectivePerPairReason.PurgedStream;
      }

      _logger.LogDebug(
          "No existing model found for stream {StreamId} in {PerspectiveName}, creating new model",
          streamId,
          perspectiveName
      );
      currentModel = CreateEmptyModel(streamId);
    }

    // Idempotency guard: skip events already applied to this row.
    // The cursor advances in a separate transaction (PerspectiveCompletionFlushWorker) than
    // the row upsert (this method). When the worker dies between commits, the same events
    // get re-claimed and would re-apply to a populated model - duplicating list-style
    // projection rows.
    //
    // Idempotency filter. The decision lives in PerspectiveIdempotencyFilter so it is unit-tested
    // once rather than per generated runner; see its tests for each case. In short: compare
    // commit_sequence when both sides have one, compare event ids only when both are UUIDv7, and
    // otherwise defer - an event that cannot be shown to be applied is passed to Apply, because a
    // double-apply is visible and recoverable while a discarded event is neither.
    // A rebuild re-derives the row from its events, so the row's own recorded position is the one thing
    // it must not consult: every event IS already applied by that measure, the filter below drops the
    // whole batch, and the stream returns Completed having folded nothing while the rebuilder reports
    // success (#1313). Blue-green escaped this only because it writes into a fresh shadow table, where
    // no row loads and there is no position to compare against. The ambient mode is how the rebuilder
    // already tells generated runners what it is doing, which is what ProcessingModeAccessor is for.
    if (rebuilding) {
      // Re-derive, do not fold on top. A rebuild reads the stream whole, so starting from the stored row
      // applies every event a second time: an assignment survives that, an additive fold (Balance += Amount)
      // does not. Blue-green has always started empty, because it writes into a shadow table where no row
      // loads; this is the same thing for the modes that write the live table. modelLoadedFromDb is left
      // alone on purpose: it still describes the DATABASE, which is what decides insert versus update below.
      currentModel = CreateEmptyModel(streamId);
    }
    var existingMetadata = loaded.Metadata;
    var lastAppliedEventId = existingMetadata?.EventId;
    var lastAppliedCommitSequence = existingMetadata?.CommitSequence;
    if (!string.IsNullOrEmpty(lastAppliedEventId) && events.Count > 0) {
      // An event below the row's recorded position is not dropped on that evidence alone (#1347): the row can have been
      // moved past it by a later batch without ever folding it, and the worker's cursor check cannot see that when the
      // stream has no cursor or a cursor behind the row. Re-fold the stream from the earliest such event instead; this
      // runs under the apply lock already held, which is why it calls the core and not RewindAndRunAsync. A prepare
      // hands the pair to the per-pair path, which re-folds it the same way.
      var rewindAnchor = global::Whizbang.Core.Perspectives.PerspectiveIdempotencyFilter.FindRewindAnchor(
          lastAppliedEventId, lastAppliedCommitSequence, events.Select(e => (e.MessageId.Value, e.LocalCommitSequence)));
      if (rewindAnchor is Guid anchorEventId) {
        if (preparing) {
          return global::Whizbang.Core.Perspectives.PerspectivePerPairReason.RewindBelowPosition;
        }
        var anchorCommitSequence = events.First(e => e.MessageId.Value == anchorEventId).LocalCommitSequence;
        _logger.LogWarning(
            "[diag.filter.rewind-below-position] {PerspectiveName} stream {StreamId}: event {AnchorEventId} at commit sequence {AnchorCs} is below the row's recorded position {LastCs} ({LastEventId}); rewinding instead of filtering {Count} event(s) as already applied",
            perspectiveName,
            streamId,
            anchorEventId,
            anchorCommitSequence,
            lastAppliedCommitSequence,
            lastAppliedEventId,
            events.Count);
        return await _rewindAndRunCoreAsync(streamId, perspectiveName, anchorEventId, anchorCommitSequence, cancellationToken);
      }
      var filtered = new List<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>>(events.Count);
      foreach (var e in events) {
        var isAlreadyApplied = global::Whizbang.Core.Perspectives.PerspectiveIdempotencyFilter.IsAlreadyApplied(
            lastAppliedEventId, lastAppliedCommitSequence, e.MessageId.Value, e.LocalCommitSequence);
        if (!isAlreadyApplied) {
          filtered.Add(e);
        }
      }
      if (filtered.Count != events.Count) {
        if (filtered.Count == 0) {
          // All events already applied. Status MUST be Completed (not None) so the
          // PerspectiveWorker enqueues each EventWorkId for deletion from wh_perspective_events
          // - otherwise claim_orphaned_perspective_events keeps re-claiming the same rows
          // every safety-net interval and we get a hot loop of "Skipped already-applied".
          // The events ARE done from the perspective-events table standpoint; the runner just
          // had no model work to do because the row already reflects them.
          //
          // Warning, not Debug. Every event in the batch was judged already-applied, so this method is
          // about to report Completed and have the work rows deleted without touching the model. When
          // that judgement is right the line is redundant with the cursor; when it is wrong it is the
          // only trace that a write was dropped, and at Debug nobody sees it. A whole batch filtered is
          // rare enough in a healthy system to afford the level - the partial case below stays at Debug.
          if (_logger.IsEnabled(LogLevel.Warning)) {
            var droppedIds = string.Join(",", events.Select(e => e.MessageId.Value.ToString("D")));
            // Only a stamped event is judged applied (PerspectiveIdempotencyFilter), so every dropped one has a commit sequence.
            var droppedSeqs = string.Join(",", events.Select(e => Convert.ToString(e.LocalCommitSequence, global::System.Globalization.CultureInfo.InvariantCulture)));
            _logger.LogWarning(
                "[diag.filter.all-dropped] {Skipped} events filtered as 'already applied' for {PerspectiveName} stream {StreamId}: persistedEventId={LastEventId} persistedCs={LastCs} droppedEventIds=[{DroppedIds}] droppedCs=[{DroppedSeqs}] returning Completed (events will be deleted from wh_perspective_events)",
                events.Count - filtered.Count,
                perspectiveName,
                streamId,
                lastAppliedEventId,
                lastAppliedCommitSequence,
                droppedIds,
                droppedSeqs);
          }
          var alreadyAppliedAsGuid = Guid.TryParse(lastAppliedEventId, out var parsed) ? parsed : Guid.Empty;
          return new PerspectiveCursorCompletion {
            StreamId = streamId,
            PerspectiveName = perspectiveName,
            PerspectiveType = typeof(TPerspective),
            LastEventId = alreadyAppliedAsGuid != Guid.Empty ? alreadyAppliedAsGuid : (lastProcessedEventId ?? Guid.Empty),
            Status = PerspectiveProcessingStatus.Completed,
            EventsProcessed = 0,
            RowChanged = false,  // every event was judged already applied, so the model was not touched
            ProcessedEventIds = events.Select(e => e.MessageId.Value).ToArray()
          };
        } else {
          // Partial skip: SOME events filtered, SOME pass through to Apply. Surface the
          // dropped ids + cs so we can correlate against wh_event_store and confirm whether
          // they were already in ProcessedLineNumbers / equivalent idempotency state.
          if (_logger.IsEnabled(LogLevel.Debug)) {
            var droppedIds = string.Join(",", events.Where(e => !filtered.Contains(e)).Select(e => e.MessageId.Value.ToString("D")));
            var droppedSeqs = string.Join(",", events.Where(e => !filtered.Contains(e)).Select(e => Convert.ToString(e.LocalCommitSequence, global::System.Globalization.CultureInfo.InvariantCulture)));
            _logger.LogDebug(
                "[diag.filter.partial-dropped] {Skipped} events filtered as 'already applied' for {PerspectiveName} stream {StreamId}: persistedEventId={LastEventId} persistedCs={LastCs} droppedEventIds=[{DroppedIds}] droppedCs=[{DroppedSeqs}] remaining={Remaining}",
                events.Count - filtered.Count,
                perspectiveName,
                streamId,
                lastAppliedEventId,
                lastAppliedCommitSequence,
                droppedIds,
                droppedSeqs,
                filtered.Count);
          }
        }
      }
      events = filtered;
    }

    // Get perspective instance from DI, and track progress from the row the batch folds onto.
    return new ApplyState {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = lastProcessedEventId,
      Events = events,
      BackgroundTasks = backgroundTasks,
      BatchState = batchState,
      Perspective = _serviceProvider.GetRequiredService<TPerspective>(),
      CollectiveFence = collectiveFence,
      ApplyRead = applyRead,
      RowVersion = rowVersion,
      ModelLoadedFromDb = modelLoadedFromDb,
      StartedPurged = startedPurged,
      Rebuilding = rebuilding,
      StoredModel = storedModel,
      ExistingMetadata = existingMetadata,
      LastSuccessfulEventId = lastProcessedEventId,
      LastSuccessfulEventType = existingMetadata?.EventType,
      UpdatedModel = currentModel,
      // True once Apply has contributed a concrete model OR the row came from DB.
      // When false at save time, updatedModel is only the scaffolded default and must not be written.
      HasWrittenUpdate = modelLoadedFromDb,
      PendingPurge = startedPurged,  // True while the stream is purged: the row is (to be) removed
    };
  }

  /// <summary>
  /// Folds the batch onto the row it read: the pre-perspective stages, the collectives a replay interleaves, and every
  /// event's Apply, in order and in memory. The fold of the per-pair path and of a prepare.
  /// </summary>
  private async Task _foldAsync(ApplyState state, bool firePreLifecycle, CancellationToken cancellationToken) {
    var streamId = state.StreamId;
    var lastProcessedEventId = state.LastProcessedEventId;
    var events = state.Events;
    var backgroundTasks = state.BackgroundTasks;

    // Invoke PrePerspective lifecycle receptors (fires once per batch, not per event, and not again
    // when a refused write re-runs the batch)
    if (firePreLifecycle && events.Count > 0) {
      var firstEnvelope = events[0];  // First envelope for receptor routing (envelope preserves security context)
      var firstEnvelopeType = firstEnvelope.Payload.GetType();
      var firstEnvelopeTypeName = PerspectiveRunnerTypeNames.ReceptorKey(firstEnvelopeType);

      // Slice 30A gates short-circuit when no receptor is registered. Original code consulted
      // only the source-generated compile-time WhizbangReceptorRegistryQuery, which has the
      // same blind spot as the InboxDispatchWorker / TransportConsumerWorker fixes: services
      // whose generated contribution lists no compile-time receptor for a stage would silently
      // skip the spawn even when an integration-test wait helper (or any production runtime
      // registration) was listening at that stage. Resolve the runtime IReceptorRegistry once
      // here and OR the compile-time check with a per-Type lookup; GetReceptorsFor returns
      // compile-time entries concatenated with runtime ones, so the per-Type call subsumes the
      // static check in the common case. The static fallback covers harnesses that don't
      // register IReceptorRegistry in DI.
      var runtimeReceptorRegistry = _serviceProvider.GetService<global::Whizbang.Core.Messaging.IReceptorRegistry>();

      var context = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.PrePerspectiveDetached,
        StreamId = streamId,
        PerspectiveType = typeof(TPerspective),
        EventId = null,  // No specific event ID yet (haven't processed)
        LastProcessedEventId = lastProcessedEventId,
        ProcessingMode = global::Whizbang.Core.Messaging.ProcessingModeAccessor.Current
      };

      // Slice 30A - gate the dedicated-OS-thread spawn on the source-gen receptor registry.
      // When no receptor is registered for PrePerspectiveDetached on this event type the
      // spawn is pure overhead: BackgroundStageDispatch.StartLongRunning creates an OS
      // thread via TaskCreationOptions.LongRunning (~5-10 ms on Linux), and the drain
      // awaits this task via backgroundTasks below. Production PERF data showed the spawn
      // dominates per-drain wall time on perspectives with no Pre/Post detached receptors
      // registered. Tag dispatch fires from the inline path below so skipping the spawn
      // does not lose tag side-effects.
      if (runtimeReceptorRegistry?.GetReceptorsFor(firstEnvelopeType, LifecycleStage.PrePerspectiveDetached).Count > 0
          || global::Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PrePerspectiveDetached, firstEnvelopeTypeName)) {
        // Fire ASYNC hooks (non-blocking - runs concurrently with perspective processing).
        // Use BackgroundStageDispatch.StartLongRunning (dedicated thread) instead of Task.Run
        // (pooled thread) so this stage isn't starved when the ThreadPool is saturated by EF
        // continuations + transport callbacks during heavy processing. This invocation IS awaited
        // via backgroundTasks below - without LongRunning, pool starvation causes the await to
        // block past the test deadline even though the body is trivial.
        var preAsyncTask = global::Whizbang.Core.Workers.BackgroundStageDispatch.StartLongRunning(async () => {
          await using var lifecycleScope = _scopeFactory.CreateAsyncScope();
          var receptorInvoker = lifecycleScope.ServiceProvider.GetService<global::Whizbang.Core.Messaging.IReceptorInvoker>();
          if (receptorInvoker is not null) {
            await receptorInvoker.InvokeAsync(firstEnvelope, LifecycleStage.PrePerspectiveDetached, context with { CurrentStage = LifecycleStage.PrePerspectiveDetached }, cancellationToken);
          }
        }, cancellationToken);
        backgroundTasks.Add(preAsyncTask);
      }

      // Slice 30A - same gate for inline PrePerspective. Inline cost is lower (no OS thread
      // spawn) but still includes a DI scope creation + IReceptorInvoker lookup + security
      // context establishment. Skip when no inline receptors are registered.
      if (runtimeReceptorRegistry?.GetReceptorsFor(firstEnvelopeType, LifecycleStage.PrePerspectiveInline).Count > 0
          || global::Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PrePerspectiveInline, firstEnvelopeTypeName)) {
        // Fire INLINE hooks (blocking, transactional)
        await using var lifecycleScope = _scopeFactory.CreateAsyncScope();
        var receptorInvoker = lifecycleScope.ServiceProvider.GetService<global::Whizbang.Core.Messaging.IReceptorInvoker>();
        if (receptorInvoker is not null) {
          await receptorInvoker.InvokeAsync(firstEnvelope, LifecycleStage.PrePerspectiveInline, context with { CurrentStage = LifecycleStage.PrePerspectiveInline }, cancellationToken);
        }
      }
    }

    // Check if per-event tracing is enabled
    var enableEventSpans = _tracingOptions?.CurrentValue.EnablePerspectiveEventSpans ?? false;
    var appliedEventTypes = new global::System.Collections.Generic.List<string>();

    // Replay/rebuild only: interleave this tenant's collective events for the model into the stream's replay,
    // ordered by message id among the per-stream events - so a set-based collective mutation is reproduced
    // per-row and survives the rebuild. No-op during live drain, or when the model has no [CollectiveApplyFor]
    // handler / no applier is registered (the call returns the same list unchanged).
    if (_collectiveReplayApplier is not null && events.Count > 0) {
      events = await _collectiveReplayApplier.InterleaveForReplayAsync(
          typeof(TModel), events, cancellationToken);
      state.Events = events;
    }

    _foldEvents(state, events, enableEventSpans, appliedEventTypes, cancellationToken);

    // Add summary tags to parent activity (Perspective RunAsync span from PerspectiveWorker)
    // These tags show which events were processed even when per-event spans are disabled
    var eventsProcessed = state.EventsProcessed;
    if (eventsProcessed > 0) {
      var currentActivity = Activity.Current;
      if (currentActivity is not null) {
        currentActivity.SetTag("whizbang.perspective.events_applied", eventsProcessed);
        // Group event types with counts (e.g., "OrderCreated:3, OrderUpdated:2")
        var eventTypeCounts = appliedEventTypes
          .GroupBy(t => t)
          .Select(g => $"{g.Key}:{g.Count()}")
          .ToArray();
        currentActivity.SetTag("whizbang.perspective.event_types", string.Join(", ", eventTypeCounts));
      }
    }
  }

  /// <summary>
  /// Applies each event of the batch to the model, in memory: the one live fold. Synchronous, so it can alias the
  /// apply's state by reference and keep the fold's own names; each step lands in the state as it is taken, so a
  /// failure part-way saves exactly the progress made.
  /// </summary>
  private void _foldEvents(
      ApplyState state,
      IReadOnlyList<MessageEnvelope<IEvent>> events,
      bool enableEventSpans,
      List<string> appliedEventTypes,
      CancellationToken cancellationToken) {
    var streamId = state.StreamId;
    var perspectiveName = state.PerspectiveName;
    var perspective = state.Perspective;
    var applyRead = state.ApplyRead;
    ref var eventsProcessed = ref state.EventsProcessed;
    ref var lastSuccessfulEventId = ref state.LastSuccessfulEventId;
    ref var lastSuccessfulEventType = ref state.LastSuccessfulEventType;
    ref var lastSuccessfulEventAt = ref state.LastSuccessfulEventAt;
    var processedEvents = state.ProcessedEvents;  // Track envelopes for PostPerspectiveInline (fires AFTER save)
    ref var updatedModel = ref state.UpdatedModel;
    ref var hasWrittenUpdate = ref state.HasWrittenUpdate;
    ref var collectiveInFlight = ref state.CollectiveInFlight;
    ref var pendingPurge = ref state.PendingPurge;
    ref var purgeEventId = ref state.PurgeEventId;  // The event whose Apply purged in this batch, if one did
    ref var purgeEventType = ref state.PurgeEventType;  // Its type, reported on the completion (#1151)
    ref var resurrected = ref state.Resurrected;  // An Apply returned Resurrect while the stream was purged
    ref var purgedEventsSkipped = ref state.PurgedEventsSkipped;  // Events skipped because the stream was purged
    ref var lastScope = ref state.LastScope;  // Track scope from last processed envelope
    ref var scopeEventScope = ref state.ScopeEventScope;  // The scope an IScopeEvent set; forces a scope UPDATE (#1432)
    // The row at its last event, taken when collectives committed after that event fold into it (#1956): a snapshot this
    // run takes is taken at that event, so it must not hold them, or a rewind from it would fold them again.
    ref var modelAtLastEvent = ref state.ModelAtLastEvent;

    // A rebuild or rewind whose last event always purges the row (#1151) discards whatever the events before it
    // fold, so they are not folded: they count as processed and only the purge is applied. The list is the row's
    // merged, ordered set (collectives interleaved above), so "last" is last for the row, not for the stream.
    var foldEvents = events;
    if (events.Count > 1
        && global::Whizbang.Core.Messaging.ProcessingModeAccessor.Current is global::Whizbang.Core.Messaging.ProcessingMode.Rebuild or global::Whizbang.Core.Messaging.ProcessingMode.Replay
        && IsUnconditionalPurge(events[^1].Payload)) {
      foldEvents = [events[^1]];
      processedEvents.AddRange(events.Take(events.Count - 1));
      eventsProcessed += events.Count - 1;
    }

    // Process all events in order
    foreach (var envelope in foldEvents) {

      // Phase H step 9 slice 4: cooperative cancellation between events. Apply itself is a
      // pure synchronous function and can't be canceled mid-call, but we check the lease
      // token between events so a hot stream with N pending events doesn't run past the
      // deadline. The dispatch worker's LeaseHandle (constructed at drain start) is what
      // drives this token; without the check the apply loop would ignore the deadline and
      // a runaway perspective could park the drain pump for many minutes.
      cancellationToken.ThrowIfCancellationRequested();

      // Extract event from envelope
      var @event = envelope.Payload;
      var eventTypeName = global::Whizbang.Core.TypeNameFormatter.SimpleName(@event.GetType());

      // Track scope from envelope for perspective upsert
      var envelopeScope = envelope.GetCurrentScope();
      if (envelopeScope?.Scope != null) {
        lastScope = envelopeScope.Scope;
      }

      // Create per-event span if enabled
      using var eventActivity = enableEventSpans
        ? WhizbangActivitySource.Tracing.StartActivity(
            $"Apply {eventTypeName}",
            ActivityKind.Internal)
        : null;
      eventActivity?.SetTag("whizbang.perspective.event_type", PerspectiveRunnerTypeNames.SpanTag(@event.GetType()));
      eventActivity?.SetTag("whizbang.perspective.event_id", envelope.MessageId.Value.ToString());
      eventActivity?.SetTag("whizbang.perspective.stream_id", streamId.ToString());

      // Track event type for summary
      appliedEventTypes.Add(eventTypeName);

      // Collective events have no per-stream Apply(): during replay/rebuild they are folded into this one
      // row by the in-memory applier (self-referential per WHIZ106). They were interleaved above only in
      // rebuild/rewind mode, so this branch never runs during live drain.
      // A collective is another stream's event: it folds into the row but does not move the row's checkpoint, as on the
      // live path, where the set-based sink never touches a row's cursor (#1931). Its stream may be archived whole, and a
      // cursor naming it would name an event the hot store does not hold. A purged row has nothing to fold into, and the
      // collective is still not the row's event, so it moves no checkpoint there either (#1957).
      if (@event is global::Whizbang.Core.Messaging.ICollectiveEvent) {
        if (_collectiveReplayApplier is not null && updatedModel is not null && !pendingPurge) {
          if (modelAtLastEvent is null && _snapshotStore is not null && _snapshotOptions?.Value.Enabled == true) {
            modelAtLastEvent = _toSnapshotJson(updatedModel);
          }
          updatedModel = (TModel?)_collectiveReplayApplier.ApplyInMemory(
              typeof(TModel), updatedModel, streamId, @event);
          hasWrittenUpdate = true;
          // #1970: a collective still in flight may yet reach the row live, so this run's snapshot would not say what it holds.
          collectiveInFlight |= _collectiveReplayApplier.IsInFlight(@event);
        }
        processedEvents.Add(envelope);
        eventsProcessed++;
        continue;
      }
      // A stream event after collectives: the row at the last event is the one this event leaves.
      modelAtLastEvent?.Dispose();
      modelAtLastEvent = null;

      // A purged stream stays purged (issue #1027): an event after the purge is skipped, not applied to an
      // empty model, unless its Apply returns Resurrect. The checkpoint still advances past a skipped event.
      if (pendingPurge) {
        var resurrection = _tryResurrect(perspective, streamId, @event, envelope.IsFirstInLocalStream);
        processedEvents.Add(envelope);
        lastSuccessfulEventId = envelope.MessageId.Value;
        lastSuccessfulEventAt = _eventTime(envelope);
        eventsProcessed++;
        if (resurrection is null) {
          purgedEventsSkipped++;
          continue;
        }
        pendingPurge = false;
        resurrected = true;
        updatedModel = resurrection;
        hasWrittenUpdate = true;
        lastSuccessfulEventType = PerspectiveRunnerTypeNames.Recorded(@event.GetType());
        continue;
      }

      // Apply event to model using perspective's pure Apply method
      TModel? appliedModel;
      global::Whizbang.Core.Perspectives.ModelAction action;
      try {
        (appliedModel, action) = _applyEvent(perspective, updatedModel, @event, envelope.IsFirstInLocalStream);
      } catch (Exception applyEx) {
        _logger.LogError(
            applyEx,
            "Error applying {EventType} to {PerspectiveName} stream {StreamId}, skipping event {EventId}",
            eventTypeName,
            perspectiveName,
            streamId,
            envelope.MessageId.Value
        );
        // Advance checkpoint past the failed event so it isn't retried indefinitely
        processedEvents.Add(envelope);
        lastSuccessfulEventId = envelope.MessageId.Value;
        lastSuccessfulEventAt = _eventTime(envelope);
        eventsProcessed++;
        continue;
      }

      // Set span outcome
      eventActivity?.SetTag("whizbang.perspective.action", action.ToString());

      // Handle action from Apply result
      switch (action) {
        case global::Whizbang.Core.Perspectives.ModelAction.Delete:
          // Soft delete: Model should have DeletedAt set by perspective or caller
          // For now, keep the model (it may have been modified by perspective)
          updatedModel = appliedModel ?? updatedModel;
          // A soft delete with no model produces nothing: on a stream with no row it would store the
          // scaffolded default (#1677). With a model, or on a row already produced, it writes as before.
          hasWrittenUpdate |= appliedModel != null;
          break;
        case global::Whizbang.Core.Perspectives.ModelAction.Purge:
          // Hard delete: Mark for purge, skip upsert
          pendingPurge = true;
          purgeEventId = envelope.MessageId.Value;
          purgeEventType = PerspectiveRunnerTypeNames.Recorded(@event.GetType());
          updatedModel = null;
          break;
        default:
          // Normal update or no-change
          if (appliedModel != null) {
            updatedModel = appliedModel;
            hasWrittenUpdate = true;
          }
          // else: null model with None = no change.
          // On an existing stream hasWrittenUpdate started true; on a new stream it stays
          // false until an Apply actually returns a model, which gates the final upsert.
          break;
      }

      // IScopeEvent handling: if the event carries a scope change, apply it
      if (@event is global::Whizbang.Core.IScopeEvent scopeEvent) {
        var proposedScope = scopeEvent.Scope;
        // A perspective that implements IPerspectiveScopeFor decides the final scope. The current scope is the one an
        // earlier scope event in this batch set, else the row's stored scope, never the last message's envelope scope
        // (#1432).
        if (_traits.HasScopeInterface
            && perspective is global::Whizbang.Core.Perspectives.IPerspectiveScopeFor<TModel> scopePerspective) {
          var currentScope = scopeEventScope ?? applyRead.Scope ?? new global::Whizbang.Core.Lenses.PerspectiveScope();
          scopeEventScope = scopePerspective.ApplyScope(currentScope, proposedScope);
        } else {
          scopeEventScope = proposedScope;
        }
      }

      // Track envelope for PostPerspective lifecycle hooks (fire AFTER save completes)
      // Envelope preserved for security context propagation
      processedEvents.Add(envelope);

      // Track success
      lastSuccessfulEventId = envelope.MessageId.Value;
      lastSuccessfulEventAt = _eventTime(envelope);
      lastSuccessfulEventType = PerspectiveRunnerTypeNames.Recorded(@event.GetType());
      eventsProcessed++;
    }

  }

  /// <summary>
  /// Decides the row write a folded batch makes, before it is made: a purge, a write of the folded model, or nothing;
  /// whether this run ends in a snapshot, and the snapshot taken before the write; the position and scope the row is
  /// written with. Shared by the per-pair write and a prepare, so both write the same row.
  /// </summary>
  private async Task _beginRowWriteAsync(ApplyState state, CancellationToken cancellationToken) {
    var eventsProcessed = state.EventsProcessed;
    var pendingPurge = state.PendingPurge;
    var updatedModel = state.UpdatedModel;
    var hasWrittenUpdate = state.HasWrittenUpdate;
    var lastSuccessfulEventId = state.LastSuccessfulEventId;
    var collectiveInFlight = state.CollectiveInFlight;
    var modelAtLastEvent = state.ModelAtLastEvent;

    // Slice 29 instrumentation: capture SaveModelAndCheckpoint + Flush wall time. A
    // production run measured a much lower than expected events/sec drain despite high
    // concurrency configured - need to know whether DB write or in-memory apply or
    // lifecycle dominates per drain.
    state.SaveStartTicks = _timeProvider.GetTimestamp();
    // Issue #1002: whether this run ends in a snapshot, decided before the write, so a model the write strips in
    // place is serialized before it only when a snapshot is actually due.
    var snapshotDue = _snapshotStore is not null && _snapshotOptions?.Value.Enabled == true
        && !pendingPurge && updatedModel is not null && hasWrittenUpdate && lastSuccessfulEventId.HasValue
        && _eventsSinceSnapshot(state.StreamId) + eventsProcessed >= _snapshotCadence(_snapshotOptions.Value).Threshold
        && !collectiveInFlight;
    state.SnapshotDue = snapshotDue;
    if (pendingPurge) {
      // A stream that began this batch purged and stayed so has no row to remove.
      state.RowWrite = state.PurgeEventId.HasValue ? RowWriteKind.Purge : RowWriteKind.None;
    } else if (updatedModel != null && hasWrittenUpdate) {
      state.CheckpointCommitSequence = _recordedCommitSequence(
          await _commitSequenceOfAsync(lastSuccessfulEventId!.Value, state.BatchState, cancellationToken),
          state.ProcessedEvents,
          state.Rebuilding);
      // #1956: the row at its last event when collectives folded after it, so the snapshot holds none of them.
      // Issue #983: taken before the write, which strips a Split class model's promoted fields in place.
      var snapshotBeforeWrite = snapshotDue ? modelAtLastEvent ?? _snapshotBeforeWrite(updatedModel) : null;
      if (snapshotDue) {
        state.ModelAtLastEvent = null;  // handed to the snapshot write
      }
      state.SnapshotBeforeWrite = snapshotBeforeWrite;
      // #2252: a Split class model's fold, taken before the write strips its promoted fields; null for any other model.
      state.FoldBeforeWrite = state.StoredModel is null ? null : _snapshotBeforeWrite(updatedModel);
      // The model's [InheritScope] decides the scope: OnCreate and Always fields on a new row, the Always
      // fields merged into the row's stored scope on an existing one (#1425).
      state.ScopeWrite = _resolveScopeWrite(state.LastScope, state.ApplyRead, state.ModelLoadedFromDb, state.ScopeEventScope);
      state.RowWrite = RowWriteKind.Upsert;
    } else {
      state.RowWrite = RowWriteKind.None;
    }
  }

  /// <summary>Makes the row write <see cref="_beginRowWriteAsync"/> decided, one row at a time: the per-pair write.</summary>
  /// <returns>Whether the row was written or purged.</returns>
  private async Task<bool> _writeRowAsync(ApplyState state, CancellationToken cancellationToken) {
    var streamId = state.StreamId;
    var perspectiveName = state.PerspectiveName;
    if (state.RowWrite == RowWriteKind.Purge) {
      // Hard delete: record the purge FIRST, then remove the row, a crash between the two leaves a marker
      // on a live row (harmless), never a missing row a later event could recreate.
      await _markPurgedAsync(streamId, perspectiveName, state.PurgeEventId, cancellationToken);
      await _perspectiveStore.PurgeAsync(streamId, cancellationToken);
      _logger.LogDebug(
          "Model purged for {PerspectiveName} stream {StreamId}",
          perspectiveName,
          streamId
      );
      return true;
    }
    if (state.RowWrite == RowWriteKind.None) {
      return false;
    }
    var updatedModel = state.UpdatedModel!;
    var hasWrittenUpdate = state.HasWrittenUpdate;
    var modelLoadedFromDb = state.ModelLoadedFromDb;
    var processedEvents = state.ProcessedEvents;
    // The store says whether the row was written: its ordering guard can refuse a write quietly, and a row it
    // left untouched is not a changed row (#1342).
    return await SaveModelAndCheckpointAsync(
              streamId,
              perspectiveName,
              updatedModel,
              hasWrittenUpdate,
              state.LastSuccessfulEventId!.Value,
              _checkpointType(state.LastSuccessfulEventType),
              state.CheckpointCommitSequence,
              _checkpointTime(state.LastSuccessfulEventAt),
              modelLoadedFromDb,
              processedEvents,
              cancellationToken,
              state.ScopeWrite.Scope,
              state.ScopeWrite.ForceUpdate,
              state.RowVersion
          );
  }

  /// <summary>
  /// Prepares the row write <see cref="_writeRowAsync"/> would make, for the batch's write sink: the same metadata, row
  /// start, physical values and Split stripping, handed to the store's preparation instead of its upsert. Null when the
  /// store cannot prepare it. Called only for a model an Apply produced, which the write guard always admits.
  /// </summary>
  private async Task<global::Whizbang.Core.Perspectives.PendingPerspectiveWrite?> _prepareRowWriteAsync(
      ApplyState state, CancellationToken cancellationToken) {
    var streamId = state.StreamId;
    var (metadata, rowStartedAt) = await _rowWriteStartAsync(
        streamId,
        state.LastSuccessfulEventId!.Value,
        _checkpointType(state.LastSuccessfulEventType),
        state.CheckpointCommitSequence,
        _checkpointTime(state.LastSuccessfulEventAt),
        state.ModelLoadedFromDb,
        state.ProcessedEvents,
        cancellationToken);
    using var rowStart = global::Whizbang.Core.Perspectives.PerspectiveRowStart.Begin(streamId, rowStartedAt);
    return _prepareRowWrite(
        streamId, state.UpdatedModel!, metadata, state.ScopeWrite.Scope, state.ScopeWrite.ForceUpdate, state.RowVersion);
  }

  /// <summary>
  /// What follows the row write: forget a purge a resurrection undid, rewind a batch the ordering guard refused (#1608),
  /// flush, snapshot, fire the detached post-perspective stage and return the completion. The end of the per-pair path
  /// and of a completion.
  /// </summary>
  private async Task<PerspectiveCursorCompletion> _finishApplyAsync(
      ApplyState state, bool rowChanged, CancellationToken cancellationToken) {
    var streamId = state.StreamId;
    var perspectiveName = state.PerspectiveName;
    var events = state.Events;
    var backgroundTasks = state.BackgroundTasks;
    var collectiveFence = state.CollectiveFence;
    var eventsProcessed = state.EventsProcessed;
    var lastSuccessfulEventId = state.LastSuccessfulEventId;
    var processedEvents = state.ProcessedEvents;
    var updatedModel = state.UpdatedModel;
    var hasWrittenUpdate = state.HasWrittenUpdate;
    var pendingPurge = state.PendingPurge;
    var purgeEventId = state.PurgeEventId;
    var purgeEventType = state.PurgeEventType;
    var snapshotDue = state.SnapshotDue;
    var snapshotBeforeWrite = state.SnapshotBeforeWrite;
    var modelAtLastEvent = state.ModelAtLastEvent;
    state.SnapshotBeforeWrite = null;  // owned here from now on
    state.ModelAtLastEvent = null;
    using var foldBeforeWrite = state.FoldBeforeWrite;
    state.FoldBeforeWrite = null;
    // Why a rebuild that folded events left the row untouched (#2252)
    var unchangedReason = global::Whizbang.Core.Perspectives.PerspectiveRowUnchangedReason.Unreported;

    // Unit of Work: Save model + checkpoint ONCE at end
    if (eventsProcessed > 0) {
      if (state.RowWrite == RowWriteKind.Upsert) {
        // Forget the purge only once the resurrected row is written.
        if (rowChanged && state.Resurrected && state.StartedPurged) {
          await _clearPurgeAsync(streamId, perspectiveName, cancellationToken);
        }
        // A rebuild's write the ordering guard refused, whether made per pair or by the batch write (#2252).
        if (!rowChanged && state.Rebuilding) {
          unchangedReason = _readRefusedRebuildWrite(foldBeforeWrite, updatedModel!, state.StoredModel);
        }
        // A live write the ordering guard refused folded this batch onto a row that is already past it: the row
        // holds a later event and lacks these (#1608). Reporting the batch applied would retire its work rows with
        // nothing left pending to repair the row, so the stream is re-folded from its earliest event in the batch
        // instead, exactly as for an event below the row's recorded position (#1347). A rebuild's refusal stands:
        // it already folded the whole stream.
        if (!rowChanged && !state.Rebuilding && processedEvents.Count > 0) {
          snapshotBeforeWrite?.Dispose();
          modelAtLastEvent?.Dispose();
          if (backgroundTasks.Count > 0) {
            await Task.WhenAll(backgroundTasks);
          }
          var earliest = global::Whizbang.Core.Perspectives.ReplayEventOrder.Order(processedEvents)[0];
          _logger.LogWarning(
              "[diag.write.refused] {PerspectiveName} stream {StreamId}: the row is past the batch starting at event {EventId}; rewinding so the batch is folded rather than reported applied",
              perspectiveName,
              streamId,
              earliest.MessageId.Value);
          return await _rewindAndRunCoreAsync(
              streamId, perspectiveName, earliest.MessageId.Value, earliest.LocalCommitSequence, cancellationToken);
        }
      } else if (!pendingPurge) {
        // No event produced a model, so there was nothing to write (#2252).
        unchangedReason = global::Whizbang.Core.Perspectives.PerspectiveRowUnchangedReason.NoModel;
      }
      _reportPurgedEventsSkipped(perspectiveName, streamId, state.PurgedEventsSkipped);

      // CRITICAL: Explicitly flush all changes to ensure data is committed and queryable
      // PostgresUpsertStrategy.UpsertPerspectiveRowAsync() calls SaveChangesAsync() internally,
      // but we need to ensure the same DbContext is flushed before PostPerspectiveInline fires.
      // PostPerspectiveInline guarantees data is persisted and immediately queryable.
      await _perspectiveStore.FlushAsync(cancellationToken);
      if (_logger.IsEnabled(LogLevel.Debug)) {
        var saveMs = _timeProvider.GetElapsedTime(state.SaveStartTicks).TotalMilliseconds;
        if (saveMs > 50 || eventsProcessed >= 10) {
          _logger.LogDebug(
            "PERF SaveModelAndCheckpoint {PerspectiveName} stream {StreamId}: {EventCount} events in {SaveMs:F1}ms ({MsPerEvent:F2}ms/evt)",
            perspectiveName, streamId, eventsProcessed, saveMs, saveMs / eventsProcessed);
        }
      }

      _logger.LogDebug(
          "Successfully processed {EventCount} events for {PerspectiveName} stream {StreamId}, checkpoint: {CheckpointEventId}",
          eventsProcessed,
          perspectiveName,
          streamId,
          lastSuccessfulEventId
      );

      // Create snapshot if configured and threshold reached.
      // Require hasWrittenUpdate - a batch of pure ApplyResult.None() on a new stream
      // produces no persisted row, so there's nothing to snapshot.
      if (_snapshotStore is not null && _snapshotOptions?.Value.Enabled == true
          && !pendingPurge && updatedModel is not null && hasWrittenUpdate && lastSuccessfulEventId.HasValue) {
        // The cadence is the perspective's own (_snapshotCadence); whether this run is due was decided before the write.
        var snapshotRetention = _snapshotCadence(_snapshotOptions.Value).Retention;
        _eventsSinceLastSnapshot.AddOrUpdate(streamId, eventsProcessed, (_, count) => count + eventsProcessed);
        if (snapshotDue) {
          // #1963: the collectives mark this snapshot records, or a deferral when a collective of the row moved across
          // the fence's two reads. A deferred snapshot stays due: the count is not reset, so the next apply takes it.
          var collectiveMark = collectiveFence is { } fenceBefore
              ? await _collectiveMarkAfterWriteAsync(fenceBefore, perspectiveName, streamId, cancellationToken)
              : new CollectiveMark(Deferred: false, ThroughSequence: null);
          if (!collectiveMark.Deferred && await _cadenceSnapshotSlotTakenAsync(streamId, perspectiveName, cancellationToken)) {
            // #2456: the boundary snapshot keeps the slot; the count restarts so the slot is not asked again every batch.
            _eventsSinceLastSnapshot.TryRemove(streamId, out _);
          } else if (!collectiveMark.Deferred) {
            // Slice 26.11: resolve commit_sequence for the snapshot anchor so subsequent
            // rewinds can locate the snapshot by commit_sequence (deterministic) rather
            // than event_id (subject to UUIDv7 generation-time race). Null is OK: the
            // commit-sequence-anchored lookup falls back to event_id-anchored on miss.
            // #2379: taken at the event with the highest version the row holds, so the snapshot's version names the
            // events it holds (every one up to it); the last event applied can sit lower when ids and versions disagree.
            var snapshotAt = new LastInStream();
            foreach (var applied in processedEvents) {
              snapshotAt.Offer(applied);
            }
            var snapshotEventId = snapshotAt.Position ?? lastSuccessfulEventId.Value;
            var snapshotCommitSequence = await _eventStore.GetCommitSequenceAsync(
                snapshotEventId, cancellationToken);
            await _snapshotStore.CreateSnapshotAsync(
                streamId, perspectiveName, snapshotEventId,
                snapshotCommitSequence,
                snapshotBeforeWrite ?? _toSnapshotJson(updatedModel), collectiveMark.ThroughSequence, cancellationToken);
            snapshotBeforeWrite = null; // handed to the store
            await _snapshotStore.PruneOldSnapshotsAsync(
                streamId, perspectiveName, snapshotRetention, cancellationToken);
            _eventsSinceLastSnapshot.TryRemove(streamId, out _);
          }
        }
      }
      snapshotBeforeWrite?.Dispose(); // not due this run
      modelAtLastEvent?.Dispose(); // not handed to the snapshot write

      // Fire PostPerspectiveDetached lifecycle hooks AFTER perspective data is flushed
      // PostPerspectiveDetached is for early, non-blocking notification (data committed but checkpoint not yet saved)
      // PostPerspectiveInline fires LATER in PerspectiveWorker after checkpoint commits (guarantees both data + checkpoint are committed)
      // ReceptorInvoker.InvokeAsync() handles ALL security context setup internally
      // Slice 30A's runtime registry - resolved once for the post loop; same instance as the
      // pre block but the variable isn't in scope here, so resolve again. Singleton lookup is
      // cheap. Falls back to the static check when the registry isn't registered.
      var postRuntimeReceptorRegistry = _serviceProvider.GetService<global::Whizbang.Core.Messaging.IReceptorRegistry>();
      foreach (var envelope in processedEvents) {
        // Slice 30A - gate the per-event PostPerspectiveDetached OS-thread spawn on the
        // receptor registry. Production PERF data showed per-event spawn cost dominates
        // multi-event drains: e.g. a 44-event batch would otherwise spawn 44 dedicated
        // OS threads even when no Post receptor is registered for the perspective's
        // event types. The HasReceptors lookup is a HashSet contains check (~50 ns) vs
        // the ~5-10 ms thread spawn it replaces. Consults both registries so runtime
        // registrations (integration-test waits) still fire - see PrePerspective gate above
        // for the same rationale.
        var envelopeType = envelope.Payload.GetType();
        var envelopeTypeName = PerspectiveRunnerTypeNames.ReceptorKey(envelopeType);
        if (!(postRuntimeReceptorRegistry?.GetReceptorsFor(envelopeType, LifecycleStage.PostPerspectiveDetached).Count > 0)
            && !global::Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PostPerspectiveDetached, envelopeTypeName)) {
          continue;
        }
        var context = new LifecycleExecutionContext {
          CurrentStage = LifecycleStage.PostPerspectiveDetached,
          StreamId = streamId,
          PerspectiveType = typeof(TPerspective),
          EventId = envelope.MessageId.Value,
          LastProcessedEventId = lastSuccessfulEventId,
          ProcessingMode = global::Whizbang.Core.Messaging.ProcessingModeAccessor.Current
        };

        // Fire ASYNC hooks (non-blocking - for early notification before checkpoint commits).
        // Use BackgroundStageDispatch.StartLongRunning (dedicated thread) instead of Task.Run
        // for the same reason as PrePerspectiveDetached above - under heavy parallel load,
        // pooled-thread Task.Run continuations get starved and the awaiting Task.WhenAll
        // hangs past the test deadline.
        var postAsyncTask = global::Whizbang.Core.Workers.BackgroundStageDispatch.StartLongRunning(async () => {
          await using var lifecycleScope = _scopeFactory.CreateAsyncScope();
          var receptorInvoker = lifecycleScope.ServiceProvider.GetService<global::Whizbang.Core.Messaging.IReceptorInvoker>();
          if (receptorInvoker is not null) {
            await receptorInvoker.InvokeAsync(envelope, LifecycleStage.PostPerspectiveDetached, context with { CurrentStage = LifecycleStage.PostPerspectiveDetached }, cancellationToken);
          }
        }, cancellationToken);
        backgroundTasks.Add(postAsyncTask);
      }
    }

    // CRITICAL: Wait for all background async lifecycle tasks to complete before returning
    // This ensures async stages (PrePerspectiveDetached, PostPerspectiveDetached) actually execute
    // even under thread pool exhaustion. Without this, Task.Run might not execute due to
    // thread pool saturation, causing lifecycle receptors to never fire.
    if (backgroundTasks.Count > 0) {
      await Task.WhenAll(backgroundTasks);
    }

    var resultStatus = eventsProcessed > 0 ? PerspectiveProcessingStatus.Completed : PerspectiveProcessingStatus.None;

    return new PerspectiveCursorCompletion {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      PerspectiveType = typeof(TPerspective),
      // The last successful event starts at the cursor the batch was read after, so it already falls back to it.
      LastEventId = lastSuccessfulEventId ?? Guid.Empty,
      Status = resultStatus,
      EventsProcessed = eventsProcessed,
      RowChanged = rowChanged,
      UnchangedReason = unchangedReason,
      ProcessedEventIds = events.Select(e => e.MessageId.Value).ToArray(),
      PurgedBy = pendingPurge && purgeEventId.HasValue ? purgeEventType : null
    };
  }

  /// <summary>
  /// The partial-success path of a failed apply: saves the row up to the last event that applied, so the checkpoint
  /// moves past what was folded. The caller rethrows the failure; a failure of this save is thrown in its place.
  /// </summary>
  [SuppressMessage("Major Code Smell", "S2139:Exceptions should be either logged or rethrown but not both", Justification = "The worker that receives the rethrown save failure retries and dead-letters the batch; this Warning is the one line that says the partial-progress save, not the apply, failed (#1976)")]
  private async Task _savePartialProgressAsync(ApplyState state, Exception ex, CancellationToken cancellationToken) {
    var streamId = state.StreamId;
    var perspectiveName = state.PerspectiveName;
    var lastProcessedEventId = state.LastProcessedEventId;
    var lastSuccessfulEventId = state.LastSuccessfulEventId;
    var eventsProcessed = state.EventsProcessed;
    var updatedModel = state.UpdatedModel;
    var hasWrittenUpdate = state.HasWrittenUpdate;
    var processedEvents = state.ProcessedEvents;
    var rebuilding = state.Rebuilding;
    var lastScope = state.LastScope;
    var applyRead = state.ApplyRead;
    var modelLoadedFromDb = state.ModelLoadedFromDb;
    var scopeEventScope = state.ScopeEventScope;
    var lastSuccessfulEventType = state.LastSuccessfulEventType;
    var lastSuccessfulEventAt = state.LastSuccessfulEventAt;
    var rowVersion = state.RowVersion;

    // Partial success: save checkpoint up to last successful event
    if (lastSuccessfulEventId is { } successful && lastProcessedEventId != successful) {
      _logger.LogWarning(
          ex,
          "Error processing events for {PerspectiveName} stream {StreamId} after {EventCount} events. Saving partial progress to checkpoint {CheckpointEventId}",
          perspectiveName,
          streamId,
          eventsProcessed,
          lastSuccessfulEventId
      );

      try {
        // Only save if we have a model AND Apply has produced one (skip if purge was pending
        // or the only events were ApplyResult.None() on a new stream)
        if (updatedModel != null && hasWrittenUpdate) {
          var partialCheckpointCommitSequence = _recordedCommitSequence(
              await _eventStore.GetCommitSequenceAsync(successful, cancellationToken),
              processedEvents,
              rebuilding);
          var partialScopeWrite = _resolveScopeWrite(lastScope, applyRead, modelLoadedFromDb, scopeEventScope);
          await SaveModelAndCheckpointAsync(
              streamId,
              perspectiveName,
              updatedModel,
              hasWrittenUpdate,
              successful,
              _checkpointType(lastSuccessfulEventType),
              partialCheckpointCommitSequence,
              _checkpointTime(lastSuccessfulEventAt),
              modelLoadedFromDb,
              processedEvents,
              cancellationToken,
              partialScopeWrite.Scope,
              partialScopeWrite.ForceUpdate,
              rowVersion
          );
        }
      } catch (Exception saveEx) {
        // #1976: rethrown to the worker, which retries the batch and dead-letters it at the bound, so a Warning.
        _logger.LogWarning(
            saveEx,
            "Failed to save partial progress for {PerspectiveName} stream {StreamId}",
            perspectiveName,
            streamId
        );
        // Re-throw - we couldn't save partial progress
        throw;
      }
    } else {
      // #1976: rethrown to the worker, which retries the batch and dead-letters it at the bound, so a Warning.
      _logger.LogWarning(
          ex,
          "Error processing first event for {PerspectiveName} stream {StreamId}, no progress to save",
          perspectiveName,
          streamId
      );
    }
  }

  /// <summary>
  /// Applies an event to the model using the perspective's synchronous Apply method.
  /// AOT-compatible: uses switch statement instead of reflection.
  /// Apply methods are pure functions that return new model state and optional action.
  /// The event's facts (<see cref="global::Whizbang.Core.Perspectives.ApplyContext"/>) are current for the Apply and
  /// restored when it returns or throws (#1878).
  /// </summary>
  /// <returns>Tuple of (model, action) where action indicates Delete/Purge/None.</returns>
  private (TModel?, global::Whizbang.Core.Perspectives.ModelAction) _applyEvent(
      TPerspective perspective,
      TModel? currentModel,
      IEvent @event,
      bool isFirstEventInStream) {

    using var applyContext = global::Whizbang.Core.Perspectives.ApplyContext.Enter(isFirstEventInStream);
    return ApplyEventCore(perspective, currentModel, @event);
  }

  /// <summary>
  /// Applies an event to a purged stream (issue #1027). Returns the model to recreate the row with when the
  /// Apply returned <see cref="global::Whizbang.Core.Perspectives.ModelAction.Resurrect"/> with a model; null
  /// means the event is skipped. The Apply sees an empty model, as on a new stream, and its result is discarded
  /// unless it resurrects. An Apply that throws on a purged stream is a skip, not a failure: the row it would
  /// have changed is gone.
  /// </summary>
  private TModel? _tryResurrect(
      TPerspective perspective,
      Guid streamId,
      IEvent @event,
      bool isFirstEventInStream) {
    try {
      var (model, action) = _applyEvent(perspective, CreateEmptyModel(streamId), @event, isFirstEventInStream);
      return action == global::Whizbang.Core.Perspectives.ModelAction.Resurrect ? model : null;
    } catch (Exception applyEx) when (applyEx is not OperationCanceledException) {
      _logger.LogDebug(
          applyEx,
          "Apply of {EventType} threw on purged stream {StreamId}; the event is skipped",
          global::Whizbang.Core.TypeNameFormatter.SimpleName(@event.GetType()),
          streamId
      );
      return null;
    }
  }

  /// <summary>The purge markers, when a driver registered them; without them a purge is honored within its batch only.</summary>
  private global::Whizbang.Core.Perspectives.IPerspectivePurgeMarkerStore? _purgeMarkers =>
      _serviceProvider.GetService<global::Whizbang.Core.Perspectives.IPerspectivePurgeMarkerStore>();

  /// <summary>
  /// The purge probe of a missing row: false for a stream the batch knows is new, the batch's answer where it has one,
  /// and otherwise the stream's own query.
  /// </summary>
  private async Task<bool> _isPurgedForApplyAsync(
      Guid streamId, string perspectiveName, global::Whizbang.Core.Perspectives.IPerspectiveBatchState? batchState,
      CancellationToken cancellationToken) {
    if (batchState is not null) {
      if (batchState.IsKnownNew(streamId, perspectiveName)) {
        return false;
      }
      if (_purgeMarkers is { } markers
          && await batchState.IsPurgedAsync(markers, streamId, perspectiveName, cancellationToken) is { } batched) {
        return batched;
      }
    }
    return await _isPurgedAsync(streamId, perspectiveName, cancellationToken);
  }

  /// <summary>
  /// The history probe of a missing row of a perspective whose rows can be evicted: false for a stream the batch knows
  /// is new, the batch's answer where it has one, and otherwise the stream's own query.
  /// </summary>
  private async Task<bool> _hasHistoryBeforeAsync(
      Guid streamId, string perspectiveName, Guid beforeEventId,
      global::Whizbang.Core.Perspectives.IPerspectiveBatchState? batchState, CancellationToken cancellationToken) {
    if (batchState is not null) {
      if (batchState.IsKnownNew(streamId, perspectiveName)) {
        return false;
      }
      if (await batchState.HasHistoryBeforeAsync(
              _eventStore, streamId, perspectiveName, beforeEventId, _handledEventTypes, cancellationToken) is { } batched) {
        return batched;
      }
    }
    return await _eventStore.HasStreamEventsBeforeAsync(streamId, beforeEventId, _handledEventTypes, cancellationToken);
  }

  /// <summary>
  /// The commit sequence the row's write records for its last applied event: the batch's one read for all its pairs
  /// where it planned this event (#2246), otherwise the event's own read.
  /// </summary>
  private async Task<long?> _commitSequenceOfAsync(
      Guid eventId, global::Whizbang.Core.Perspectives.IPerspectiveBatchState? batchState, CancellationToken cancellationToken) {
    if (batchState is not null
        && await batchState.CommitSequenceOfAsync(_eventStore, eventId, cancellationToken) is { } batched) {
      return batched.Value;
    }
    return await _eventStore.GetCommitSequenceAsync(eventId, cancellationToken);
  }

  private async Task<bool> _isPurgedAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken) {
    var markers = _purgeMarkers;
    return markers is not null && await markers.IsPurgedAsync(streamId, perspectiveName, cancellationToken);
  }

  private async Task _markPurgedAsync(Guid streamId, string perspectiveName, Guid? purgeEventId, CancellationToken cancellationToken) {
    var markers = _purgeMarkers;
    if (markers is not null) {
      await markers.MarkPurgedAsync(streamId, perspectiveName, purgeEventId, cancellationToken);
    }
  }

  private async Task _clearPurgeAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken) {
    var markers = _purgeMarkers;
    if (markers is not null) {
      await markers.ClearAsync(streamId, perspectiveName, cancellationToken);
    }
  }

  /// <summary>Logs and counts the events a purged stream skipped (whizbang.perspective.purged_events_skipped).</summary>
  private void _reportPurgedEventsSkipped(string perspectiveName, Guid streamId, int skipped) {
    if (skipped == 0) {
      return;
    }
    _logger.LogInformation(
        "Skipped {SkippedCount} event(s) for {PerspectiveName} stream {StreamId}: the stream was purged and no Apply resurrected it",
        skipped,
        perspectiveName,
        streamId
    );
    _serviceProvider.GetService<PerspectiveMetrics>()?.PurgedEventsSkipped.Add(
        skipped, new KeyValuePair<string, object?>("perspective_name", perspectiveName));
  }

  /// <summary>
  /// Why a rebuild's write was refused (#2252): the fold compared with the row as it was stored, both serialized by the
  /// snapshot serializer. Equal documents mean the read model was already right. A Split class model's fold is the
  /// document taken before the write stripped its promoted fields.
  /// </summary>
  private static global::Whizbang.Core.Perspectives.PerspectiveRowUnchangedReason _readRefusedRebuildWrite(
      JsonDocument? foldBeforeWrite, TModel folded, TModel? stored) {
    if (stored is null) {
      return global::Whizbang.Core.Perspectives.PerspectiveRowUnchangedReason.WriteRefused;
    }
    using var foldAfter = foldBeforeWrite is null ? _toSnapshotJson(folded) : null;
    using var storedDocument = _toSnapshotJson(stored);
    return global::Whizbang.Core.Perspectives.PerspectiveRowUnchanged.OfRefusedWrite(foldBeforeWrite ?? foldAfter, storedDocument);
  }

  /// <summary>
  /// Moves the replay frontier (max event id, max local commit sequence) past an envelope.
  /// </summary>
  /// <summary>
  /// The commit sequence a fold records as its position. A live apply records its last event's; a rebuild records
  /// the highest among the events it folded (#1352), as the rewind's frontier does. A rebuild folds a stream in
  /// version order, and where rows were stamped after the fact the last version can carry the lowest commit sequence
  /// on the stream: recording it would put the row's position below events it already folded, and the store's
  /// ordering guard refuses a write whose position sits below the stored one, so the corrected row was never written.
  /// </summary>
  private static long? _recordedCommitSequence(
      long? lastEventsCommitSequence,
      List<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>> folded,
      bool rebuilding) {
    if (!rebuilding) {
      return lastEventsCommitSequence;
    }
    var highest = lastEventsCommitSequence;
    foreach (var envelope in folded) {
      if (envelope.LocalCommitSequence is long seq && (highest is null || seq > highest.Value)) {
        highest = seq;
      }
    }
    return highest;
  }

  private static (Guid? EventId, long? CommitSequence) _advanceFrontier(
      Guid? frontierEventId,
      long? frontierCommitSequence,
      MessageEnvelope<IEvent> envelope) {
    if (frontierEventId is null || string.CompareOrdinal(
          envelope.MessageId.Value.ToString("D"), frontierEventId.Value.ToString("D")) > 0) {
      frontierEventId = envelope.MessageId.Value;
    }
    if (envelope.LocalCommitSequence is long seq
        && (frontierCommitSequence is null || seq > frontierCommitSequence.Value)) {
      frontierCommitSequence = seq;
    }
    return (frontierEventId, frontierCommitSequence);
  }


  /// <summary>
  /// When an event happened, for the checkpoint it moves: its last hop's time, or now for an event that carries no hop.
  /// One rule for every fold (#2469).
  /// </summary>
  private static DateTime _eventTime(MessageEnvelope<IEvent> envelope) =>
      envelope.Hops is { Count: > 0 } ? envelope.Hops[^1].Timestamp.UtcDateTime : DateTime.UtcNow;

  /// <summary>The event type a row write records: the last applied event's, or empty when no event set one.</summary>
  private static string _checkpointType(string? lastSuccessfulEventType) => lastSuccessfulEventType ?? string.Empty;

  /// <summary>The time a row write records: the last applied event's, or now when no event set one.</summary>
  private static DateTime _checkpointTime(DateTime? lastSuccessfulEventAt) => lastSuccessfulEventAt ?? DateTime.UtcNow;

  /// <summary>
  /// The scope this apply writes, decided by the model's [InheritScope] over the row the apply read.
  /// </summary>
  private global::Whizbang.Core.Lenses.ScopeWrite _resolveScopeWrite(
      PerspectiveScope? lastScope,
      global::Whizbang.Core.Perspectives.PerspectiveApplyRead applyRead,
      bool rowExists,
      PerspectiveScope? scopeEventScope)
    => global::Whizbang.Core.Lenses.ScopeInheritance.Resolve(
        lastScope, applyRead.Scope, rowExists, _traits.InheritScopeOnCreate, _traits.InheritScopeAlways, scopeEventScope);

  /// <summary>
  /// Saves the model atomically.
  /// This is the ONLY place where database writes occur (unit of work pattern).
  /// Checkpoint persistence happens through the return value of RunAsync - the
  /// PerspectiveWorker collects checkpoint completions and hands them to
  /// PerspectiveCompletionFlushWorker, which persists them via IWorkCoordinator.CompletePerspectiveAsync
  /// (the complete_perspective SQL function).
  /// </summary>
  /// <returns>
  /// Whether the row was written. False when the store's ordering guard refused the write, which leaves the row
  /// untouched and must not be reported as a changed row (#1342).
  /// </returns>
  /// <remarks>
  /// Internal so its write guard (#1677) is tested directly (#2469): every caller passes a model an Apply produced, so
  /// the guard's refusal cannot be reached through them, and it stays because a write of the scaffolded default would
  /// store a row no event made.
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/RunnerBase/RunnerBaseSnapshotTests.cs:TheRowWrite_RefusesAModelNoApplyProduced_AndWritesNothingAsync</tests>
  internal async Task<bool> SaveModelAndCheckpointAsync(
      Guid streamId,
      string perspectiveName,
      TModel model,
      bool producedByApply,
      Guid checkpointEventId,
      string checkpointEventType,
      long? checkpointCommitSequence,
      DateTime checkpointEventAt,
      bool rowExists,
      IReadOnlyList<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>> folded,
      CancellationToken cancellationToken,
      PerspectiveScope? scope = null,
      bool forceUpdateScope = false,
      global::Whizbang.Core.Perspectives.PerspectiveRowVersion expectedVersion = default) {

    // The last check before the write (#1677): a row is stored only from a model an Apply produced, or the stored
    // row or snapshot an apply continues from, never from the scaffolded default built as Apply input.
    if (!global::Whizbang.Core.Perspectives.PerspectiveRowWriteGuard.AllowsWrite(producedByApply, _logger, perspectiveName, streamId)) {
      return false;
    }

    var (metadata, rowStartedAt) = await _rowWriteStartAsync(
        streamId, checkpointEventId, checkpointEventType, checkpointCommitSequence, checkpointEventAt, rowExists, folded,
        cancellationToken);
    using var rowStart = global::Whizbang.Core.Perspectives.PerspectiveRowStart.Begin(streamId, rowStartedAt);

    // Upsert model (insert or update): metadata.EventId records the last applied event so the next run can skip
    // already-applied events (see RunWithEventsAsync idempotency guard), and expectedVersion lands the write only on the
    // row version the apply read (issue #928). The result says whether the row was written: the store's ordering guard
    // can refuse it quietly (#1342).
    var physicalFieldValues = PhysicalFieldValues(model);
    if (physicalFieldValues is not null) {
      // A model with physical fields writes their values to their columns; a Split model's document omits them.
      model = StripPhysicalFields(model);
      return await _perspectiveStore.TryUpsertWithPhysicalFieldsAsync(
          streamId,
          model,
          physicalFieldValues,
          scope,
          forceUpdateScope,
          metadata,
          expectedVersion,
          cancellationToken
      );
    }
    if (scope != null) {
      return await _perspectiveStore.TryUpsertAsync(
          streamId,
          model,
          scope,
          forceUpdateScope,
          metadata,
          expectedVersion,
          cancellationToken
      );
    }
    return await _perspectiveStore.TryUpsertAsync(
        streamId,
        model,
        new global::Whizbang.Core.Lenses.PerspectiveScope(),
        false,
        metadata,
        expectedVersion,
        cancellationToken
    );
  }

  /// <summary>
  /// What every write of a folded row is stamped with, whichever statement writes it (#2246): the metadata of the last
  /// applied event, and the time the row started, which an inserted row is dated at.
  /// </summary>
  private async Task<(global::Whizbang.Core.Lenses.PerspectiveMetadata Metadata, DateTime? RowStartedAt)> _rowWriteStartAsync(
      Guid streamId,
      Guid checkpointEventId,
      string checkpointEventType,
      long? checkpointCommitSequence,
      DateTime checkpointEventAt,
      bool rowExists,
      IReadOnlyList<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>> folded,
      CancellationToken cancellationToken) {
    // Build metadata that captures the last applied event's identity AND commit_sequence.
    // The runner reads metadata back on the next run (ReadForApplyAsync, or GetMetadataByStreamIdAsync) to filter
    // out already-applied events (idempotency across worker crashes between row upsert and
    // cursor advance). CommitSequence is the load-bearing field for that filter: UUIDv7
    // event_ids can invert under concurrent emission, so event_id-only comparison silently
    // drops late-delivered events with smaller event_id but larger commit_sequence (a
    // production bug). When commit_sequence is available on both sides, the filter prefers it.
    var metadata = new global::Whizbang.Core.Lenses.PerspectiveMetadata {
      EventId = checkpointEventId.ToString("D"),
      EventType = checkpointEventType,
      Timestamp = checkpointEventAt,
      CommitSequence = checkpointCommitSequence
    };

    // A row this write may insert is dated at the first event folded into it (#2154), not at the write's own time, which
    // is the last folded event's and goes to updated_at; or at its stream's recorded start once the stream no longer holds
    // its first event (#2152, the superseding reaper or a prefix truncate removed it). The event store is asked only when
    // the row is not known to exist and the fold does not begin with the stream's first event; an update never moves
    // created_at.
    // <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
    var rowStartedAt = await global::Whizbang.Core.Perspectives.PerspectiveRowStart.ResolveAsync(
        _eventStore, streamId, rowExists, folded, cancellationToken);
    return (metadata, rowStartedAt);
  }

  /// <summary>
  /// Prepares the write <see cref="SaveModelAndCheckpointAsync"/> makes, for a set-based write (#2246): the same
  /// physical values and Split stripping, handed to the store's preparation. Called inside the row start
  /// <see cref="_rowWriteStartAsync"/> resolved, as the upsert is.
  /// </summary>
  private global::Whizbang.Core.Perspectives.PendingPerspectiveWrite? _prepareRowWrite(
      Guid streamId,
      TModel model,
      global::Whizbang.Core.Lenses.PerspectiveMetadata metadata,
      PerspectiveScope? scope,
      bool forceUpdateScope,
      global::Whizbang.Core.Perspectives.PerspectiveRowVersion expectedVersion) {
    // Prepare the row write the upsert would make, with the same document, physical values and scope; the batch's write
    // sink writes it with the rest of the batch.
    var physicalFieldValues = PhysicalFieldValues(model);
    if (physicalFieldValues is not null) {
      model = StripPhysicalFields(model);
      return _perspectiveStore.PrepareWrite(
          streamId,
          model,
          scope ?? new global::Whizbang.Core.Lenses.PerspectiveScope(),
          forceUpdateScope,
          metadata,
          expectedVersion,
          physicalFieldValues
      );
    }
    // The upsert without physical fields writes a null scope as an empty one that forces no update.
    return _perspectiveStore.PrepareWrite(
        streamId,
        model,
        scope ?? new global::Whizbang.Core.Lenses.PerspectiveScope(),
        scope != null && forceUpdateScope,
        metadata,
        expectedVersion,
        null
    );
  }

  /// <summary>What an evicted ephemeral row can be rebuilt from (issues #1483, #1506).</summary>
  private enum EvictedRowFloor {
    /// <summary>No snapshot of the stream is placed before the incoming batch.</summary>
    None,
    /// <summary>A snapshot is placed before the batch and the current serialization version reads it.</summary>
    Readable,
    /// <summary>A snapshot is placed before the batch but was written in a serialization version that cannot be read.</summary>
    Stale,
  }

  /// <summary>
  /// The snapshot floor an ephemeral perspective's evicted row can be rebuilt from: the snapshot placed before
  /// <paramref name="beforeEventId"/> in stream order (issue #1483), and whether it can be read (issue #1506). It is
  /// the same lookup and the same read the rewind core makes, so <see cref="EvictedRowFloor.Readable"/> here means
  /// the rewind will restore from it. A stale floor is reported apart from a missing one so the warning names it.
  /// </summary>
  private async Task<(EvictedRowFloor State, Guid SnapshotEventId)> _evictedRowFloorAsync(
      Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken cancellationToken) {
    if (_snapshotStore is null) {
      return (EvictedRowFloor.None, Guid.Empty);
    }
    var floor = await _snapshotStore.GetLatestSnapshotBeforeEventInStreamOrderAsync(
        streamId, perspectiveName, beforeEventId, cancellationToken);
    if (!floor.HasValue) {
      return (EvictedRowFloor.None, Guid.Empty);
    }
    var state = _fromSnapshotJson(floor.Value.SnapshotData) is null ? EvictedRowFloor.Stale : EvictedRowFloor.Readable;
    return (state, floor.Value.SnapshotEventId);
  }

  /// <inheritdoc/>
  public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
      Guid streamId,
      string perspectiveName,
      Guid triggeringEventId,
      CancellationToken cancellationToken = default)
    => RewindAndRunAsync(streamId, perspectiveName, triggeringEventId, triggeringCommitSequence: null, cancellationToken);

  /// <inheritdoc/>
  public async Task<PerspectiveCursorCompletion> RewindAndRunAsync(
      Guid streamId,
      string perspectiveName,
      Guid triggeringEventId,
      long? triggeringCommitSequence,
      CancellationToken cancellationToken = default) {

    // Same-pod Apply serialization (rewind-vs-live race fix). Acquired BEFORE
    // the snapshot read so the in-memory replay window cannot interleave with
    // a live drain Apply on the same (streamId, perspectiveName).
    await using IAsyncDisposable? __applyLock = _applyCoordinator is null
        ? null
        : await _applyCoordinator.AcquireAsync(streamId, perspectiveName, cancellationToken).ConfigureAwait(false);

    return await _rewindAndRunCoreAsync(
        streamId, perspectiveName, triggeringEventId, triggeringCommitSequence, cancellationToken);
  }

  /// <summary>
  /// The rewind body, ASSUMING the apply lock is already held. Two callers: the public
  /// RewindAndRunAsync (acquires the lock above) and RunAsync's resurrection-on-wake branch
  /// (which runs under the lock RunAsync itself acquired - re-acquiring here would deadlock).
  /// </summary>
  private async Task<PerspectiveCursorCompletion> _rewindAndRunCoreAsync(
      Guid streamId,
      string perspectiveName,
      Guid triggeringEventId,
      long? triggeringCommitSequence,
      CancellationToken cancellationToken = default) {

    Guid? replayFromEventId = null;
    TModel? snapshotModel = null;
    JsonDocument? snapshotData = null;
    var hasSnapshot = false;
    // #1738: the snapshot's version when an archive covers the snapshot, so the replay reads the archived events after it.
    long? readThroughArchiveAfterVersion = null;
    // #1963: the commit sequence through which the snapshot's row held its collectives, when the snapshot records it.
    long? collectivesThroughSequence = null;
    var startedAt = DateTimeOffset.UtcNow;
    // Without a snapshot floor an ephemeral perspective does not replay at all (the drop below), so the "performing full
    // replay" warnings would describe a replay that never runs. The drop's own warning is the one line it logs (#1862).
    var replaysWithoutAFloor = !_traits.IsEphemeral;
    // The lines that announce the replay, logged once the rewind knows it replays: a rewind that needs archived history
    // whose archive was deleted (#1964) refuses instead, and its refusal is the one line it logs.
    Action? announceReplay = null;

    if (_snapshotStore is not null) {
      // The snapshot has to sit before the triggering event IN THE ORDER THE REPLAY FOLDS, because the rewind
      // restores it and replays forward from there. A snapshot is taken at the event with the highest version it
      // holds and holds every event up to that version, so the store selects the newest one below the trigger's
      // version (commit sequence and event id run backward against the stream for retroactively stamped rows and
      // can name a snapshot later than the trigger, #1355). Version order is the replay's order only for a stream
      // with no origin to deal, though: a single-origin stream replays in origin order, which can put an event
      // above the snapshot's version before one it holds. Restoring such a snapshot folds the earlier event after
      // the later one, so the floor is judged by the replay's own order (ReplayFloor, #2379): everything the
      // snapshot holds must come first in that order, and the trigger after it.
      //
      // There is deliberately no fallback to any other floor or order. A fallback is not a safety net here, it is
      // the same wrong answer arriving one branch later: it fires exactly when the floor cannot be placed. A full
      // replay costs more and reproduces the row; a wrong restore point leaves a read model nothing is pending to
      // repair.
      var snapshot = await _snapshotStore.GetSnapshotFloorAsync(
          streamId, perspectiveName, triggeringEventId, cancellationToken);

      // #1738: a snapshot an archive covers, but that records no version, cannot say where the archive's events after it
      // begin, so it is not used: the replay starts from the stream's start, which reads through the archive by its gap.
      if (snapshot?.ArchivedThroughVersion is not null && snapshot.SnapshotVersion is null) {
        var coveredSnapshotEventId = snapshot.SnapshotEventId;
        announceReplay = () => _logger.LogWarning(
            "Snapshot of {PerspectiveName} stream {StreamId} at {SnapshotEventId} is covered by an archive and records no stream version; performing full replay through the archive due to late event {TriggeringEventId}",
            perspectiveName, streamId, coveredSnapshotEventId, triggeringEventId);
        snapshot = null;
      }

      if (snapshot is not null) {
        snapshotModel = _fromSnapshotJson(snapshot.SnapshotData);
        // #2379: a readable snapshot is a floor only when the events it holds come first in the replay's order. A snapshot
        // an archive covers, of a stream whose archive was deleted, is refused below without reading anything (#1964).
        if (snapshotModel is not null
            && (snapshot.ArchivedThroughVersion is null || await _deletedArchiveExtentAsync(streamId, cancellationToken) is null)
            && !global::Whizbang.Core.Perspectives.ReplayFloor.Qualifies(
              await _streamAsReplayedAsync(streamId, cancellationToken), snapshot.SnapshotVersion, triggeringEventId)) {
          var refusedSnapshotEventId = snapshot.SnapshotEventId;
          var refusedSnapshotVersion = snapshot.SnapshotVersion;
          announceReplay = () => _logger.LogWarning(
              "Snapshot of {PerspectiveName} stream {StreamId} at {SnapshotEventId} (stream version {SnapshotVersion}) does not hold only events that come before late event {TriggeringEventId} in replay order; performing full replay",
              perspectiveName, streamId, refusedSnapshotEventId, refusedSnapshotVersion, triggeringEventId);
          snapshotModel = null;
          snapshot = null;
          replaysWithoutAFloor = false;  // the refusal above is the one line this replay logs
        }
      }

      if (snapshot is not null) {
        if (snapshotModel is not null) {
          replayFromEventId = snapshot.SnapshotEventId;
          snapshotData = snapshot.SnapshotData;
          hasSnapshot = true;
          readThroughArchiveAfterVersion = snapshot.ArchivedThroughVersion is null ? null : snapshot.SnapshotVersion;
          collectivesThroughSequence = snapshot.CollectivesThroughSequence;
          var restoredFrom = snapshot;
          announceReplay = () => _logger.LogWarning(
              "Restoring {PerspectiveName} stream {StreamId} from snapshot at {SnapshotEventId} (stream version {SnapshotVersion}) due to late event {TriggeringEventId} (detected at commit sequence {TriggeringCommitSequence})",
              perspectiveName, streamId, restoredFrom.SnapshotEventId, restoredFrom.SnapshotVersion, triggeringEventId, triggeringCommitSequence);
        } else if (replaysWithoutAFloor) {
          announceReplay = () => _logger.LogWarning(
              "Snapshot for {PerspectiveName} stream {StreamId} has a stale serialization version; performing full replay due to late event {TriggeringEventId}",
              perspectiveName, streamId, triggeringEventId);
        }
      } else if (replaysWithoutAFloor) {
        announceReplay += () => _logger.LogWarning(
            "No snapshot of {PerspectiveName} stream {StreamId} is placed before event {TriggeringEventId} (detected at commit sequence {TriggeringCommitSequence}); performing full replay",
            perspectiveName, streamId, triggeringEventId, triggeringCommitSequence);
      }
    } else if (replaysWithoutAFloor) {
      announceReplay = () => _logger.LogWarning(
          "Snapshot store not available for {PerspectiveName} stream {StreamId}, performing full replay due to late event {TriggeringEventId}",
          perspectiveName, streamId, triggeringEventId);
    }

    // Fire PerspectiveRewindStarted system event as System user
    // These events are marked [AuditEvent(Exclude = true)] to skip the audit pipeline
    // which requires security context that doesn't exist during background rewind.
    //
    // One id per rewind, minted here and shared by the Started/Completed pair, the way a rebuild
    // mints one per run. It is the stream these two events are written to. Passing the rewound
    // stream here instead wrote them into the application stream itself, where they took a version
    // and a commit sequence and turned up in every later replay, audit, stream read and version
    // count of that aggregate (#1360). The stream that was rewound travels as a plain property.
    // Time-ordered so a reader can sort rewinds without joining anything.
    Guid rewindStreamId = global::Whizbang.Core.ValueObjects.TrackedGuid.New();
    var dispatcher = _serviceProvider.GetService<IDispatcher>();
    if (dispatcher is not null) {
      try {
        await dispatcher.AsSystem().ForAllTenants().PublishAsync(new PerspectiveRewindStarted(
            rewindStreamId, streamId, perspectiveName, triggeringEventId, replayFromEventId, hasSnapshot, startedAt));
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        _logger.LogDebug(ex, "Failed to publish PerspectiveRewindStarted for {PerspectiveName} stream {StreamId}",
          perspectiveName, streamId);
      }
    }

    // In-memory replay: apply all events without intermediate DB writes
    // Lenses see pre-replay data until the single atomic write at the end
    PerspectiveCursorCompletion result;
    if (_traits.IsEphemeral && !hasSnapshot) {
      // Ephemeral out-of-grace straggler. No snapshot floor survives before this late event, and
      // this stream's consumed bodies have been reaped (wh_event_body rows deleted, pointer-only).
      // _runFromModelAsync(null model, null anchor) would replay from event zero over reaped/absent
      // bodies and silently corrupt the authoritative model. The reorder window (grace + snapshot
      // floor) was exceeded, so the accepted loss is that this straggler does not rewind: keep the
      // current out-of-order-but-present model and do NOT move the cursor. LastEventId = Guid.Empty
      // is the "nothing replayed" completion: ReportPerspectiveCompletionAsync skips the checkpoint
      // write on it, so the cursor stays where it is (never rewound backward to the straggler).
      // DroppedStragglers names the straggler so the worker retires its work once; reporting only
      // "nothing replayed" left it pending, and every claim dropped it again, without end (#1862).
      // <docs>fundamentals/events/ephemeral-events</docs>
      _logger.LogWarning(
          "Skipping ephemeral rewind for {PerspectiveName} stream {StreamId}: late event {TriggeringEventId} has no readable snapshot floor and this stream's ephemeral bodies are reaped; a full replay would corrupt the authoritative model. Straggler dropped: out-of-order rewind window exceeded. Its work is retired and the cursor is not moved.",
          perspectiveName, streamId, triggeringEventId);
      result = _droppedStraggler(streamId, perspectiveName, triggeringEventId);
    } else if ((!hasSnapshot || readThroughArchiveAfterVersion is not null)
        && await _deletedArchiveExtentAsync(streamId, cancellationToken) is { } deletedExtent) {
      // #1964: the replay needs archived history (from the stream's start it reads the archived prefix through the gap;
      // from a snapshot an archive covers it reads the archived events after it), and that archive was deleted. A replay
      // would fold the hot events alone and write a partial row as the whole. A rewind always reads through the archive,
      // as a rebuild that opts in to archived data does, so it refuses as that rebuild skips the stream (#1958): the row
      // and the cursor are left as they are, and the late event is named dropped so the worker retires its work instead
      // of refusing it again on every claim. A rewind from a snapshot no archive covers never reads the archive and
      // never asks.
      // <docs>fundamentals/perspectives/rewind#deleted-archive</docs>
      _logger.LogWarning(
          "Refusing rewind of {PerspectiveName} stream {StreamId} for late event {TriggeringEventId}: the replay needs the stream's archived history and its archive was deleted ({ArchivedExtent}), so a replay would fold only the hot events. The row and the cursor are left as they are, and the late event is dropped.",
          perspectiveName, streamId, triggeringEventId, deletedExtent);
      result = _droppedStraggler(streamId, perspectiveName, triggeringEventId);
    } else {
      announceReplay?.Invoke();
      result = await _replayCheckedAsync(
          streamId, perspectiveName, snapshotModel, snapshotData,
          new ReplayFrom(replayFromEventId, readThroughArchiveAfterVersion, collectivesThroughSequence), cancellationToken);
    }

    // Fire PerspectiveRewindCompleted system event
    if (dispatcher is not null) {
      try {
        await dispatcher.AsSystem().ForAllTenants().PublishAsync(new PerspectiveRewindCompleted(
            rewindStreamId, streamId, perspectiveName, triggeringEventId, result.LastEventId,
            result.EventsProcessed,
            startedAt, DateTimeOffset.UtcNow));
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        _logger.LogDebug(ex, "Failed to publish PerspectiveRewindCompleted for {PerspectiveName} stream {StreamId}",
          perspectiveName, streamId);
      }
    }

    return result;
  }

  /// <summary>
  /// Every event of the stream this perspective folds, as a full replay reads it, so
  /// <see cref="global::Whizbang.Core.Perspectives.ReplayFloor"/> can place the snapshot in the replay's order (#2379). A
  /// body reaped from an ephemeral stream is not read (consumed, snapshot-covered history), and an archived prefix is
  /// read only where the store reads through the archive; events missing from the read are below the snapshot and held
  /// by it, so the check places the snapshot among the events it can see.
  /// </summary>
  private async Task<IReadOnlyList<MessageEnvelope<IEvent>>> _streamAsReplayedAsync(
      Guid streamId, CancellationToken cancellationToken) {
    var stream = new List<MessageEnvelope<IEvent>>();
    await foreach (var envelope in _eventStore.ReadPolymorphicAsync(streamId, null, _handledEventTypes, cancellationToken)) {
      stream.Add(envelope);
    }
    return stream;
  }

  /// <summary>
  /// The completion of a rewind that replayed nothing and dropped its late event: the cursor is not moved (an empty last
  /// event skips the checkpoint write), nothing was written, and the late event is named so the worker retires its work.
  /// </summary>
  private static PerspectiveCursorCompletion _droppedStraggler(Guid streamId, string perspectiveName, Guid triggeringEventId) =>
      new() {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        PerspectiveType = typeof(TPerspective),
        LastEventId = Guid.Empty,
        Status = PerspectiveProcessingStatus.None,
        EventsProcessed = 0,
        RowChanged = false,  // the rewind was skipped, so nothing was written
        DroppedStragglers = [triggeringEventId],
      };

  /// <summary>
  /// How much of the stream's archive was deleted (#1964), from the stream lifecycle ledger: the extent when the stream
  /// lost archived history, or null when it did not, or when the host registers no ledger (it has no archive).
  /// </summary>
  private async Task<global::Whizbang.Core.Archival.ArchivedStreamExtent?> _deletedArchiveExtentAsync(
      Guid streamId, CancellationToken cancellationToken) {
    var ledger = _serviceProvider.GetService<global::Whizbang.Core.Archival.IArchivedStreamExtents>();
    if (ledger is null) {
      return null;
    }
    var extents = await ledger.GetExtentsAsync([streamId], cancellationToken);
    return extents.TryGetValue(streamId, out var extent)
        && extent is global::Whizbang.Core.Archival.ArchivedStreamExtent.PrefixDeleted
          or global::Whizbang.Core.Archival.ArchivedStreamExtent.WholeDeleted
      ? extent
      : null;
  }

  /// <summary>
  /// The rewind's replay, retried when its write is refused because the row moved (#1863). The replay writes against the
  /// row version it read before it read any event, as a live apply writes against the version it read (#928). A writer
  /// that folds a newer event into the row while the rewind replays moves that version, and the store refuses the
  /// rewind's write with <see cref="global::Whizbang.Core.Perspectives.PerspectiveRowConflictException"/>: the replay runs
  /// again from the same restore point, now reading the newer event too, so the row ends with the rewind's correction
  /// and the newer event. Bounded like the live apply's retry; a row that moves under every replay fails the rewind,
  /// which the worker counts and retries.
  /// </summary>
  /// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveRewindWriteCheckTests.cs</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveLateEventApplyTests.cs:ARowThatMovesWhileARewindReplays_EndsWithTheCorrectionAndTheNewerEventAsync</tests>
  private async Task<PerspectiveCursorCompletion> _replayCheckedAsync(
      Guid streamId,
      string perspectiveName,
      TModel? snapshotModel,
      JsonDocument? snapshotData,
      ReplayFrom from,
      CancellationToken cancellationToken) {
    var attempt = 0;
    while (true) {
      attempt++;
      // A replay that is refused restarts from the same snapshot, read again from its stored form so nothing the
      // refused replay folded carries into the next one.
      var initialModel = attempt == 1 || snapshotData is null ? snapshotModel : _fromSnapshotJson(snapshotData);
      try {
        return await _runFromModelAsync(streamId, perspectiveName, initialModel, from, cancellationToken);
      } catch (global::Whizbang.Core.Perspectives.PerspectiveRowConflictException conflict)
          when (_retriesReplayConflict(conflict, perspectiveName, streamId, attempt)) {
        // Replay again onto the moved row.
      }
    }
  }

  /// <summary>
  /// Whether a rewind's replay whose write was refused because the row moved runs again (#1863): true, logged at Debug,
  /// until the last attempt; then false, logged at Warning, and the conflict fails the rewind.
  /// </summary>
  private bool _retriesReplayConflict(
      global::Whizbang.Core.Perspectives.PerspectiveRowConflictException conflict, string perspectiveName, Guid streamId, int attempt) {
    if (attempt < MAX_ROW_CONFLICT_ATTEMPTS) {
      _logger.LogDebug(
          conflict,
          "Row for {PerspectiveName} stream {StreamId} changed while a rewind replayed it (replay {Attempt} of {MaxAttempts}); replaying again onto the moved row",
          perspectiveName,
          streamId,
          attempt,
          MAX_ROW_CONFLICT_ATTEMPTS
      );
      return true;
    }
    _logger.LogWarning(
        conflict,
        "Row for {PerspectiveName} stream {StreamId} changed under each of {MaxAttempts} rewind replays; the rewind fails and is retried. Nothing stale was written",
        perspectiveName,
        streamId,
        MAX_ROW_CONFLICT_ATTEMPTS
    );
    return false;
  }

  /// <summary>
  /// Replays events in-memory starting from a provided model (or empty model if null).
  /// Does NOT call GetByStreamIdAsync - uses the provided model directly.
  /// No intermediate DB writes - single atomic UpsertAsync at the end.
  /// No lifecycle hooks - receptors are suppressed via ProcessingMode on the PerspectiveWorker context.
  /// </summary>
  // FUTURE: optimize _runFromModelAsync to use RunWithEventsAsync with pre-supplied events
  private async Task<PerspectiveCursorCompletion> _runFromModelAsync(
      Guid streamId,
      string perspectiveName,
      TModel? initialModel,
      ReplayFrom from,
      CancellationToken cancellationToken) {
    var replayFromEventId = from.EventId;
    var readThroughArchiveAfterVersion = from.ReadThroughArchiveAfterVersion;

    // Use provided model or create empty one (full replay from event zero)
    // The row version this replay writes against, read before any event is (#1863): a writer that commits after this
    // read moves the version and the write below is refused, and one that committed before it stored its event first,
    // so the replay reads that event. A store that tracks no versions returns Unchecked and writes as it always did.
    var applyRead = await _perspectiveStore.ReadForApplyAsync(streamId, cancellationToken);
    var rowVersion = applyRead.Version;

    var currentModel = initialModel ?? CreateEmptyModel(streamId);
    // True once the model is one an Apply produced: a snapshot's model was, the scaffolded empty one is only Apply
    // input. A replay whose every event the perspective declines writes nothing, exactly as the live drain does,
    // instead of storing the scaffold as a blank row (#1677).
    var producedByApply = initialModel is not null;

    // Purge stays purged (issue #1027): a full replay begins purged when the stream is marked, exactly as the
    // live drain does on a missing row - the history before an operator purge is gone, so the fold alone could
    // not reproduce it. A replay from a snapshot starts from a live row and folds any purge it meets.
    var startedPurged = initialModel is null && await _isPurgedAsync(streamId, perspectiveName, cancellationToken);

    // Get perspective instance from DI
    var perspective = _serviceProvider.GetRequiredService<TPerspective>();

    // Track progress
    var eventsProcessed = 0;
    Guid? lastSuccessfulEventId = replayFromEventId;
    string? lastSuccessfulEventType = null;
    DateTime? lastSuccessfulEventAt = null;
    TModel? updatedModel = currentModel;
    var pendingPurge = startedPurged;
    Guid? purgeEventId = null;
    var resurrected = false;
    var purgedEventsSkipped = 0;
    PerspectiveScope? lastScope = null;
    // The replay FRONTIER - max event id / max local commit_sequence across everything
    // applied. The checkpoint metadata must carry the frontier, not the origin-order-last
    // event's stamp: after a replay everything at or below the frontier IS applied (that is
    // the idempotency floor's meaning), and the upsert's cross-pod lost-update guard rightly
    // refuses a write whose sequence sits below the stored floor - an origin-order replay's
    // final event can carry a LOWER local stamp than a backfilled straggler it slotted.
    Guid? frontierEventId = null;
    long? frontierCommitSequence = null;
    // #2366: the replayed event last in stream order (by version), the position the cursor reports.
    var lastInStream = new LastInStream();

    // Slice 24c: intermediate-snapshot tracking. When the rewind interval is configured
    // (> 0) we snapshot every N events DURING the replay, not just at the end. Subsequent
    // rewinds - including "very late" events whose MessageId falls between intermediate
    // points and the end - find a qualifying snapshot rather than replaying from event zero.
    var snapshotsOn = _snapshotStore is not null && _snapshotOptions?.Value.Enabled == true;
    var rewindSnapshotInterval = snapshotsOn ? _snapshotOptions!.Value.RewindSnapshotIntervalEvents : 0;
    var eventsSinceIntermediateSnapshot = 0;
    // #2379: an intermediate snapshot is taken only where the fold so far is every event up to some version, at that
    // version's event: a fold in origin order can hold a later version before an earlier one.
    var foldedPrefix = new global::Whizbang.Core.Perspectives.FoldedVersionPrefix();
    global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>? firstStreamEvent = null;

    // Build list of event types this perspective handles
    var eventTypes = _handledEventTypes;

    // An overwrite-only perspective (#1869): every Apply builds the row from its event alone, so the last event decides
    // the row and the rewind applies only it. It answers null when the last event cannot decide alone (a purge-capable
    // event in the stream, an Apply that returns no model or throws), and the full fold below runs instead. A stream
    // that starts purged is folded in full, because only the fold knows when an Apply resurrects it.
    // <docs>fundamentals/perspectives/rewind#overwrite-only</docs>
    // A model with collectives is folded in full (#1956): a collective committed after the last event changes the row the
    // last event alone would write.
    var foldsCollectives = _modelHasCollectives();
    if (_traits.IsOverwriteOnly && !startedPurged && !foldsCollectives
        && await _replayLastEventOnlyAsync(streamId, perspectiveName, perspective, rowVersion, replayFromEventId, eventTypes, cancellationToken)
          is { } lastEventOnly) {
      return lastEventOnly;
    }

    // v0.688 - Rewind catch-up loop (a production forensic fix). The original
    // implementation read events ONCE here and applied that fixed list. Events
    // appended to the stream during the in-memory apply window were silently
    // dropped (a production bulk-import saga landed with a handful of per-item
    // entries missing even though all events were durable). The wrapping while
    // loop re-checks the event store after each
    // apply pass - if new events arrived they get picked up in the next
    // iteration instead of being lost between PerspectiveRewindStarted and
    // PerspectiveRewindCompleted. The loop exits when the read returns zero
    // new events (event store quiescent w.r.t. lastSuccessfulEventId).
    // Bounded by MAX_REWIND_CATCH_UP_ITERATIONS as a safety against pathological
    // append rates; in practice 1-2 iterations cover the bulk-import case.
    // <docs>fundamentals/perspectives/rewind#catch-up-loop</docs>
    // <tests>tests/Whizbang.Core.Component.Tests/Perspectives/PerspectiveRewindCompletionGapTests.cs</tests>
    // <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/ReconcileRewindScenarioTests.cs</tests>
    // <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReconcileRewindDeclarationSqlTests.cs</tests>
    var rewindCatchUpIterations = 0;
    var anchorEventId = replayFromEventId;

    // #1956: a rewind is a replay, so it folds the row's collectives as a rebuild does. It folds those committed after
    // where it starts: after the snapshot's event, whose model already holds the ones committed before it, or every one
    // for a replay from the stream's start. Each folds once across the catch-up passes, and none moves the checkpoint.
    long? collectiveStartSequence = foldsCollectives && replayFromEventId is Guid snapshotEventId
        ? await _eventStore.GetCommitSequenceAsync(snapshotEventId, cancellationToken)
        : null;
    var foldedCollectives = new HashSet<Guid>();
    // #1963: how far the row holds its collectives at its last stream event, for the snapshots this replay takes. It holds
    // what the start held (the snapshot's mark, or the snapshot-event rule for a snapshot that records none) and every
    // collective folded before that event, whose own commit sequence it reaches too. A collective folded with no commit
    // sequence cannot be described by one, so the replay's snapshots then record no mark.
    var heldStart = from.CollectivesThroughSequence ?? collectiveStartSequence;
    var heldThrough = new CollectiveHold(foldsCollectives, heldStart, startDescribed: replayFromEventId is null || heldStart is not null);
    // The row at its last event, taken when collectives committed after that event fold into it: the snapshot this replay
    // ends with is taken at that event, so it must not hold them, or a rewind from it would fold them again.
    JsonDocument? modelAtLastEvent = null;

    while (true) {
      rewindCatchUpIterations++;
      if (rewindCatchUpIterations > MAX_REWIND_CATCH_UP_ITERATIONS) {
        _logger.LogWarning(
            "Rewind for {PerspectiveName} stream {StreamId} hit MAX_REWIND_CATCH_UP_ITERATIONS ({Max}); breaking - sustained append rate exceeds apply rate, projection may still trail event-store HEAD until next claim cycle",
            perspectiveName, streamId, MAX_REWIND_CATCH_UP_ITERATIONS);
        break;
      }

      // Read events from the current anchor. First pass uses replayFromEventId
      // (snapshot or null=from-beginning). Subsequent passes use the last event
      // we applied, so we only pick up the delta of events appended since.
      var events = new global::System.Collections.Generic.List<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>>();
      // #1738: the first pass from a snapshot an archive covers reads the archived events after it, then the hot ones; every
      // later pass resumes after the last event applied, which is hot.
      var read = rewindCatchUpIterations == 1 && readThroughArchiveAfterVersion is long afterVersion
          ? _eventStore.ReadPolymorphicThroughArchiveAsync(streamId, anchorEventId, afterVersion, eventTypes, cancellationToken)
          : _eventStore.ReadPolymorphicAsync(streamId, anchorEventId, eventTypes, cancellationToken);
      await foreach (var envelope in read) {
        events.Add(envelope);
      }
      // #2152: the first of the stream's own events the replay reads, across every pass.
      firstStreamEvent ??= events.Count > 0 ? events[0] : null;

      // Ordering invariant: origin order for a single-origin stream, else commit order when every event is
      // stamped, else id order. The inversion detector is commit-sequence-authoritative, and an id-ordered replay
      // would silently undo newer writes. The rule lives in ReplayEventOrder, which the rebuild shares.
      if (events.Count > 1) {
        events = [.. global::Whizbang.Core.Perspectives.ReplayEventOrder.Order(events)];
      }

      // The row's collectives committed after the replay's start, merged among the stream's events in commit order (#1956).
      // A first pass from a snapshot with no stream event after it still folds those committed since the snapshot, for the
      // tenant the row is stored under.
      if (foldsCollectives
          && _collectiveTenant(events, applyRead, firstPassFromASnapshot: rewindCatchUpIterations == 1 && initialModel is not null)
            is { Known: true } tenant) {
        var merged = await _collectiveReplayApplier!.InterleaveAfterAsync(
            typeof(TModel), events,
            new global::Whizbang.Core.Perspectives.CollectiveReplayStart(tenant.TenantId, replayFromEventId, collectiveStartSequence) {
              CollectivesThroughSequence = from.CollectivesThroughSequence,
            },
            cancellationToken);
        events = [.. merged.Where(e => e.Payload is not global::Whizbang.Core.Messaging.ICollectiveEvent || foldedCollectives.Add(e.MessageId.Value))];
      }

      foldedPrefix.Read(events.Where(e => e.Payload is not global::Whizbang.Core.Messaging.ICollectiveEvent));
      if (events.Count == 0) {
        // Event store quiescent: the rewind has caught up to HEAD AT THIS COMMIT.
        // This is the contract PerspectiveRewindCompleted now honors.
        break;
      }

      // A batch whose last event always purges the row (#1151) discards whatever the events before it fold: they
      // move the frontier and count as processed, and only the purge is applied.
      var foldFrom = 0;
      if (events.Count > 1 && IsUnconditionalPurge(events[^1].Payload)) {
        foldFrom = events.Count - 1;
        foreach (var skipped in events.Take(foldFrom)) {
          if (skipped.Payload is not global::Whizbang.Core.Messaging.ICollectiveEvent) {
            (frontierEventId, frontierCommitSequence) = _advanceFrontier(frontierEventId, frontierCommitSequence, skipped);
            lastInStream.Offer(skipped);
            _ = foldedPrefix.Folded(skipped);
          }
          eventsProcessed++;
        }
      }

      // Apply all events in memory - no intermediate DB writes, no lifecycle hooks
      foreach (var envelope in events.Skip(foldFrom)) {
        var @event = envelope.Payload;

        // A collective folds into the row and moves no checkpoint and no frontier (#1956), as in a rebuild (#1931). A purged
        // row has nothing to fold into. Its scope is the collective's, not the row's, so it is not tracked for the write.
        if (@event is global::Whizbang.Core.Messaging.ICollectiveEvent) {
          if (!pendingPurge && updatedModel is not null) {
            if (snapshotsOn && modelAtLastEvent is null) {
              modelAtLastEvent = _toSnapshotJson(updatedModel);
            }
            updatedModel = (TModel?)_collectiveReplayApplier!.ApplyInMemory(
                typeof(TModel), updatedModel, streamId, @event);
            producedByApply = true;
            heldThrough.Fold(envelope.LocalCommitSequence);
            // #1970: a collective still in flight may yet reach the row live, so no snapshot of this replay says what it holds.
            if (_collectiveReplayApplier.IsInFlight(@event)) {
              heldThrough.MeetInFlight();
            }
          }
          eventsProcessed++;
          continue;
        }
        // A stream event after collectives: the row at the last event is the one this event leaves.
        modelAtLastEvent?.Dispose();
        modelAtLastEvent = null;
        heldThrough.ReachEvent(envelope.LocalCommitSequence);

        // Track scope from envelope for perspective upsert
        var envelopeScope = envelope.GetCurrentScope();
        if (envelopeScope?.Scope != null) {
          lastScope = envelopeScope.Scope;
        }

        // A purged stream stays purged: skip the event unless its Apply returns Resurrect. The frontier still
        // moves past a skipped event, so the cursor never re-flags it as a straggler.
        if (pendingPurge) {
          var resurrection = _tryResurrect(perspective, streamId, @event, envelope.IsFirstInLocalStream);
          lastSuccessfulEventId = envelope.MessageId.Value;
          lastSuccessfulEventAt = _eventTime(envelope);
          eventsProcessed++;
          (frontierEventId, frontierCommitSequence) = _advanceFrontier(frontierEventId, frontierCommitSequence, envelope);
          lastInStream.Offer(envelope);
          _ = foldedPrefix.Folded(envelope);
          if (resurrection is null) {
            purgedEventsSkipped++;
            continue;
          }
          pendingPurge = false;
          resurrected = true;
          updatedModel = resurrection;
          producedByApply = true;
          lastSuccessfulEventType = PerspectiveRunnerTypeNames.Recorded(@event.GetType());
          continue;
        }

        // Apply event to model using perspective's pure Apply method
        TModel? appliedModel;
        global::Whizbang.Core.Perspectives.ModelAction action;
        try {
          (appliedModel, action) = _applyEvent(perspective, updatedModel, @event, envelope.IsFirstInLocalStream);
        } catch (Exception applyEx) {
          _logger.LogError(
              applyEx,
              "Error applying {EventType} to perspective stream {StreamId}, skipping event {EventId}",
              global::Whizbang.Core.TypeNameFormatter.SimpleName(@event.GetType()),
              streamId,
              envelope.MessageId.Value
          );
          lastSuccessfulEventId = envelope.MessageId.Value;
          lastSuccessfulEventAt = _eventTime(envelope);
          eventsProcessed++;
          _ = foldedPrefix.Folded(envelope);
          continue;
        }

        switch (action) {
          case global::Whizbang.Core.Perspectives.ModelAction.Delete:
            updatedModel = appliedModel ?? updatedModel;
            producedByApply |= appliedModel != null;
            break;
          case global::Whizbang.Core.Perspectives.ModelAction.Purge:
            pendingPurge = true;
            purgeEventId = envelope.MessageId.Value;
            updatedModel = null;
            break;
          default:
            if (appliedModel != null) {
              updatedModel = appliedModel;
              producedByApply = true;
            }
            break;
        }

        lastSuccessfulEventId = envelope.MessageId.Value;

        lastSuccessfulEventAt = _eventTime(envelope);
        lastSuccessfulEventType = PerspectiveRunnerTypeNames.Recorded(@event.GetType());
        eventsProcessed++;
        (frontierEventId, frontierCommitSequence) = _advanceFrontier(frontierEventId, frontierCommitSequence, envelope);
        lastInStream.Offer(envelope);
        var prefixAt = foldedPrefix.Folded(envelope);

        // Slice 24c: take an intermediate snapshot every N replayed events. Puts a snapshot
        // at THIS point in the stream's history so future rewinds for late events with
        // MessageId near this point find a qualifying restore point. PruneOldSnapshotsAsync
        // caps total snapshots per stream so this can't bloat the table without bound.
        if (rewindSnapshotInterval > 0
            && !pendingPurge && updatedModel is not null && producedByApply && heldThrough.Settled) {
          eventsSinceIntermediateSnapshot++;
          // A due snapshot waits for the next point where the fold is a version prefix (#2379).
          if (eventsSinceIntermediateSnapshot >= rewindSnapshotInterval && prefixAt is not null
              && !await _cadenceSnapshotSlotTakenAsync(streamId, perspectiveName, cancellationToken)) {
            try {
              // Slice 26.11: resolve commit_sequence for the intermediate snapshot too,
              // so a rewind triggered by a "very late" event with commit_sequence between
              // intermediate snapshots finds the right restore point.
              var intermediateCommitSequence = await _eventStore.GetCommitSequenceAsync(
                  prefixAt.MessageId.Value, cancellationToken);
              await _snapshotStore!.CreateSnapshotAsync(
                  streamId, perspectiveName, prefixAt.MessageId.Value,
                  intermediateCommitSequence,
                  _toSnapshotJson(updatedModel), heldThrough.Mark, cancellationToken);
              await _snapshotStore.PruneOldSnapshotsAsync(
                  streamId, perspectiveName, _snapshotOptions!.Value.MaxSnapshotsPerStream, cancellationToken);
              eventsSinceIntermediateSnapshot = 0;
            } catch (Exception snapshotEx) when (snapshotEx is not OperationCanceledException) {
              // Intermediate snapshots are best-effort - failures here don't break the rewind.
              // The end-of-rewind snapshot at line ~833 is the durable fallback. Just log.
              _logger.LogDebug(snapshotEx,
                  "Intermediate snapshot at event {EventId} for {PerspectiveName} stream {StreamId} failed; continuing replay",
                  lastSuccessfulEventId.Value, perspectiveName, streamId);
            }
          }
        }
      } // closes foreach (var envelope in foldEvents)

      // v0.688 - advance the catch-up anchor past everything this pass READ so the next
      // iteration's ReadPolymorphicAsync only returns events appended since. The store reads after the anchor's
      // position in the stream, its version (#2379), so the anchor is the event read last in stream order, never the
      // largest id, which sits anywhere in a stream whose ids and versions disagree.
      // Collectives are other streams' events, so they never anchor this stream's read; a pass that folded only
      // collectives leaves the anchor where it was.
      anchorEventId = global::Whizbang.Core.Perspectives.StreamReadAnchor.After(
        events.Where(e => e.Payload is not global::Whizbang.Core.Messaging.ICollectiveEvent), anchorEventId);
    } // closes while (true)

    return await _finishReplayAsync(new ReplayOutcome {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      RowVersion = rowVersion,
      ReplayFromEventId = replayFromEventId,
      EventsProcessed = eventsProcessed,
      PendingPurge = pendingPurge,
      PurgeEventId = purgeEventId,
      UpdatedModel = updatedModel,
      ProducedByApply = producedByApply,
      LastSuccessfulEventId = lastSuccessfulEventId,
      LastSuccessfulEventType = lastSuccessfulEventType,
      LastSuccessfulEventAt = lastSuccessfulEventAt,
      LastScope = lastScope,
      Resurrected = resurrected,
      StartedPurged = startedPurged,
      PurgedEventsSkipped = purgedEventsSkipped,
      FrontierEventId = frontierEventId,
      FrontierCommitSequence = frontierCommitSequence,
      LastInStream = lastInStream,
      ModelAtLastEvent = modelAtLastEvent,
      CollectivesHeldThrough = heldThrough.Mark,
      CollectivesSettled = heldThrough.Settled,
      FirstStreamEvent = firstStreamEvent,
    }, cancellationToken);
  }

  /// <summary>
  /// The single write that ends a replay (#1869): the row, or its purge, at the replay's frontier, then the snapshot
  /// that follows every rewind that applied something. Both replays end here, the full fold and an overwrite-only
  /// perspective's last-event apply, so the two write a row the same way.
  /// </summary>
  private async Task<PerspectiveCursorCompletion> _finishReplayAsync(ReplayOutcome outcome, CancellationToken cancellationToken) {
    var streamId = outcome.StreamId;
    var perspectiveName = outcome.PerspectiveName;
    var rowVersion = outcome.RowVersion;
    var replayFromEventId = outcome.ReplayFromEventId;
    var eventsProcessed = outcome.EventsProcessed;
    var pendingPurge = outcome.PendingPurge;
    var purgeEventId = outcome.PurgeEventId;
    var updatedModel = outcome.UpdatedModel;
    var producedByApply = outcome.ProducedByApply;
    var lastSuccessfulEventId = outcome.LastSuccessfulEventId;
    var lastSuccessfulEventType = outcome.LastSuccessfulEventType;
    var lastSuccessfulEventAt = outcome.LastSuccessfulEventAt;
    var lastScope = outcome.LastScope;
    var resurrected = outcome.Resurrected;
    var startedPurged = outcome.StartedPurged;
    var purgedEventsSkipped = outcome.PurgedEventsSkipped;
    var frontierEventId = outcome.FrontierEventId;
    var frontierCommitSequence = outcome.FrontierCommitSequence;
    var rowChanged = false;  // True once this replay actually wrote or purged the row
    // Why a rebuild that folded events left the row untouched (#2252)
    var unchangedReason = global::Whizbang.Core.Perspectives.PerspectiveRowUnchangedReason.Unreported;
    var rebuilding = global::Whizbang.Core.Messaging.ProcessingModeAccessor.Current
        is global::Whizbang.Core.Messaging.ProcessingMode.Rebuild;

    // Single atomic write at the end - lenses see pre-replay data until this point.
    // Checkpoint at the replay FRONTIER (max id / max local sequence applied): the floor's
    // meaning is "everything at or below is applied", and a frontier below the stored floor
    // would be refused by the upsert's cross-pod lost-update guard - discarding the very
    // correction the rewind computed.
    if (eventsProcessed > 0) {
      // Issue #983: taken before the write, which strips a Split class model's promoted fields in place.
      JsonDocument? snapshotBeforeWrite = null;
      if (pendingPurge) {
        // The replay ends purged: record it first, then remove the row (a rewind can run over a live one).
        if (purgeEventId.HasValue) {
          await _markPurgedAsync(streamId, perspectiveName, purgeEventId, cancellationToken);
        }
        await _perspectiveStore.PurgeAsync(streamId, cancellationToken);
        rowChanged = true;
      } else if (updatedModel != null && producedByApply) {
        var checkpointEventId = frontierEventId ?? lastSuccessfulEventId!.Value;
        var replayCheckpointCommitSequence = frontierCommitSequence
            ?? await _eventStore.GetCommitSequenceAsync(checkpointEventId, cancellationToken);
        // Issue #1002: the snapshot below follows every rewind that applied something, when snapshots are on.
        var snapshotDue = _snapshotStore is not null && _snapshotOptions?.Value.Enabled == true && lastSuccessfulEventId.HasValue
            && outcome.CollectivesSettled;
        // #1956: the row at its last event when collectives folded after it, so the snapshot holds none of them.
        snapshotBeforeWrite = snapshotDue ? _takeModelAtLastEvent(outcome) ?? _snapshotBeforeWrite(updatedModel) : null;
        // #2252: a Split class model's fold, taken before the write strips its promoted fields; null for any other model.
        using var foldBeforeWrite = rebuilding ? _snapshotBeforeWrite(updatedModel) : null;
        rowChanged = await SaveModelAndCheckpointAsync(
            streamId, perspectiveName, updatedModel, producedByApply, checkpointEventId, _checkpointType(lastSuccessfulEventType),
            replayCheckpointCommitSequence, _checkpointTime(lastSuccessfulEventAt),
            rowVersion.State == global::Whizbang.Core.Perspectives.PerspectiveRowVersionState.Present,
            outcome.FirstStreamEvent is { } firstStreamEvent ? [firstStreamEvent] : [],
            cancellationToken,
            // A rewind reads no stored scope, so it writes scope only if it recreates the row; an existing row keeps
            // its scope (Always fields catch up on the next live apply).
            global::Whizbang.Core.Lenses.ScopeInheritance.Resolve(
                lastScope, rowScope: null, rowExists: false, _traits.InheritScopeOnCreate, _traits.InheritScopeAlways, scopeEventScope: null).Scope,
            forceUpdateScope: false,
            expectedVersion: rowVersion);
        if (rowChanged && resurrected && startedPurged) {
          await _clearPurgeAsync(streamId, perspectiveName, cancellationToken);
        }
        // The row did not move while the replay ran (a moved row is refused above, and replayed again), yet its
        // ordering guard refused the write: the row is stamped past everything this replay read. The replay's
        // correction is not in the row, and a rewind that wrote nothing is not one that applied (#1863).
        if (!rowChanged && rebuilding) {
          unchangedReason = _readRefusedRebuildWrite(
              foldBeforeWrite, updatedModel, await _perspectiveStore.GetByStreamIdAsync(streamId, cancellationToken));
        }
        if (!rowChanged) {
          _logger.LogWarning(
              "[diag.rewind.write.refused] {PerspectiveName} stream {StreamId}: the row is stamped past the rewind's frontier at event {EventId}, so the replay was not written and the row keeps what it holds",
              perspectiveName,
              streamId,
              checkpointEventId);
        }
      } else {
        // No event produced a model, so there was nothing to write (#2252).
        unchangedReason = global::Whizbang.Core.Perspectives.PerspectiveRowUnchangedReason.NoModel;
      }
      _reportPurgedEventsSkipped(perspectiveName, streamId, purgedEventsSkipped);

      await _perspectiveStore.FlushAsync(cancellationToken);

      _logger.LogDebug(
          "Replay completed: {EventCount} events for {PerspectiveName} stream {StreamId}, checkpoint: {CheckpointEventId}",
          eventsProcessed, perspectiveName, streamId, lastSuccessfulEventId);

      // Create snapshot after replay if configured.
      // Slice 26.11 / production forensic G3: resolve commit_sequence so the snapshot row carries it.
      // Without this, GetLatestSnapshotBeforeCommitSequenceAsync (which filters
      // snapshot_commit_sequence IS NOT NULL) silently misses this snapshot on subsequent
      // rewinds - defeating the deterministic-replay invariant the column was added for.
      if (_snapshotStore is not null && _snapshotOptions?.Value.Enabled == true
          && !pendingPurge && updatedModel is not null && producedByApply && lastSuccessfulEventId.HasValue
          && outcome.CollectivesSettled
          && !await _cadenceSnapshotSlotTakenAsync(streamId, perspectiveName, cancellationToken)) {
        // #2379: taken at the event with the highest version the replay read, so the snapshot's version names the events
        // it holds (the floor's and every one read above it). The last event folded can sit lower in a stream the replay
        // folds in origin order.
        var snapshotEventId = outcome.LastInStream.Position ?? lastSuccessfulEventId.Value;
        var afterReplayCommitSequence = await _eventStore.GetCommitSequenceAsync(
            snapshotEventId, cancellationToken);
        await _snapshotStore.CreateSnapshotAsync(
            streamId, perspectiveName, snapshotEventId,
            afterReplayCommitSequence,
            snapshotBeforeWrite ?? _toSnapshotJson(updatedModel), outcome.CollectivesHeldThrough, cancellationToken);
        snapshotBeforeWrite = null; // handed to the store
        await _snapshotStore.PruneOldSnapshotsAsync(
            streamId, perspectiveName, _snapshotOptions.Value.MaxSnapshotsPerStream, cancellationToken);
        _eventsSinceLastSnapshot.TryRemove(streamId, out _);
      }
      snapshotBeforeWrite?.Dispose(); // no snapshot taken
    }
    outcome.ModelAtLastEvent?.Dispose(); // not handed to the store

    var resultStatus = eventsProcessed > 0 ? PerspectiveProcessingStatus.Completed : PerspectiveProcessingStatus.None;

    return new PerspectiveCursorCompletion {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      PerspectiveType = typeof(TPerspective),
      // #2366: the cursor reports one position, in the stream's one order: the replayed event last by version. It used
      // to report the largest id applied together with the largest commit sequence applied, a position no event has
      // when ids and versions disagree (copies stored after a newer event), and the cursor was put back on that id.
      // The id frontier stays the fallback when the store gives no versions.
      // A replay's last successful event starts at the event it replays from, so it already falls back to that event.
      LastEventId = outcome.LastInStream.Position ?? frontierEventId ?? lastSuccessfulEventId ?? Guid.Empty,
      // Reported rather than left for the caller to rediscover. The caller used to look the id above
      // up among the rows its batch claimed, which a replay never applied, so the commit-sequence
      // cursor stayed behind while the event-id cursor moved and the inversion detector then
      // under-reported against the stale one (#1365).
      LastCommitSequence = frontierCommitSequence,
      // #2366: a replay from the stream's start read every event stamped at or below its frontier (a stamp is assigned
      // only after the event commits), so each is folded and the cursor stores the frontier. A replay from a snapshot
      // reads only the events after the snapshot's id, which says nothing about a stamp below the frontier.
      ReplayedThroughCommitSequence = eventsProcessed > 0 && replayFromEventId is null ? frontierCommitSequence : null,
      Status = resultStatus,
      RowChanged = rowChanged,
      UnchangedReason = unchangedReason,
      EventsProcessed = eventsProcessed
    };
  }

  /// <summary>
  /// The rewind of an overwrite-only perspective (#1869): reads the stream as the full replay reads it, keeps only the
  /// last event in the order the full replay folds it (<see cref="global::Whizbang.Core.Perspectives.LastInReplayOrder"/>),
  /// applies that one to an empty model, and writes the row through the same write the full replay ends with. The
  /// events before it are read and counted, never held or applied, and no receptor runs for them. Each catch-up pass over
  /// events appended meanwhile does the same. Returns null when the last event cannot decide the row alone, before
  /// anything is written, and the caller folds the stream in full.
  /// </summary>
  /// <docs>fundamentals/perspectives/rewind#overwrite-only</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/OverwriteOnlyRewindTests.cs</tests>
  private async Task<PerspectiveCursorCompletion?> _replayLastEventOnlyAsync(
      Guid streamId,
      string perspectiveName,
      TPerspective perspective,
      global::Whizbang.Core.Perspectives.PerspectiveRowVersion rowVersion,
      Guid? replayFromEventId,
      Type[] eventTypes,
      CancellationToken cancellationToken) {
    var outcome = new ReplayOutcome {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      RowVersion = rowVersion,
      ReplayFromEventId = replayFromEventId,
      LastSuccessfulEventId = replayFromEventId,
    };
    var anchorEventId = replayFromEventId;
    var pass = 0;
    while (true) {
      pass++;
      if (pass > MAX_REWIND_CATCH_UP_ITERATIONS) {
        _logger.LogWarning(
            "Rewind for {PerspectiveName} stream {StreamId} hit MAX_REWIND_CATCH_UP_ITERATIONS ({Max}); breaking: sustained append rate exceeds apply rate, projection may still trail event-store HEAD until next claim cycle",
            perspectiveName, streamId, MAX_REWIND_CATCH_UP_ITERATIONS);
        break;
      }
      var read = new global::Whizbang.Core.Perspectives.LastInReplayOrder(static e => e.GetCurrentScope()?.Scope is not null);
      var readAnchor = new global::Whizbang.Core.Perspectives.StreamReadAnchor();
      await foreach (var envelope in _eventStore.ReadPolymorphicAsync(streamId, anchorEventId, eventTypes, cancellationToken)) {
        if (!DecidesTheRowAlone(envelope.Payload)) {
          return null;
        }
        read.Offer(envelope);
        outcome.FirstStreamEvent ??= envelope;
        (outcome.FrontierEventId, outcome.FrontierCommitSequence) = _advanceFrontier(outcome.FrontierEventId, outcome.FrontierCommitSequence, envelope);
        outcome.LastInStream.Offer(envelope);
        readAnchor.Offer(envelope);
      }
      if (read.Count == 0) {
        break;
      }
      var last = read.Last!;
      if (_applyAlone(perspective, streamId, last) is not { } applied) {
        return null;
      }
      outcome.UpdatedModel = applied;
      outcome.ProducedByApply = true;
      outcome.EventsProcessed += read.Count;
      outcome.LastSuccessfulEventId = last.MessageId.Value;
      outcome.LastSuccessfulEventType = PerspectiveRunnerTypeNames.Recorded(last.Payload.GetType());
      outcome.LastSuccessfulEventAt = _eventTime(last);
      // The last event read with a scope, which the read picked for having one.
      outcome.LastScope = read.LastMatching is { } scoped ? scoped.GetCurrentScope()!.Scope : outcome.LastScope;
      // #2379: the next pass reads after the event last in stream order, as the store places the anchor.
      anchorEventId = readAnchor.Next(anchorEventId);
      _logger.LogDebug(
          "Overwrite-only rewind of {PerspectiveName} stream {StreamId}: applied the last of {EventCount} event(s) read",
          perspectiveName, streamId, read.Count);
    }
    return await _finishReplayAsync(outcome, cancellationToken);
  }

  /// <summary>
  /// Applies an overwrite-only stream's last event to an empty model (#1869). Null when the Apply returns no model or
  /// throws: the row is then the events before it, which only the full fold applies (and logs, for a throw).
  /// </summary>
  private TModel? _applyAlone(TPerspective perspective, Guid streamId, MessageEnvelope<IEvent> envelope) {
    try {
      return _applyEvent(perspective, CreateEmptyModel(streamId), envelope.Payload, envelope.IsFirstInLocalStream).Item1;
    } catch (Exception applyEx) when (applyEx is not OperationCanceledException) {
      _logger.LogDebug(
          applyEx,
          "Apply of {EventType} threw on the last event of overwrite-only stream {StreamId}; the stream is replayed in full",
          global::Whizbang.Core.TypeNameFormatter.SimpleName(envelope.Payload.GetType()),
          streamId);
      return null;
    }
  }

  /// <summary>
  /// The replayed event last in stream order (#2366): the highest version read. A replay of a stream whose ids disagree
  /// with its versions (copies stored after a newer event) ends there, and the cursor reports it. Null when any event
  /// read carried no version, so the order is not known.
  /// </summary>
  private sealed class LastInStream {
    private Guid? _eventId;
    private long? _version;
    private bool _unversioned;

    public Guid? Position => _unversioned ? null : _eventId;

    public void Offer(MessageEnvelope<IEvent> envelope) {
      if (envelope.LocalStreamVersion is not long version) {
        _unversioned = true;
        return;
      }
      if (_version is null || version > _version.Value) {
        _version = version;
        _eventId = envelope.MessageId.Value;
      }
    }
  }

  /// <summary>What a replay folded, for the write that ends it (<see cref="_finishReplayAsync"/>).</summary>
  private sealed class ReplayOutcome {
    public Guid StreamId { get; set; }
    public string PerspectiveName { get; set; } = string.Empty;
    public global::Whizbang.Core.Perspectives.PerspectiveRowVersion RowVersion { get; set; }
    public Guid? ReplayFromEventId { get; set; }
    public int EventsProcessed { get; set; }
    public bool PendingPurge { get; set; }
    public Guid? PurgeEventId { get; set; }
    public TModel? UpdatedModel { get; set; }
    public bool ProducedByApply { get; set; }
    public Guid? LastSuccessfulEventId { get; set; }
    public string? LastSuccessfulEventType { get; set; }
    public DateTime? LastSuccessfulEventAt { get; set; }
    public PerspectiveScope? LastScope { get; set; }
    public bool Resurrected { get; set; }
    public bool StartedPurged { get; set; }
    public int PurgedEventsSkipped { get; set; }
    public Guid? FrontierEventId { get; set; }
    public long? FrontierCommitSequence { get; set; }
    // #2366: the replayed event last in stream order, the position the cursor reports.
    public LastInStream LastInStream { get; set; } = new();
    // The row at its last event, when collectives committed after that event folded into it (#1956).
    public JsonDocument? ModelAtLastEvent { get; set; }
    // How far the row held its collectives at its last event (#1963): the mark the replay's snapshot records.
    public long? CollectivesHeldThrough { get; set; }
    // #1970: false when the replay folded a collective still in flight; its snapshot is then not taken.
    public bool CollectivesSettled { get; set; } = true;
    // The first of the stream's own events the replay read (#2152, #2154): a row the replay inserts is dated at it, or at
    // the stream's recorded start when it does not begin the stream and the stream's first event is gone.
    public MessageEnvelope<IEvent>? FirstStreamEvent { get; set; }
  }

  /// <summary>Where a replay starts: the snapshot's event, its archive mark and its collectives mark (#1963).</summary>
  private readonly record struct ReplayFrom(Guid? EventId, long? ReadThroughArchiveAfterVersion, long? CollectivesThroughSequence);

  /// <summary>
  /// How far a replayed row holds its collectives at its last stream event (#1963). The collectives folded since that
  /// event count once the next stream event is reached, because a snapshot is taken at a stream event; the ones folded
  /// after the last stream event are not in the snapshot (#1956).
  /// </summary>
  private sealed class CollectiveHold(bool foldsCollectives, long? start, bool startDescribed) {
    private long? _held = start;
    private long? _pending;
    private bool _pendingUnstamped;
    // A snapshot the replay started from, whose collectives no commit sequence describes (it records no mark and its event
    // carries none), leaves the replay's snapshots with no mark either.
    private bool _unstamped = !startDescribed;

    /// <summary>A collective folded; its commit sequence, or null when it has none.</summary>
    public void Fold(long? commitSequence) {
      if (commitSequence is long sequence) {
        _pending = _pending is long pending ? Math.Max(pending, sequence) : sequence;
      } else {
        _pendingUnstamped = true;
      }
    }

    /// <summary>A stream event folded: the collectives before it are held, and so is its own position.</summary>
    public void ReachEvent(long? commitSequence) {
      _held = _max(_max(_held, _pending), commitSequence);
      _unstamped |= _pendingUnstamped;
      _pending = null;
      _pendingUnstamped = false;
    }

    /// <summary>
    /// The mark a snapshot at the last stream event records: none for a model with no collectives, and none once an
    /// unstamped collective was folded.
    /// </summary>
    public long? Mark => foldsCollectives && !_unstamped ? _held : null;

    /// <summary>A folded collective was still in flight (#1970): the live sink may yet apply it to the row.</summary>
    public void MeetInFlight() => Settled = false;

    /// <summary>Whether no folded collective was in flight, so the replay's snapshots may be taken.</summary>
    public bool Settled { get; private set; } = true;

    private static long? _max(long? a, long? b) => a is long x && b is long y ? Math.Max(x, y) : a ?? b;
  }

  /// <summary>Hands the replay's row at its last event to the snapshot write, once.</summary>
  private static JsonDocument? _takeModelAtLastEvent(ReplayOutcome outcome) {
    var model = outcome.ModelAtLastEvent;
    outcome.ModelAtLastEvent = null;
    return model;
  }

  /// <summary>
  /// Whether a replay of this model folds collectives (#1956): a collective replay applier is registered and names a
  /// collective event for the model.
  /// </summary>
  private bool _modelHasCollectives() => _collectiveEventTypeNames().Count > 0;

  /// <summary>The stored event-type names of the model's collectives; none without a collective replay applier.</summary>
  private global::System.Collections.Generic.IReadOnlyList<string> _collectiveEventTypeNames() =>
      _collectiveReplayApplier?.CollectiveEventTypeNamesFor(
          global::Whizbang.Core.TypeNameFormatter.FormatClrTypeName(typeof(TModel)).Replace('+', '.')) ?? [];

  /// <summary>The collective snapshot fence, when a driver registered one (#1963).</summary>
  private global::Whizbang.Core.Perspectives.ICollectiveSnapshotFence? _collectiveSnapshotFence =>
      _serviceProvider.GetService<global::Whizbang.Core.Perspectives.ICollectiveSnapshotFence>();

  /// <summary>
  /// Whether a live snapshot of this row reads the collective fence (#1963): the model has collectives and a fence is
  /// registered. A model no collective changes, or a host with no fence, takes snapshots that record no mark.
  /// </summary>
  private bool _collectiveFenceApplies() => _collectiveSnapshotFence is not null && _modelHasCollectives();

  /// <summary>One read of the collective fence, with what the read after the write needs to ask it again.</summary>
  private sealed record CollectiveFenceRead(
      string? TenantId,
      global::System.Collections.Generic.IReadOnlyList<string> CollectiveEventTypes,
      global::Whizbang.Core.Perspectives.CollectiveSnapshotFenceReading Reading);

  /// <summary>The mark a snapshot records, or that it is deferred (#1963).</summary>
  private readonly record struct CollectiveMark(bool Deferred, long? ThroughSequence);

  /// <summary>Reads the fence for the row's tenant, before the row is read.</summary>
  private async Task<CollectiveFenceRead> _readCollectiveFenceAsync(string? tenantId, CancellationToken cancellationToken) {
    var types = _collectiveEventTypeNames();
    var reading = await _collectiveSnapshotFence!.ReadAsync(tenantId, types, afterSequence: null, cancellationToken);
    return new CollectiveFenceRead(tenantId, types, reading);
  }

  /// <summary>
  /// The fence read before a live apply's row read, when the apply may end in a snapshot of a row with collectives
  /// (#1963); null otherwise. A rebuild reads none: it folds every collective in commit order, so its snapshot holds
  /// exactly the ones before its event. The row's tenant is its events', as a replay of the row takes it.
  /// </summary>
  private async Task<CollectiveFenceRead?> _readCollectiveFenceBeforeLiveReadAsync(
      Guid streamId,
      IReadOnlyList<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>> events,
      CancellationToken cancellationToken) {
    var snapshotMayBeDue = events.Count > 0
        && _snapshotStore is not null && _snapshotOptions?.Value.Enabled == true
        && _eventsSinceSnapshot(streamId) + events.Count >= _snapshotCadence(_snapshotOptions.Value).Threshold;
    if (!snapshotMayBeDue
        || global::Whizbang.Core.Messaging.ProcessingModeAccessor.Current is global::Whizbang.Core.Messaging.ProcessingMode.Rebuild
        || !_collectiveFenceApplies()) {
      return null;
    }
    return await _readCollectiveFenceAsync(events[0].GetCurrentScope()?.Scope.TenantId, cancellationToken);
  }

  /// <summary>
  /// Reads the fence again after the row was read and written (#1963), and decides the snapshot's mark. The snapshot
  /// holds exactly the row's collectives stamped at or below the first read's mark when no collective of the row was in
  /// flight at the first read, and none is in flight, or was stamped past that mark, at the second. Otherwise it is
  /// deferred and counted. A store with no stamped event marks zero: no collective was complete.
  /// </summary>
  private async Task<CollectiveMark> _collectiveMarkAfterWriteAsync(
      CollectiveFenceRead before, string perspectiveName, Guid streamId, CancellationToken cancellationToken) {
    var mark = before.Reading.CompleteThroughSequence ?? 0;
    if (!before.Reading.CollectiveInFlight) {
      var after = await _collectiveSnapshotFence!.ReadAsync(before.TenantId, before.CollectiveEventTypes, mark, cancellationToken);
      if (!after.CollectiveInFlight) {
        return new CollectiveMark(Deferred: false, ThroughSequence: mark);
      }
    }
    _deferSnapshot(perspectiveName, streamId);
    return new CollectiveMark(Deferred: true, ThroughSequence: null);
  }

  /// <summary>Counts and logs a snapshot deferred because a collective of the row was in flight (#1963).</summary>
  private void _deferSnapshot(string perspectiveName, Guid streamId) {
    _serviceProvider.GetService<PerspectiveMetrics>()?.SnapshotsDeferred.Add(
        1, new KeyValuePair<string, object?>("perspective_name", perspectiveName));
    _logger.LogDebug(
        "Snapshot of {PerspectiveName} stream {StreamId} deferred: a collective of the row was in flight, so the snapshot could not say which collectives it holds; it stays due",
        perspectiveName,
        streamId);
  }

  /// <summary>
  /// The tenant whose collectives a replay pass folds (#1956): the tenant of the stream's events when the pass read any;
  /// on a first pass from a snapshot that read none, the tenant the row is stored under, when the store read the row. Not
  /// known otherwise, and the pass then folds no collective.
  /// </summary>
  private static (bool Known, string? TenantId) _collectiveTenant(
      global::System.Collections.Generic.List<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>> events,
      global::Whizbang.Core.Perspectives.PerspectiveApplyRead applyRead,
      bool firstPassFromASnapshot) {
    if (events.Count > 0) {
      return (true, events[0].GetCurrentScope()?.Scope.TenantId);
    }
    return firstPassFromASnapshot && applyRead.Version.State == global::Whizbang.Core.Perspectives.PerspectiveRowVersionState.Present
        ? (true, applyRead.Scope?.TenantId)
        : (false, null);
  }

  /// <inheritdoc/>
  public async Task BootstrapSnapshotAsync(
      Guid streamId,
      string perspectiveName,
      Guid lastProcessedEventId,
      CancellationToken cancellationToken = default) =>
    _ = await TryBootstrapSnapshotAsync(streamId, perspectiveName, lastProcessedEventId, cancellationToken);

  // #2294: the bootstrap says what it did, so a caller that relies on the snapshot (the reap-driven pass) never counts a
  // snapshot that was not written.
  /// <inheritdoc/>
  public async Task<global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome> TryBootstrapSnapshotAsync(
      Guid streamId,
      string perspectiveName,
      Guid lastProcessedEventId,
      CancellationToken cancellationToken = default) {

    if (_snapshotStore is null || _snapshotOptions?.Value.Enabled != true) {
      return global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome.SnapshotsOff;
    }

    // #1963: a bootstrap snapshot is a live row too, so a row with collectives reads the fence before the model and again
    // after it, for the tenant the row is stored under. A store that cannot say the row's tenant cannot be fenced, so the
    // snapshot is deferred to a later bootstrap.
    CollectiveFenceRead? collectiveFence = null;
    if (_collectiveFenceApplies()) {
      var applyRead = await _perspectiveStore.ReadForApplyAsync(streamId, cancellationToken);
      if (applyRead.Version.State != global::Whizbang.Core.Perspectives.PerspectiveRowVersionState.Present) {
        _deferSnapshot(perspectiveName, streamId);
        return global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome.Deferred;
      }
      collectiveFence = await _readCollectiveFenceAsync(applyRead.Scope?.TenantId, cancellationToken);
    }

    var model = await _perspectiveStore.GetByStreamIdAsync(streamId, cancellationToken);
    if (model is null) {
      // The perspective purged the row, an eviction removed it, or no event ever produced one: nothing to snapshot.
      _logger.LogInformation(
          "Bootstrap snapshot of {PerspectiveName} stream {StreamId} at event {EventId} not taken: the stream has no row to snapshot",
          perspectiveName, streamId, lastProcessedEventId);
      return global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome.RowMissing;
    }

    var collectiveMark = collectiveFence is { } fenceBefore
        ? await _collectiveMarkAfterWriteAsync(fenceBefore, perspectiveName, streamId, cancellationToken)
        : new CollectiveMark(Deferred: false, ThroughSequence: null);
    if (collectiveMark.Deferred) {
      return global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome.Deferred;
    }

    var snapshotData = _toSnapshotJson(model);
    // Slice 26.11 / production forensic G4: bootstrap must stamp commit_sequence so the bootstrap snapshot
    // participates in commit-sequence-anchored rewind lookups. Without this, every late event
    // would force a full replay because GetLatestSnapshotBeforeCommitSequenceAsync skips the
    // NULL-commit_sequence rows the legacy overload produced.
    var bootstrapCommitSequence = await _eventStore.GetCommitSequenceAsync(
        lastProcessedEventId, cancellationToken);
    await _snapshotStore.CreateSnapshotAsync(
        streamId, perspectiveName, lastProcessedEventId, bootstrapCommitSequence, snapshotData, collectiveMark.ThroughSequence,
        cancellationToken);

    _logger.LogDebug(
        "Bootstrap snapshot created for {PerspectiveName} stream {StreamId} at event {EventId}",
        perspectiveName, streamId, lastProcessedEventId);
    return global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome.Written;
  }

  // #2296: the reap-driven bootstrap is taken THROUGH the reap boundary, the last body the cycle is about to reap, not at
  // the cursor. A snapshot at the cursor was a rewind floor only for a late event placed after it: a late event below the
  // cursor found no floor once the bodies below were reaped, and an ephemeral perspective dropped it. The model as of the
  // boundary is folded from the floor below it (the latest snapshot, or the stream's start) through the boundary, from
  // bodies the coverage gate has kept, since every body above the latest snapshot is still stored. Nothing is written to
  // the row. At the cursor the row already is that model, so the bootstrap at the cursor is taken as before. A model with
  // collectives folds the row's collectives too (#2456), as a rewind from the same floor would: those placed before the
  // last of the stream's events through the boundary, and none placed after all of them.
  // #2456: the snapshot written at the reap boundary takes the slot, and the snapshots below or above it go: only the
  // boundary moves an ephemeral perspective's floor, and the stream keeps the one snapshot its cadence allows.
  /// <inheritdoc/>
  public async Task<global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome> TryBootstrapSnapshotThroughAsync(
      Guid streamId,
      string perspectiveName,
      Guid lastProcessedEventId,
      Guid boundaryEventId,
      CancellationToken cancellationToken = default) {
    var outcome = await _bootstrapThroughAsync(streamId, perspectiveName, lastProcessedEventId, boundaryEventId, cancellationToken);
    if (outcome == global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome.Written) {
      // A written snapshot means a snapshot store and enabled snapshot options.
      await _snapshotStore!.PruneOldSnapshotsAsync(
          streamId, perspectiveName, _snapshotCadence(_snapshotOptions!.Value).Retention, cancellationToken);
    }
    return outcome;
  }

  private async Task<global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome> _bootstrapThroughAsync(
      Guid streamId,
      string perspectiveName,
      Guid lastProcessedEventId,
      Guid boundaryEventId,
      CancellationToken cancellationToken) {
    if (boundaryEventId == lastProcessedEventId) {
      return await TryBootstrapSnapshotAsync(streamId, perspectiveName, lastProcessedEventId, cancellationToken);
    }
    if (_snapshotStore is null || _snapshotOptions?.Value.Enabled != true) {
      return global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome.SnapshotsOff;
    }

    var floor = await _snapshotStore.GetSnapshotFloorAsync(streamId, perspectiveName, boundaryEventId, cancellationToken);
    TModel? model = null;
    if (floor is not null) {
      model = _fromSnapshotJson(floor.SnapshotData);
      if (model is null) {
        // The bodies below the floor may be reaped already, so a fold from the stream's start could not reproduce them.
        _logger.LogWarning(
            "Bootstrap snapshot of {PerspectiveName} stream {StreamId} through event {BoundaryEventId} not taken: the snapshot below it at {FloorEventId} cannot be read, and the history it covers may be reaped",
            perspectiveName, streamId, boundaryEventId, floor.SnapshotEventId);
        return global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome.Refused;
      }
    } else if (await _deletedArchiveExtentAsync(streamId, cancellationToken) is not null) {
      _logger.LogWarning(
          "Bootstrap snapshot of {PerspectiveName} stream {StreamId} through event {BoundaryEventId} not taken: the stream's archived history was deleted, so a fold from its start would be partial",
          perspectiveName, streamId, boundaryEventId);
      return global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome.Refused;
    }

    var read = new global::System.Collections.Generic.List<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>>();
    var events = floor is { ArchivedThroughVersion: not null, SnapshotVersion: int afterVersion }
        ? _eventStore.ReadPolymorphicThroughArchiveAsync(streamId, floor.SnapshotEventId, afterVersion, _handledEventTypes, cancellationToken)
        : _eventStore.ReadPolymorphicAsync(streamId, floor?.SnapshotEventId, _handledEventTypes, cancellationToken);
    await foreach (var envelope in events) {
      read.Add(envelope);
    }
    if (read.Find(e => e.MessageId.Value == boundaryEventId) is not { LocalStreamVersion: long boundaryVersion }) {
      _logger.LogWarning(
          "Bootstrap snapshot of {PerspectiveName} stream {StreamId} through event {BoundaryEventId} not taken: the boundary event is not read with its stream version after the snapshot below it",
          perspectiveName, streamId, boundaryEventId);
      return global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome.Refused;
    }

    // Every event up to the boundary, in the order a replay folds them (#2379).
    var through = global::Whizbang.Core.Perspectives.ReplayEventOrder.Order(
        [.. read.Where(e => e.LocalStreamVersion is long version && version <= boundaryVersion)]);
    // #2456: the row's collectives through the boundary, and the mark that says which ones the snapshot holds.
    var (folded, collectivesMark) = await _collectivesThroughBoundaryAsync(floor, model is not null, through, cancellationToken);
    // The live apply's own fold (#2246), so the boundary model is folded exactly as the row was: a purge stays purged
    // unless an Apply resurrects it, an Apply that throws is skipped, and a scaffold no Apply returned is no model.
    var fold = new ApplyState {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      Perspective = _serviceProvider.GetRequiredService<TPerspective>(),
      UpdatedModel = model ?? CreateEmptyModel(streamId),
      HasWrittenUpdate = model is not null,
      PendingPurge = model is null && await _isPurgedAsync(streamId, perspectiveName, cancellationToken),
    };
    _foldEvents(fold, folded, enableEventSpans: false, [], cancellationToken);
    var purged = fold.PendingPurge;
    var producedByApply = fold.HasWrittenUpdate && fold.UpdatedModel is not null;
    model = fold.UpdatedModel;
    if (purged || !producedByApply) {
      _logger.LogInformation(
          "Bootstrap snapshot of {PerspectiveName} stream {StreamId} through event {BoundaryEventId} not taken: the stream has no row as of that event",
          perspectiveName, streamId, boundaryEventId);
      return global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome.RowMissing;
    }
    if (fold.CollectiveInFlight) {
      // #1970: a collective still in flight may yet reach the row live, so the snapshot could not say what it holds.
      _deferSnapshot(perspectiveName, streamId);
      return global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome.Deferred;
    }

    var boundaryCommitSequence = await _eventStore.GetCommitSequenceAsync(boundaryEventId, cancellationToken);
    var snapshotData = _toSnapshotJson(model!);
    await _snapshotStore.CreateSnapshotAsync(
        streamId, perspectiveName, boundaryEventId, boundaryCommitSequence, snapshotData, collectivesMark, cancellationToken);
    _logger.LogDebug(
        "Bootstrap snapshot created for {PerspectiveName} stream {StreamId} through its reap boundary at event {EventId}",
        perspectiveName, streamId, boundaryEventId);
    return global::Whizbang.Core.Perspectives.SnapshotBootstrapOutcome.Written;
  }

  /// <summary>
  /// The events the reap-boundary bootstrap folds, with the row's collectives among them, and the collectives mark its
  /// snapshot records (#2456). A model with no collectives folds the stream's events alone and records no mark. One with
  /// collectives takes them from the applier as a rewind from the same floor does: those committed past the floor's mark
  /// (or every one, from the stream's start), each placed after every stream event it was committed after (#2449). The
  /// fold ends at the last stream event: a collective placed after all of them was committed after every event through
  /// the boundary, so the snapshot at the boundary does not hold it, and a replay from the snapshot folds it. A collective
  /// placed before the stream's first event with no floor below had no row to reach live, so it is not folded either.
  /// </summary>
  private async Task<(global::System.Collections.Generic.IReadOnlyList<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>> Folded, long? Mark)> _collectivesThroughBoundaryAsync(
      global::Whizbang.Core.Perspectives.PerspectiveSnapshotFloor? floor,
      bool fromARow,
      global::System.Collections.Generic.IReadOnlyList<global::Whizbang.Core.Observability.MessageEnvelope<global::Whizbang.Core.IEvent>> through,
      CancellationToken cancellationToken) {
    if (!_modelHasCollectives()) {
      return (through, null);
    }
    long? startSequence = floor is null ? null : await _eventStore.GetCommitSequenceAsync(floor.SnapshotEventId, cancellationToken);
    // The tenant of the stream's events, as a replay pass that read some takes it (_collectiveTenant).
    var merged = await _collectiveReplayApplier!.InterleaveAfterAsync(
        typeof(TModel), through,
        new global::Whizbang.Core.Perspectives.CollectiveReplayStart(through[0].GetCurrentScope()?.Scope.TenantId, floor?.SnapshotEventId, startSequence) {
          CollectivesThroughSequence = floor?.CollectivesThroughSequence,
        },
        cancellationToken);
    var list = merged.ToList();
    var first = fromARow ? 0 : list.FindIndex(static e => e.Payload is not global::Whizbang.Core.Messaging.ICollectiveEvent);
    var last = list.FindLastIndex(static e => e.Payload is not global::Whizbang.Core.Messaging.ICollectiveEvent);
    var folded = list.GetRange(first, last - first + 1);
    // How far the row holds its collectives at the boundary, by the rule a replay's own snapshot follows (#1963).
    var heldStart = floor?.CollectivesThroughSequence ?? startSequence;
    var held = new CollectiveHold(foldsCollectives: true, heldStart, startDescribed: floor is null || heldStart is not null);
    foreach (var envelope in folded) {
      if (envelope.Payload is global::Whizbang.Core.Messaging.ICollectiveEvent) {
        held.Fold(envelope.LocalCommitSequence);
      } else {
        held.ReachEvent(envelope.LocalCommitSequence);
      }
    }
    return (folded, held.Mark);
  }

  /// <summary>
  /// Whether a snapshot the live apply or a replay would take must give way to the one the stream holds (#2456). An
  /// ephemeral perspective keeps one snapshot, its rewind floor, and the floor moves only to a point the grace window has
  /// closed on: the reap boundary, through the reap-driven bootstrap. A snapshot at the tip sits above bodies still inside
  /// the window, so it fills an empty slot but never replaces the snapshot there: replacing a boundary snapshot left a late
  /// event below the tip with no floor once the bodies under the boundary were reaped. A Sourced perspective's cadence is
  /// unchanged.
  /// </summary>
  private async Task<bool> _cadenceSnapshotSlotTakenAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken) =>
      _traits.IsEphemeral && await _snapshotStore!.HasAnySnapshotAsync(streamId, perspectiveName, cancellationToken);

  // The snapshot cadence: every how many applied events a run snapshots, and how many snapshots a stream keeps.
  // Ephemeral perspectives snapshot on their own aggressive, single-slot cadence so a fresh rewind floor exists
  // within the grace window before consumed bodies are reaped; Sourced perspectives use the standard
  // cadence/retention. The generator injects the mode-appropriate settings here.
  // Asked only once snapshots are known to be on (#2469), so it takes the options rather than checking for none.
  private (int Threshold, int Retention) _snapshotCadence(PerspectiveSnapshotOptions options) {
    var snapshotThreshold = _traits.IsEphemeral
        ? options.EphemeralSnapshotEveryNEvents
        : options.SnapshotEveryNEvents;
    var snapshotRetention = _traits.IsEphemeral
        ? options.EphemeralMaxSnapshotsPerStream
        : options.MaxSnapshotsPerStream;
    return (snapshotThreshold, snapshotRetention);
  }

  // The snapshot of a model the write strips in place (issue #983), serialized before the write when snapshots are on. A
  // Split class model's promoted fields are stripped from the instance itself, so a snapshot taken after the write would
  // lack them and a rewind from it would write their defaults back. Null for every other model, whose snapshot after the
  // write is the model it applied.
  private JsonDocument? _snapshotBeforeWrite(TModel model) => _traits.SnapshotsBeforeWrite ? _toSnapshotJson(model) : null;

  #region SNAPSHOT_SERIALIZATION
  // Snapshot (de)serialization routes through the source-generated JSON type registry
  // (AOT-safe, no reflection): the perspective model type is registered in the consumer's
  // MessageJsonContext, and the registry's combined options also carry the WhizbangId
  // converters - so id-bearing models round-trip faithfully instead of collapsing to {}.
  // Lazily resolved so the registry is fully populated (via [ModuleInitializer]) on first use.
  private static global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<TModel>? _snapshotModelTypeInfo;
  private static global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<TModel> _snapshotTypeInfo =>
      _snapshotModelTypeInfo ??= (global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<TModel>)
          global::Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(TModel));

  // Write: serialize the model with the current serialization logic, then stamp it with the
  // current serialization version so a future reader knows which implementation wrote it.
  private static JsonDocument _toSnapshotJson(TModel model) {
    using var modelDoc = JsonSerializer.SerializeToDocument(model, _snapshotTypeInfo);
    return global::Whizbang.Core.Perspectives.SnapshotEnvelope.Wrap(
        modelDoc.RootElement, global::Whizbang.Core.Serialization.SerializationVersion.CURRENT);
  }

  // Read: if the stored serialization version matches, deserialize the model; otherwise apply the
  // configured SnapshotUpgradePolicy. Returns null to signal "rebuild from events" (the caller
  // then falls back to a full replay, exactly as if no snapshot existed).
  private TModel? _fromSnapshotJson(JsonDocument doc) {
    var policy = _snapshotOptions?.Value.UpgradePolicy
        ?? global::Whizbang.Core.Perspectives.SnapshotUpgradePolicy.RebuildFromEvents;
    var result = global::Whizbang.Core.Perspectives.SnapshotEnvelope.Read(
        doc, global::Whizbang.Core.Serialization.SerializationVersion.CURRENT, policy);
    if (result.Action != global::Whizbang.Core.Perspectives.SnapshotReadAction.UseModel) {
      return null;
    }
    return JsonSerializer.Deserialize(result.Model.GetRawText(), _snapshotTypeInfo)!;
  }
  #endregion
}
