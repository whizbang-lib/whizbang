using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The blue-green rebuild: it replays into a shadow table (the stores of its flow are redirected there), catches the
/// shadow up with the streams written while it ran, the last of them inside the swap, and swaps it in. Before
/// #1025 it replayed into the live table exactly as the in-place rebuild did.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs</code-under-test>
[Category("Unit")]
[Category("Perspectives")]
public class PerspectiveRebuilderBlueGreenTests {
  private const string PERSPECTIVE = "TestPerspective";
  private const string MODEL = "global::Test.Model";
  private const string TABLE = "wh_per_test";
  private const string SHADOW = "wh_per_test_bg";
  private const string EVENT_TYPE = "TestEvent";
  private const string COLLECTIVE_TYPE = "TestCollective";

  private static readonly Guid _a = Guid.Parse("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = Guid.Parse("00000000-0000-0000-0000-00000000000b");
  private static readonly Guid _c = Guid.Parse("00000000-0000-0000-0000-00000000000c");
  private static readonly Guid _d = Guid.Parse("00000000-0000-0000-0000-00000000000d");

  [Test]
  public async Task BlueGreen_WithoutASwapper_ReplaysInPlace_AndSaysSoAsync() {
    var fixture = new Fixture(swapper: null);
    fixture.Add(_a, 1);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(fixture.Runner.Runs.Single().Table).IsEqualTo(TABLE)
      .Because("With no driver support there is no shadow table, so the replay writes the live table, as it always did.");
  }

  [Test]
  public async Task BlueGreen_ReplaysIntoTheShadowTable_ThenSwapsItInAsync() {
    var swapper = new FakeSwapper();
    var fixture = new Fixture(swapper);
    fixture.Add(_a, 1);
    fixture.Add(_b, 2);
    fixture.Add(_c, 3);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(3);
    await Assert.That(fixture.Runner.Runs.Select(r => r.Table).Distinct()).IsEquivalentTo([SHADOW])
      .Because("Every stream is replayed into the shadow table; the live table is never written by the rebuild.");
    await Assert.That(swapper.Calls).IsEquivalentTo(["find", "create " + TABLE, "swap " + TABLE + "->" + SHADOW + " keep 00:00:10"]);
    await Assert.That(fixture.Runner.Runs.Select(r => r.Phase).Distinct()).IsEquivalentTo([RebuildPhase.Replaying]);
    await Assert.That(fixture.Runner.Runs[^1].Status!.TotalStreams).IsEqualTo(3)
      .Because("Progress is reported while the rebuild runs.");
    await Assert.That(await fixture.Rebuilder.GetRebuildStatusAsync(PERSPECTIVE)).IsNull();
  }

  [Test]
  public async Task BlueGreen_CatchesUpTheStreamsWrittenWhileItRan_AndTheLastOnesUnderTheWriteLockAsync() {
    var swapper = new FakeSwapper();
    var fixture = new Fixture(swapper);
    fixture.Add(_a, 1);
    fixture.Add(_b, 2);
    // A live writer commits to A and to a new stream D while the first pass runs.
    fixture.Runner.OnFirstRun = () => {
      fixture.Add(_a, 10);
      fixture.Add(_d, 11);
    };
    // And to B just before the swap closed the table to writers.
    swapper.BeforeLock = () => fixture.Add(_b, 12);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(swapper.Deleted.Select(ids => string.Join(",", ids)))
      .IsEquivalentTo([$"{_a},{_d}", $"{_b}"])
      .Because("A caught-up stream's shadow row is deleted, so the stream is folded again from its first event.");
    var catchUps = fixture.Runner.Runs.Where(r => r.Phase == RebuildPhase.CatchingUp).Select(r => r.StreamId).ToList();
    await Assert.That(catchUps).IsEquivalentTo([_a, _d]);
    var underLock = fixture.Runner.Runs.Where(r => r.Phase == RebuildPhase.Swapping).ToList();
    await Assert.That(underLock.Select(r => r.StreamId)).IsEquivalentTo([_b]);
    await Assert.That(underLock[0].Table).IsEqualTo(SHADOW)
      .Because("The catch-up inside the swap still writes the shadow table.");
    await Assert.That(result.StreamsProcessed).IsEqualTo(5);
  }

  [Test]
  public async Task BlueGreen_WithNoCatchUpPasses_LeavesEveryChangeToTheSwapAsync() {
    var swapper = new FakeSwapper();
    var fixture = new Fixture(swapper, options: new BlueGreenRebuildOptions { MaxCatchUpPasses = 0, KeepPreviousTable = false, SwapLockTimeout = TimeSpan.FromSeconds(2) });
    fixture.Add(_a, 1);
    fixture.Runner.OnFirstRun = () => fixture.Add(_a, 5);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(fixture.Runner.Runs.Select(r => (r.StreamId, r.Phase)))
      .IsEquivalentTo([(_a, RebuildPhase.Replaying), (_a, RebuildPhase.Swapping)]);
    await Assert.That(swapper.Calls[^1]).IsEqualTo("swap " + TABLE + "->" + SHADOW + " drop 00:00:02");
  }

  [Test]
  public async Task BlueGreen_EventsNotYetStamped_CountAsChangedAsync() {
    var swapper = new FakeSwapper();
    var fixture = new Fixture(swapper, options: new BlueGreenRebuildOptions { MaxCatchUpPasses = 1 });
    fixture.Add(_a, commitSequence: null);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(fixture.Runner.Runs.Select(r => r.Phase))
      .IsEquivalentTo([RebuildPhase.Replaying, RebuildPhase.CatchingUp, RebuildPhase.Swapping])
      .Because("An event with no commit sequence may have committed after the rebuild read its stream, so it is replayed again.");
  }

  [Test]
  public async Task BlueGreen_ACollectiveCommittedWhileItRan_ReplaysEveryStreamAsync() {
    var swapper = new FakeSwapper();
    var fixture = new Fixture(swapper, collectives: new FakeCollectives());
    fixture.Add(_a, 1);
    fixture.Add(_b, 2);
    fixture.Runner.OnFirstRun = () => fixture.Add(_c, 7, COLLECTIVE_TYPE);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(fixture.Runner.Runs.Where(r => r.Phase == RebuildPhase.CatchingUp).Select(r => r.StreamId))
      .IsEquivalentTo([_a, _b])
      .Because("A collective can change any row, so every stream is caught up, and the collective's own stream is not one of the perspective's.");
  }

  [Test]
  public async Task BlueGreen_WithoutEventTypeMetadata_TreatsEveryEventAsThePerspectivesAsync() {
    var swapper = new FakeSwapper();
    var fixture = new Fixture(swapper, withInfo: false);
    fixture.Add(_a, 1, "SomethingElse");
    fixture.Runner.OnFirstRun = () => fixture.Add(_a, 3, "SomethingElse");

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(fixture.Runner.Runs.Select(r => r.Phase)).IsEquivalentTo([RebuildPhase.Replaying, RebuildPhase.CatchingUp]);
  }

  [Test]
  public async Task BlueGreen_NoRegisteredTable_FailsWithoutCreatingAShadowAsync() {
    var swapper = new FakeSwapper { Table = null };
    var fixture = new Fixture(swapper);
    fixture.Add(_a, 1);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).Contains("no registered table");
    await Assert.That(swapper.Calls).IsEquivalentTo(["find"]);
  }

