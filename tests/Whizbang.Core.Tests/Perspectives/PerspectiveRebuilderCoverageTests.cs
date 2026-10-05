using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Events.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Coverage round 23 — targets three <see cref="PerspectiveRebuilder"/> paths the existing
/// PerspectiveRebuilderTests suite does not exercise: the event-upcaster-driven stream-scope
/// widening, the stream-group presence reconcile's zero-reachable-announcers early exit, and the
/// mid-rebuild (not just end-of-rebuild) cursor-completion flush.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs</code-under-test>
public class PerspectiveRebuilderCoverageTests {

  // --- shared test doubles (minimal; mirrors PerspectiveRebuilderTests.cs's fakes) ---

  private sealed class FakePerspectiveRunner : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object);
    public int RunCount { get; private set; }

    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) {
      RunCount++;
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.NewGuid(),
        Status = PerspectiveProcessingStatus.Completed
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
        Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(
        Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  private sealed class FakePerspectiveRunnerRegistry(IPerspectiveRunner? runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class RecordingCheckpointCompleter : IPerspectiveCheckpointCompleter {
    public List<PerspectiveCursorCompletion> CompletionsReceived { get; } = [];
    public int CallCount { get; private set; }

    public Task CompleteAsync(IReadOnlyList<PerspectiveCursorCompletion> completions, CancellationToken cancellationToken = default) {
      CallCount++;
      CompletionsReceived.AddRange(completions);
      return Task.CompletedTask;
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Mid-rebuild cursor flush: with more pending completions than the flush batch, the interior
  // flush inside the per-stream loop must fire, not just the one call after the loop ends.
  // ---------------------------------------------------------------------------------------------

  /// <summary>
  /// If the interior flush (triggered once <c>pendingCompletions</c> reaches the 50-item batch)
  /// stops firing, a long rebuild accumulates every cursor completion in memory for the WHOLE run
  /// and persists nothing until it finishes — a crash partway through then loses the entire
  /// accumulated progress instead of resuming near the last flushed batch.
  /// </summary>
  [Test]
  public async Task RebuildStreamsAsync_MoreCompletionsThanTheFlushBatch_FlushesMidRebuildAndAgainAtTheEndAsync() {
    var runner = new FakePerspectiveRunner();
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var streams = Enumerable.Range(0, 60).Select(_ => Guid.NewGuid()).ToArray();
    var completer = new RecordingCheckpointCompleter();

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCheckpointCompleter>(completer);
    var sp = services.BuildServiceProvider();
    var rebuilder = new PerspectiveRebuilder(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PerspectiveRebuilder>.Instance);

    var result = await rebuilder.RebuildStreamsAsync("TestPerspective", streams);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(60);
    await Assert.That(completer.CallCount).IsEqualTo(2)
      .Because("60 completions with a 50-item flush batch must flush once mid-rebuild (at the 50th) and " +
               "once more for the remaining 10 — a single end-of-run call means the interior flush regressed");
    await Assert.That(completer.CompletionsReceived.Count).IsEqualTo(60);
  }

  // ---------------------------------------------------------------------------------------------
  // Stream-group presence reconcile: a follower with no OTHER model registered at all has zero
  // reachable announcers — the method must return before ever asking the coordinator for table
  // names or attempting a reconcile.
  // ---------------------------------------------------------------------------------------------

  private sealed class SoloFollowerModel;

  private sealed class RecordingWorkCoordinator : IWorkCoordinator {
    public int TableLookupCalls { get; private set; }
    public List<(string Follower, IReadOnlyCollection<string> Announcers)> Reconciled { get; } = [];

    public Task<IReadOnlyList<PerspectiveTableName>> GetPerspectiveTableNamesAsync(
        IReadOnlyCollection<string> clrTypeNames, CancellationToken cancellationToken = default) {
      TableLookupCalls++;
      return Task.FromResult<IReadOnlyList<PerspectiveTableName>>([]);
    }

    public Task<int> ReconcileFollowerPresenceAsync(
        string followerTable, IReadOnlyCollection<string> announcerTables, CancellationToken cancellationToken = default) {
      lock (Reconciled) { Reconciled.Add((followerTable, announcerTables)); }
      return Task.FromResult(0);
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// If <c>StreamGroupClosure.ReachableAnnouncers</c> ever came back non-empty here (or this early
  /// return were lost), the rebuild would ask the coordinator to reconcile against announcers that
  /// don't actually exist — a false-positive reconcile risks deleting a follower's rows for
  /// streams no announcer ever evicted.
  /// </summary>
  [Test]
  [NotInParallel("PerspectiveStreamGroupRegistry")]
  public async Task Rebuild_FollowerWithNoOtherRegisteredModel_HasNoReachableAnnouncersAsync() {
    PerspectiveStreamGroupRegistry.Clear();
    try {
      PerspectiveStreamGroupRegistry.Register(
        typeof(SoloFollowerModel), "coverage-group", announce: false, follow: true, bridge: false);

      var runner = new FakePerspectiveRunner();
      var registry = new FakePerspectiveRunnerRegistry(runner);
      var coordinator = new RecordingWorkCoordinator();
      var services = new ServiceCollection();
      services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
      services.AddSingleton<IWorkCoordinator>(coordinator);
      var sp = services.BuildServiceProvider();
      var rebuilder = new PerspectiveRebuilder(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PerspectiveRebuilder>.Instance);

      var result = await rebuilder.RebuildStreamsAsync(typeof(SoloFollowerModel).FullName!, [Guid.NewGuid()]);

      await Assert.That(result.Success).IsTrue();
      await Assert.That(coordinator.TableLookupCalls).IsEqualTo(0)
        .Because("with zero reachable announcers the method must return before ever asking the coordinator " +
                 "for table names — proceeding further would attempt to reconcile against announcers that don't exist");
      await Assert.That(coordinator.Reconciled).IsEmpty();
    } finally {
      PerspectiveStreamGroupRegistry.Clear();
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Event-upcaster type-change widening: a stream holding ONLY the legacy (pre-rename) event type
  // must still be picked up when an upcaster declares that type as its rebuild-time source.
  // ---------------------------------------------------------------------------------------------

  private sealed class LegacyFakeEvent : IEvent;
  private sealed class GenericFakeEvent : IEvent;

  private sealed class TypeChangeUpcaster : IEventUpcaster {
    public bool CanUpcast(IEvent storedEvent) => storedEvent is LegacyFakeEvent;
    public IReadOnlyList<Type> SourceTypes => [typeof(LegacyFakeEvent)];
    public IReadOnlyList<Type> TargetTypes => [typeof(GenericFakeEvent)];
    public IEvent Upcast(IEvent storedEvent) => new GenericFakeEvent();
  }

  private sealed class PerspectiveInfoRegistry(
      IPerspectiveRunner? runner, PerspectiveRegistrationInfo info) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [info];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class TypedFakeEventStoreQuery(Guid streamId, string eventType) : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query => new List<EventStoreRecord> {
      new() {
        Id = Guid.NewGuid(),
        StreamId = streamId,
        AggregateId = streamId,
        AggregateType = "Test",
        Version = 1,
        EventType = eventType,
        EventData = JsonDocument.Parse("{}").RootElement,
        Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] },
        CreatedAt = DateTime.UtcNow,
      },
    }.AsQueryable();

    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => Query.Where(e => e.StreamId == streamId);
    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => Query.Where(e => e.EventType == eventType);
  }

  /// <summary>
  /// If the widening logic here is lost, a rebuild of a perspective fed by a type-change upcaster
  /// (a legacy shape renamed to a generic one) silently skips every stream that only ever held the
  /// legacy shape — the perspective's rebuilt state is missing history a live drain would have
  /// applied via the same upcaster, and nothing reports the gap.
  /// </summary>
  [Test]
  public async Task RebuildInPlaceAsync_WithTypeChangeUpcaster_WidensStreamScopeToTheLegacySourceTypeAsync() {
    var legacyEventTypeName = Whizbang.Core.TypeNameFormatter.Format(typeof(LegacyFakeEvent));
    var targetEventTypeName = Whizbang.Core.TypeNameFormatter.Format(typeof(GenericFakeEvent));
    var streamId = Guid.NewGuid();

    var runner = new FakePerspectiveRunner();
    var info = new PerspectiveRegistrationInfo(
      "TestPerspective", "global::TestPerspective", "global::TestModel", [targetEventTypeName]);
    var registry = new PerspectiveInfoRegistry(runner, info);
    // The stream holds ONLY the legacy event type — absent widening, the scope query (which
    // filters on the perspective's own subscribed types) would never select this stream at all.
    var eventStoreQuery = new TypedFakeEventStoreQuery(streamId, legacyEventTypeName);
    var pipeline = new EventUpcasterPipeline([new TypeChangeUpcaster()]);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    services.AddSingleton(pipeline);
    var sp = services.BuildServiceProvider();
    var rebuilder = new PerspectiveRebuilder(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PerspectiveRebuilder>.Instance);

    var result = await rebuilder.RebuildInPlaceAsync("TestPerspective");

    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(1)
      .Because("the legacy-only stream must be included once the upcaster's declared source type widens the " +
               "scope — without widening, the filter excludes it and StreamsProcessed would stay 0");
  }

  // ---------------------------------------------------------------------------------------------
  // Rebuild outcome records (#1135): the events, the digest, and what happens when either fails.
  // ---------------------------------------------------------------------------------------------

  /// <summary>Hands back a different digest each call, so before and after are distinguishable.</summary>
  private sealed class SequencedRowDigest : IPerspectiveRowDigest {
    public int Calls { get; private set; }

    public Task<string?> ComputeAsync(
        string perspectiveName, IReadOnlyCollection<Guid> streamIds, CancellationToken ct = default) {
      Calls++;
      return Task.FromResult<string?>($"digest-{Calls}");
    }
  }

  private sealed class ThrowingRowDigest : IPerspectiveRowDigest {
    public Task<string?> ComputeAsync(
        string perspectiveName, IReadOnlyCollection<Guid> streamIds, CancellationToken ct = default) =>
      throw new InvalidOperationException("digest refused by the test double");
  }

  private static (PerspectiveRebuilder Rebuilder, RebuildEventDispatcherDouble Dispatcher) _buildWith(
      IPerspectiveRunnerRegistry registry, IPerspectiveRowDigest? digest, bool throwOnPublish = false) {
    var dispatcher = new RebuildEventDispatcherDouble(throwOnPublish);
    var services = new ServiceCollection();
    services.AddSingleton(registry);
    services.AddSingleton<IDispatcher>(dispatcher);
    if (digest is not null) { services.AddSingleton(digest); }
    var sp = services.BuildServiceProvider();
    return (new PerspectiveRebuilder(
        sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PerspectiveRebuilder>.Instance), dispatcher);
  }

  [Test]
  public async Task RebuildStreamsAsync_WithARowDigest_RecordsBeforeAndAfterOnCompletedAsync() {
    var registry = new FakePerspectiveRunnerRegistry(new FakePerspectiveRunner());
    var digest = new SequencedRowDigest();
    var (rebuilder, dispatcher) = _buildWith(registry, digest);

    var result = await rebuilder.RebuildStreamsAsync("TestPerspective", [Guid.NewGuid()]);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(digest.Calls).IsEqualTo(2).Because("once before the replay and once after");

    var completed = dispatcher.Published.OfType<PerspectiveRebuildCompleted>().ToList();
    await Assert.That(completed.Count).IsEqualTo(1);
    await Assert.That(completed[0].RowDigestBefore).IsEqualTo("digest-1");
    await Assert.That(completed[0].RowDigestAfter).IsEqualTo("digest-2");
  }

  // A digest is evidence ABOUT a rebuild, not part of it. If it cannot be computed the rebuild still succeeded,
  // and the fields stay absent, which reads as "not known" rather than as "nothing changed".
  [Test]
  public async Task RebuildStreamsAsync_WhenTheDigestThrows_SucceedsAndRecordsNoDigestAsync() {
    var registry = new FakePerspectiveRunnerRegistry(new FakePerspectiveRunner());
    var (rebuilder, dispatcher) = _buildWith(registry, new ThrowingRowDigest());

    var result = await rebuilder.RebuildStreamsAsync("TestPerspective", [Guid.NewGuid()]);

    await Assert.That(result.Success).IsTrue().Because("a digest failure must not fail the rebuild");

    var completed = dispatcher.Published.OfType<PerspectiveRebuildCompleted>().ToList();
    await Assert.That(completed.Count).IsEqualTo(1);
    await Assert.That(completed[0].RowDigestBefore).IsNull();
    await Assert.That(completed[0].RowDigestAfter).IsNull();
  }

  // Same principle one level up: a rebuild that did its work must not be reported as failed because the record of
  // it could not be written. The caller already holds the RebuildResult.
  [Test]
  public async Task RebuildStreamsAsync_WhenPublishingTheRecordFails_StillSucceedsAsync() {
    var registry = new FakePerspectiveRunnerRegistry(new FakePerspectiveRunner());
    var (rebuilder, dispatcher) = _buildWith(registry, digest: null, throwOnPublish: true);

    var result = await rebuilder.RebuildStreamsAsync("TestPerspective", [Guid.NewGuid()]);

    await Assert.That(result.Success).IsTrue().Because("the rebuild itself succeeded; only its record failed");
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);
    await Assert.That(dispatcher.Published.Count).IsEqualTo(0).Because("every publish was refused");
  }

  /// <summary>Implements only the pre-provenance members, so the interface's default bodies are what run.</summary>
  private sealed class OriginUnawareRebuilder : IPerspectiveRebuilder {
    public List<string> Calls { get; } = [];

    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) {
      Calls.Add($"bluegreen:{perspectiveName}");
      return Task.FromResult(new RebuildResult(perspectiveName, 0, 0, TimeSpan.Zero, Success: true, Error: null));
    }

    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) {
      Calls.Add($"inplace:{perspectiveName}");
      return Task.FromResult(new RebuildResult(perspectiveName, 0, 0, TimeSpan.Zero, Success: true, Error: null));
    }

    public Task<RebuildResult> RebuildStreamsAsync(
        string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) {
      Calls.Add($"streams:{perspectiveName}:{streamIds.Count()}");
      return Task.FromResult(new RebuildResult(perspectiveName, 0, 0, TimeSpan.Zero, Success: true, Error: null));
    }

    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<RebuildStatus?>(null);
  }

  // The origin overloads carry default bodies so an implementation written before provenance keeps compiling.
  // This fixes that forwarding: if a default body were changed to throw, or to drop the call, an older
  // implementation would break at runtime with nothing to catch it.
  [Test]
  public async Task OriginOverloads_WithoutAnOverride_ForwardToTheOriginLessMethodsAsync() {
    IPerspectiveRebuilder rebuilder = new OriginUnawareRebuilder();
    var origin = new RebuildOrigin(Guid.NewGuid(), RebuildTrigger.Requested, "an operator");
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };

    var blueGreen = await rebuilder.RebuildBlueGreenAsync("P", origin);
    var inPlace = await rebuilder.RebuildInPlaceAsync("P", origin);
    var selected = await rebuilder.RebuildStreamsAsync("P", streams, origin);

    await Assert.That(blueGreen.Success).IsTrue();
    await Assert.That(inPlace.Success).IsTrue();
    await Assert.That(selected.Success).IsTrue();

    var calls = ((OriginUnawareRebuilder)rebuilder).Calls;
    await Assert.That(calls).Contains("bluegreen:P");
    await Assert.That(calls).Contains("inplace:P");
    await Assert.That(calls).Contains("streams:P:2");
  }

}
