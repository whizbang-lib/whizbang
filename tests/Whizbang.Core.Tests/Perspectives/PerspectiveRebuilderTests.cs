using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for PerspectiveRebuilder — verifies all rebuild modes and error handling.
/// </summary>
public class PerspectiveRebuilderTests {
  [Test]
  public async Task RebuildInPlaceAsync_WithRegisteredPerspective_ProcessesAllStreamsAsync() {
    // Arrange
    var runner = new FakePerspectiveRunner();
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var eventStoreQuery = new FakeEventStoreQuery([Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildInPlaceAsync("TestPerspective");

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(3);
    await Assert.That(result.PerspectiveName).IsEqualTo("TestPerspective");
    await Assert.That(runner.RunCount).IsEqualTo(3);
  }

  [Test]
  public async Task RebuildStreamsAsync_WithSpecificStreams_OnlyProcessesThoseAsync() {
    // Arrange
    var runner = new FakePerspectiveRunner();
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var eventStoreQuery = new FakeEventStoreQuery([]);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);
    var targetStreams = new[] { Guid.NewGuid(), Guid.NewGuid() };

    // Act
    var result = await rebuilder.RebuildStreamsAsync("TestPerspective", targetStreams);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);
    await Assert.That(runner.RunCount).IsEqualTo(2);
  }

  [Test]
  public async Task RebuildStreamsAsync_SkipsEphemeralStreams_DoesNotReplayThemAsync() {
    // Arrange — an ephemeral stream must never be replayed on rebuild: its events self-destruct and its
    // bodies are reaped, so replaying it would corrupt the projection. The guard resolves IWorkCoordinator
    // (optional) and refuses ephemeral streams up front.
    var runner = new FakePerspectiveRunner();
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var sourced1 = Guid.NewGuid();
    var ephemeral = Guid.NewGuid();
    var sourced2 = Guid.NewGuid();
    var eventStoreQuery = new FakeEventStoreQuery([]);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    services.AddSingleton<IWorkCoordinator>(new FakeEphemeralCoordinator(ephemeral));
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildStreamsAsync("TestPerspective", new[] { sourced1, ephemeral, sourced2 });

    // Assert — the two Sourced streams rebuild; the ephemeral one is refused and never replayed.
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(2).Because("Only the two Sourced streams are rebuilt.");
    await Assert.That(runner.RunCount).IsEqualTo(2).Because("The ephemeral stream is never replayed — no corruption from reaped bodies.");
  }

  private sealed class FakeEphemeralCoordinator(Guid ephemeralStream) : IWorkCoordinator {
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<IReadOnlyCollection<Guid>> GetStateBasedStreamIdsAsync(IReadOnlyList<Guid> streamIds, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyCollection<Guid>>([.. streamIds.Where(s => s == ephemeralStream)]);
  }

  [Test]
  public async Task RebuildInPlaceAsync_WithUnknownPerspective_ReturnsFailureAsync() {
    // Arrange
    var registry = new FakePerspectiveRunnerRegistry(runner: null);
    var eventStoreQuery = new FakeEventStoreQuery([]);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildInPlaceAsync("NonexistentPerspective");

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).Contains("No runner found");
  }

  [Test]
  public async Task GetRebuildStatusAsync_WithNoActiveRebuild_ReturnsNullAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(new FakePerspectiveRunnerRegistry(null));
    services.AddSingleton<IEventStoreQuery>(new FakeEventStoreQuery([]));
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var status = await rebuilder.GetRebuildStatusAsync("TestPerspective");