  [Test]
  public async Task BlueGreen_ASwapThatFails_DropsTheShadow_AndReportsTheFailureAsync() {
    var swapper = new FakeSwapper { SwapFailure = new InvalidOperationException("lock timeout") };
    var fixture = new Fixture(swapper);
    fixture.Add(_a, 1);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).IsEqualTo("lock timeout");
    await Assert.That(swapper.Calls[^1]).IsEqualTo("drop " + SHADOW)
      .Because("A failed rebuild leaves the live table as it was and no shadow behind.");
  }

  [Test]
  public async Task BlueGreen_AShadowThatCannotBeDropped_StillReportsTheRebuildsFailureAsync() {
    var swapper = new FakeSwapper { SwapFailure = new InvalidOperationException("lock timeout"), DropFailure = new InvalidOperationException("gone") };
    var fixture = new Fixture(swapper);
    fixture.Add(_a, 1);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Error).IsEqualTo("lock timeout");
  }

  [Test]
  public async Task Rebuild_ThatFailsBeforeItStarts_ReportsNoStreamsAsync() {
    var provider = new ServiceCollection().BuildServiceProvider();
    var rebuilder = new PerspectiveRebuilder(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PerspectiveRebuilder>.Instance);

    var result = await rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.StreamsProcessed).IsEqualTo(0);
    await Assert.That(result.EventsReplayed).IsEqualTo(0);
  }

  // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

  private sealed class Fixture {
    private readonly List<EventStoreRecord> _records = [];

    public Fixture(FakeSwapper? swapper, BlueGreenRebuildOptions? options = null, FakeCollectives? collectives = null, bool withInfo = true) {
      Runner = new RecordingRunner();
      var services = new ServiceCollection();
      services.AddSingleton<IPerspectiveRunnerRegistry>(new Registry(Runner, withInfo));
      services.AddSingleton<IEventStoreQuery>(new ListEventStoreQuery(_records));
      if (swapper is not null) {
        services.AddSingleton<IPerspectiveTableSwapper>(swapper);
      }
      if (options is not null) {
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
      }
      if (collectives is not null) {
        services.AddSingleton<ICollectiveReplayApplier>(collectives);
      }
      var provider = services.BuildServiceProvider();
      Rebuilder = new PerspectiveRebuilder(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PerspectiveRebuilder>.Instance);
      Runner.Rebuilder = Rebuilder;
    }

    public PerspectiveRebuilder Rebuilder { get; }
    public RecordingRunner Runner { get; }

    public void Add(Guid stream, long? commitSequence, string eventType = EVENT_TYPE) =>
      _records.Add(new EventStoreRecord {
        Id = Guid.NewGuid(),
        StreamId = stream,
        AggregateId = stream,
        AggregateType = "Test",
        Version = 1,
        EventType = eventType,
        EventData = JsonDocument.Parse("{}").RootElement,
        Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] },
        CreatedAt = DateTime.UtcNow,
        CommitSequence = commitSequence,
      });
  }

  private sealed record Run(Guid StreamId, string Table, RebuildPhase Phase, RebuildStatus? Status);

  private sealed class RecordingRunner : IPerspectiveRunner {
    public List<Run> Runs { get; } = [];
    public Action? OnFirstRun { get; set; }
    public PerspectiveRebuilder? Rebuilder { get; set; }
    public Type PerspectiveType => typeof(object);

    public async Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) {
      var status = await Rebuilder!.GetRebuildStatusAsync(perspectiveName, cancellationToken);
      Runs.Add(new Run(streamId, PerspectiveTableRedirect.Resolve(TABLE), status!.Phase, status));
      if (Runs.Count == 1) {
        OnFirstRun?.Invoke();
      }
      return new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.NewGuid(),
        Status = PerspectiveProcessingStatus.Completed,
      };
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  private sealed class Registry(IPerspectiveRunner runner, bool withInfo) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      withInfo ? [new PerspectiveRegistrationInfo(PERSPECTIVE, "Test.TestPerspective", MODEL, [EVENT_TYPE])] : [];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class ListEventStoreQuery(List<EventStoreRecord> records) : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query => records.AsQueryable();
    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => Query.Where(e => e.StreamId == streamId);
    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => Query.Where(e => e.EventType == eventType);
  }

  private sealed class FakeSwapper : IPerspectiveTableSwapper {
    public List<string> Calls { get; } = [];
    public List<Guid[]> Deleted { get; } = [];
    public string? Table { get; init; } = TABLE;
    public Action? BeforeLock { get; set; }
    public Exception? SwapFailure { get; init; }
    public Exception? DropFailure { get; init; }

    public Task<string?> FindTableAsync(string perspectiveName, CancellationToken cancellationToken) {
      Calls.Add("find");
      return Task.FromResult(Table);
    }

    public Task<string> CreateShadowAsync(string liveTable, CancellationToken cancellationToken) {
      Calls.Add("create " + liveTable);
      return Task.FromResult(liveTable + "_bg");
    }

    public Task DeleteRowsAsync(string table, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) {
      Deleted.Add([.. ids]);
      return Task.CompletedTask;
    }

    public async Task<string?> SwapAsync(PerspectiveTableSwap swap, Func<CancellationToken, Task> underWriteLock, CancellationToken cancellationToken) {
      Calls.Add($"swap {swap.LiveTable}->{swap.ShadowTable} {(swap.KeepPrevious ? "keep" : "drop")} {swap.LockTimeout}");
      if (SwapFailure is not null) {
        throw SwapFailure;
      }
      BeforeLock?.Invoke();
      await underWriteLock(cancellationToken);
      return swap.KeepPrevious ? swap.LiveTable + "_bg_old" : null;
    }

    public Task DropAsync(string table, CancellationToken cancellationToken) {
      Calls.Add("drop " + table);
      return DropFailure is null ? Task.CompletedTask : Task.FromException(DropFailure);
    }
  }

  private sealed class FakeCollectives : ICollectiveReplayApplier {
    public Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveForReplayAsync(
        Type modelType, IReadOnlyList<MessageEnvelope<IEvent>> streamEvents, CancellationToken cancellationToken) =>
      Task.FromResult(streamEvents);

    public object ApplyInMemory(Type modelType, object currentModel, Guid streamId, IEvent collectiveEvent) => currentModel;

    public IReadOnlyList<string> CollectiveEventTypeNamesFor(string modelTypeName) =>
      modelTypeName == MODEL ? [COLLECTIVE_TYPE] : [];
  }
}