    // Assert
    await Assert.That(status).IsNull();
  }

  [Test]
  public async Task RebuildInPlaceAsync_WithFailingStream_ContinuesWithOtherStreamsAsync() {
    // Arrange
    var runner = new FakePerspectiveRunner { FailOnStreamIndex = 1 };
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    var eventStoreQuery = new FakeEventStoreQuery(streams);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildInPlaceAsync("TestPerspective");

    // Assert — should still succeed overall, but only 2 streams processed
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);
    await Assert.That(runner.RunCount).IsEqualTo(3); // All 3 attempted
  }

  [Test]
  public async Task RebuildBlueGreenAsync_CompletesSuccessfullyAsync() {
    // Arrange
    var runner = new FakePerspectiveRunner();
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var eventStoreQuery = new FakeEventStoreQuery([Guid.NewGuid()]);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildBlueGreenAsync("TestPerspective");

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.Duration.TotalMilliseconds).IsGreaterThanOrEqualTo(0);
  }

  [Test]
  public async Task RebuildInPlaceAsync_WithUnknownPerspective_ErrorIncludesRegisteredNamesAsync() {
    // Arrange — covers line 60: detailed error message with registered perspectives
    var registry = new FakePerspectiveRunnerRegistryWithInfo(runner: null, [
      new PerspectiveRegistrationInfo("MyApp.OrderPerspective", "global::MyApp.OrderPerspective", "global::MyApp.OrderModel", []),
      new PerspectiveRegistrationInfo("MyApp.InventoryPerspective", "global::MyApp.InventoryPerspective", "global::MyApp.InventoryModel", [])
    ]);
    var eventStoreQuery = new FakeEventStoreQuery([]);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildInPlaceAsync("NonexistentPerspective");

    // Assert — error should include the registered perspective names
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).Contains("No runner found");
    await Assert.That(result.Error).Contains("MyApp.OrderPerspective");
    await Assert.That(result.Error).Contains("MyApp.InventoryPerspective");
  }

  [Test]
  public async Task RebuildInPlaceAsync_WhenScopeCreationThrows_ReturnsFailureAsync() {
    // Arrange — covers lines 103-106 (outer catch block)
    var services = new ServiceCollection();
    // Don't register IPerspectiveRunnerRegistry — GetRequiredService will throw
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildInPlaceAsync("TestPerspective");

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).IsNotNull();
    await Assert.That(result.PerspectiveName).IsEqualTo("TestPerspective");
  }

  [Test]
  public async Task RebuildInPlaceAsync_CallsCompleter_WithSuccessfulCompletionsAsync() {
    // Arrange — covers the cursor-persistence fix: rebuilder now captures RunAsync's
    // PerspectiveCursorCompletion return value and flushes through IPerspectiveCheckpointCompleter.
    var runner = new FakePerspectiveRunner();
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    var eventStoreQuery = new FakeEventStoreQuery(streams);
    var completer = new RecordingCheckpointCompleter();

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    services.AddSingleton<IPerspectiveCheckpointCompleter>(completer);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildInPlaceAsync("TestPerspective");

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(3);
    await Assert.That(completer.CompletionsReceived.Count).IsEqualTo(3);
    foreach (var completion in completer.CompletionsReceived) {
      await Assert.That(completion.PerspectiveName).IsEqualTo("TestPerspective");
      await Assert.That(completion.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    }
    var receivedStreamIds = completer.CompletionsReceived.Select(c => c.StreamId).ToHashSet();
    foreach (var s in streams) {
      await Assert.That(receivedStreamIds.Contains(s)).IsTrue();
    }
  }

  [Test]
  public async Task RebuildInPlaceAsync_FailedStream_NotInCompletionsAsync() {
    // Arrange — failed streams must not be forwarded to the completer.
    var runner = new FakePerspectiveRunner { FailOnStreamIndex = 1 };
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    var eventStoreQuery = new FakeEventStoreQuery(streams);
    var completer = new RecordingCheckpointCompleter();

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    services.AddSingleton<IPerspectiveCheckpointCompleter>(completer);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildInPlaceAsync("TestPerspective");

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);
    await Assert.That(completer.CompletionsReceived.Count).IsEqualTo(2);
    await Assert.That(completer.CompletionsReceived.Any(c => c.StreamId == streams[1])).IsFalse();
  }

  [Test]
  public async Task RebuildInPlaceAsync_WithoutCompleterRegistered_StillSucceedsAsync() {
    // Arrange — backward-compatible fallback: if no IPerspectiveCheckpointCompleter is
    // registered (e.g., caller pre-dates this feature), rebuild still updates projections.
    // Cursors won't be persisted, but the rebuild itself must not fail.
    var runner = new FakePerspectiveRunner();
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var eventStoreQuery = new FakeEventStoreQuery([Guid.NewGuid()]);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    // NO IPerspectiveCheckpointCompleter registration.
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildInPlaceAsync("TestPerspective");

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);
  }

  [Test]
  public async Task RebuildInPlaceAsync_WithSyncQueryable_UsesNonAsyncFallbackAsync() {
    // Arrange — covers lines 136-138 (sync IQueryable fallback in ToListAsync)
    // The default FakeEventStoreQuery returns a regular IQueryable (not IAsyncEnumerable),
    // so it exercises the else branch in ToListAsync
    var runner = new FakePerspectiveRunner();
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var streamIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
    var eventStoreQuery = new FakeEventStoreQuery(streamIds);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act — FakeEventStoreQuery.Query returns a plain IQueryable (not IAsyncEnumerable),
    // triggering the sync fallback path in QueryableExtensions.ToListAsync
    var result = await rebuilder.RebuildInPlaceAsync("TestPerspective");

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);
  }

  // --- Test Doubles ---

  private sealed class FakePerspectiveRunnerRegistryWithInfo(
      IPerspectiveRunner? runner,
      IReadOnlyList<PerspectiveRegistrationInfo> registrations) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => registrations;
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class FakePerspectiveRunner : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object); // Fake — no real perspective type
    public int RunCount { get; private set; }
    public int FailOnStreamIndex { get; init; } = -1;

    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) {
      var index = RunCount;
      RunCount++;

      if (index == FailOnStreamIndex) {
        throw new InvalidOperationException($"Simulated failure on stream index {index}");
      }

      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.NewGuid(),
        Status = PerspectiveProcessingStatus.Completed
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
        RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
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

  private sealed class FakeEventStoreQuery(Guid[] streamIds) : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query =>
        streamIds.Select(id => new EventStoreRecord {
          Id = Guid.NewGuid(),
          StreamId = id,
          AggregateId = id,
          AggregateType = "Test",
          Version = 1,
          EventType = "TestEvent",
          EventData = JsonDocument.Parse("{}").RootElement,
          Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] },
          CreatedAt = DateTime.UtcNow
        }).AsQueryable();

    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) =>
        Query.Where(e => e.StreamId == streamId);

    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) =>
        Query.Where(e => e.EventType == eventType);
  }

  // ---------------------------------------------------------------------------------------------
  // Stream-group presence reconcile. A rebuilt follower can end up holding rows for streams its
  // announcers no longer carry — the rebuild replays from the event store, which still has the
  // history the announcer already evicted. Reconciling after the replay is what removes them.
  // ---------------------------------------------------------------------------------------------

  private sealed class GroupAnnouncerModel;
  private sealed class GroupFollowerModel;

  private sealed class ReconcileCoordinator : IWorkCoordinator {
    public List<PerspectiveTableName> Tables { get; init; } = [];
    public List<(string Follower, IReadOnlyCollection<string> Announcers)> Reconciled { get; } = [];

    public Task<IReadOnlyList<PerspectiveTableName>> GetPerspectiveTableNamesAsync(
        IReadOnlyCollection<string> clrTypeNames, CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<PerspectiveTableName>>(Tables);

    public Task<int> ReconcileFollowerPresenceAsync(
        string followerTable, IReadOnlyCollection<string> announcerTables, CancellationToken ct = default) {
      lock (Reconciled) { Reconciled.Add((followerTable, announcerTables)); }
      return Task.FromResult(0);
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] m, int partitionCount, CancellationToken ct = default)
      => Task.CompletedTask;
  }

  private static PerspectiveRebuilder _rebuilderWith(
      ReconcileCoordinator? coordinator, out ReconcileCoordinator coord) {
    coord = coordinator ?? new ReconcileCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(new FakePerspectiveRunnerRegistry(new FakePerspectiveRunner()));
    services.AddSingleton<IEventStoreQuery>(new FakeEventStoreQuery([Guid.NewGuid()]));
    if (coordinator is not null) {
      services.AddSingleton<IWorkCoordinator>(coord);
    }
    var sp = services.BuildServiceProvider();
    return new PerspectiveRebuilder(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PerspectiveRebuilder>.Instance);
  }

  [Test]
  [NotInParallel("PerspectiveStreamGroupRegistry")]
  public async Task Rebuild_ForAPerspectiveInNoGroup_ReconcilesNothingAsync() {
    PerspectiveStreamGroupRegistry.Clear();
    try {
      var rebuilder = _rebuilderWith(new ReconcileCoordinator(), out var coord);

      await rebuilder.RebuildInPlaceAsync("NotAGroupMember");

      await Assert.That(coord.Reconciled).IsEmpty();
    } finally {
      PerspectiveStreamGroupRegistry.Clear();
    }
  }

  [Test]
  [NotInParallel("PerspectiveStreamGroupRegistry")]
  public async Task Rebuild_ForAnAnnouncerOnly_ReconcilesNothingAsync() {
    // Reconcile only makes sense for a follower: an announcer has no upstream to be absent from.
    PerspectiveStreamGroupRegistry.Clear();
    try {
      PerspectiveStreamGroupRegistry.Register(
        typeof(GroupAnnouncerModel), "g", announce: true, follow: false, bridge: false);
      var rebuilder = _rebuilderWith(new ReconcileCoordinator(), out var coord);

      await rebuilder.RebuildInPlaceAsync(typeof(GroupAnnouncerModel).FullName!);

      await Assert.That(coord.Reconciled).IsEmpty();
    } finally {
      PerspectiveStreamGroupRegistry.Clear();
    }
  }

  [Test]
  [NotInParallel("PerspectiveStreamGroupRegistry")]
  public async Task Rebuild_ForAFollower_ReconcilesAgainstItsAnnouncersAsync() {
    PerspectiveStreamGroupRegistry.Clear();
    try {
      PerspectiveStreamGroupRegistry.Register(
        typeof(GroupAnnouncerModel), "g", announce: true, follow: false, bridge: false);
      PerspectiveStreamGroupRegistry.Register(
        typeof(GroupFollowerModel), "g", announce: false, follow: true, bridge: false);

      var coordinator = new ReconcileCoordinator {
        Tables = {
          new(typeof(GroupFollowerModel).FullName!, "wh_per_follower"),
          new(typeof(GroupAnnouncerModel).FullName!, "wh_per_announcer"),
        },
      };
      var rebuilder = _rebuilderWith(coordinator, out var coord);

      await rebuilder.RebuildInPlaceAsync(typeof(GroupFollowerModel).FullName!);

      await Assert.That(coord.Reconciled).IsNotEmpty();
      await Assert.That(coord.Reconciled[0].Follower).IsEqualTo("wh_per_follower");
      await Assert.That(coord.Reconciled[0].Announcers).Contains("wh_per_announcer");
    } finally {
      PerspectiveStreamGroupRegistry.Clear();
    }
  }

  [Test]
  [NotInParallel("PerspectiveStreamGroupRegistry")]
  public async Task Rebuild_WithNoCoordinatorRegistered_SkipsReconcileAsync() {
    // A host without a work coordinator can still rebuild; there is simply nothing to
    // reconcile against, and that must not fail the rebuild.
    PerspectiveStreamGroupRegistry.Clear();
    try {
      PerspectiveStreamGroupRegistry.Register(
        typeof(GroupFollowerModel), "g", announce: false, follow: true, bridge: false);
      var rebuilder = _rebuilderWith(null, out _);

      var result = await rebuilder.RebuildInPlaceAsync(typeof(GroupFollowerModel).FullName!);

      await Assert.That(result.Success).IsTrue();
    } finally {
      PerspectiveStreamGroupRegistry.Clear();
    }
  }

  [Test]
  [NotInParallel("PerspectiveStreamGroupRegistry")]
  public async Task Rebuild_WhenTheTablesCannotBeResolved_SkipsReconcileAsync() {
    PerspectiveStreamGroupRegistry.Clear();
    try {
      PerspectiveStreamGroupRegistry.Register(
        typeof(GroupAnnouncerModel), "g", announce: true, follow: false, bridge: false);
      PerspectiveStreamGroupRegistry.Register(
        typeof(GroupFollowerModel), "g", announce: false, follow: true, bridge: false);

      // No table names come back, so there is nothing to reconcile between.
      var rebuilder = _rebuilderWith(new ReconcileCoordinator(), out var coord);

      await rebuilder.RebuildInPlaceAsync(typeof(GroupFollowerModel).FullName!);

      await Assert.That(coord.Reconciled).IsEmpty();
    } finally {
      PerspectiveStreamGroupRegistry.Clear();
    }
  }
}
