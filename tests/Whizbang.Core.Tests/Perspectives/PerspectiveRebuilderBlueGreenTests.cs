// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Startup;
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

  /// <summary>
  /// The table registry is keyed by the MODEL's type name, which is what the schema pass writes, so asking for
  /// the PERSPECTIVE's name matches nothing and the rebuild throws "no registered table" before replaying a
  /// single stream (#1308). Every blue-green test passed through that bug because the fake swapper answered
  /// whatever it was asked; this one asserts WHICH name it was asked for.
  /// </summary>
  [Test]
  public async Task BlueGreen_LooksTheTableUpByTheModelName_NotThePerspectiveAsync() {
    var swapper = new FakeSwapper();
    var fixture = new Fixture(swapper);
    fixture.Add(_a, 1);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(swapper.FoundFor).IsEqualTo(MODEL)
      .Because("the registry is keyed by the model, so the model's name is the only one that can find a table");
    await Assert.That(swapper.FoundFor).IsNotEqualTo(PERSPECTIVE)
      .Because("asking for the perspective name is the defect itself, and it fails the same way for every perspective");
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
    await Assert.That(swapper.Calls).IsEquivalentTo(
      ["find", "create " + TABLE, "swap " + TABLE + "->" + SHADOW + " keep 00:00:10"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
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

  private const string DUTY = "perspective-rebuild:" + TABLE;

  /// <summary>
  /// #2301: two blue-green rebuilds of one table share its shadow, and creating the shadow drops one left behind, so a
  /// second rebuild would destroy the first one's replay. Each rebuild holds the table's duty from before it creates the
  /// shadow until the swap is done, success or failure; the duty is an elected role, so it holds across the fleet.
  /// </summary>
  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task BlueGreen_HoldsTheTablesDuty_FromBeforeTheShadowUntilAfterTheSwapAsync(bool swapFails) {
    var swapper = new FakeSwapper { SwapFailure = swapFails ? new InvalidOperationException("lock timeout") : null };
    var fixture = new Fixture(swapper, elector: new FakeElector(swapper.Calls));
    fixture.Add(_a, 1);

    await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(swapper.Calls[1]).IsEqualTo("acquire " + DUTY);
    await Assert.That(swapper.Calls[2]).IsEqualTo("create " + TABLE);
    await Assert.That(swapper.Calls[^1]).IsEqualTo("release " + DUTY);
  }

  /// <summary>#2301: a rebuild whose table's duty another instance holds fails at once, says why, and touches nothing.</summary>
  [Test]
  public async Task BlueGreen_WhenAnotherInstanceHoldsTheTablesDuty_FailsWithoutTouchingTheShadowAsync() {
    var swapper = new FakeSwapper();
    var fixture = new Fixture(swapper, elector: new FakeElector(swapper.Calls) { Refuse = DutyRefusal.Contended });
    fixture.Add(_a, 1);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).Contains("another blue-green rebuild of " + TABLE + " is in flight");
    await Assert.That(swapper.Calls).IsEquivalentTo(["find", "acquire " + DUTY], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  /// <summary>
  /// #2301: an elector that is registered but cannot answer (unavailable), refuses this instance (evicted), or will not
  /// vote (paused, standing down or stopping) cannot confirm that no other instance is rebuilding the table, so the
  /// rebuild fails closed, naming the refusal and its detail, and touches nothing.
  /// </summary>
  [Test]
  [Arguments(DutyRefusal.Unavailable)]
  [Arguments(DutyRefusal.Refused)]
  [Arguments(DutyRefusal.Halted)]
  public async Task BlueGreen_WhenTheDutyCannotBeVotedFor_FailsClosedAsync(DutyRefusal refusal) {
    var swapper = new FakeSwapper();
    var fixture = new Fixture(swapper, elector: new FakeElector(swapper.Calls) { Refuse = refusal });
    fixture.Add(_a, 1);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).Contains("could not take the duty " + DUTY).And.Contains(refusal.ToString())
      .And.Contains("detail of " + refusal);
    await Assert.That(swapper.Calls).DoesNotContain("create " + TABLE);
  }

  /// <summary>
  /// #2301: with no elector (none registered, or the framework's null default) there is no fleet to coordinate with, so
  /// the rebuild runs, guarded on this instance alone, and the host is told so once, not on every rebuild.
  /// </summary>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task BlueGreen_WithNoElector_Rebuilds_AndSaysOnceThatOnlyThisInstanceIsGuardedAsync(bool nullDefaultRegistered) {
    var swapper = new FakeSwapper();
    var logger = new Microsoft.Extensions.Logging.Testing.FakeLogger<PerspectiveRebuilder>();
    var fixture = new Fixture(swapper, elector: nullDefaultRegistered ? NullDutyElector.Instance : null, logger: logger);
    fixture.Add(_a, 1);

    var first = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);
    var second = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(first.Success).IsTrue().Because(first.Error ?? "");
    await Assert.That(second.Success).IsTrue().Because(second.Error ?? "");
    await Assert.That(logger.Collector.GetSnapshot().Count(r => r.Message.Contains("no duty elector", StringComparison.Ordinal)))
      .IsEqualTo(1);
  }

  /// <summary>
  /// #2301: an elector grants a duty this instance already holds again (one tenure, several handles), so the duty alone
  /// cannot keep two rebuilds of one table on the same instance apart. A second rebuild started here while the first
  /// runs is refused before it votes, and the first completes.
  /// </summary>
  [Test]
  public async Task BlueGreen_ASecondRebuildOfTheTableOnThisInstance_IsRefusedBeforeItVotesAsync() {
    var swapper = new FakeSwapper();
    var fixture = new Fixture(swapper, elector: new FakeElector(swapper.Calls));
    fixture.Add(_a, 1);
    RebuildResult? second = null;
    // Once: unguarded, the second rebuild's own swap would start a third from the same hook.
    swapper.BeforeSwapAsync = async () => {
      swapper.BeforeSwapAsync = null;
      second = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);
    };

    var first = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(first.Success).IsTrue().Because(first.Error ?? "");
    await Assert.That(second!.Success).IsFalse();
    await Assert.That(second.Error).Contains("another blue-green rebuild of " + TABLE + " is in flight");
    await Assert.That(swapper.Calls.Count(c => c == "acquire " + DUTY)).IsEqualTo(1);
  }

  /// <summary>
  /// #2301: a rebuild runs for far longer than a duty's lease, so the duty is kept alive while it runs: the grant is
  /// verified, which renews it, on its renew interval. Without it the lease would lapse mid-replay and another instance
  /// could take the table's duty and start a second rebuild.
  /// </summary>
  [Test]
  public async Task BlueGreen_KeepsTheTablesDutyAliveWhileItRunsAsync() {
    var swapper = new FakeSwapper();
    var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
    var elector = new FakeElector(swapper.Calls) { RenewInterval = TimeSpan.FromSeconds(2) };
    var fixture = new Fixture(swapper, elector: elector, time: time);
    fixture.Add(_a, 1);
    var verifiedWhileRunning = false;
    // FakeTimeProvider runs the keepalive's tick inside Advance, and a tick starts its verify before it yields, so the
    // grant has been verified by the time Advance returns: nothing is waited on.
    swapper.BeforeSwapAsync = () => {
      time.Advance(TimeSpan.FromSeconds(2));
      verifiedWhileRunning = elector.Verifications > 0;
      return Task.CompletedTask;
    };

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(verifiedWhileRunning).IsTrue().Because("the keepalive renewed the duty while the rebuild held it");
  }

  /// <summary>
  /// #2301: an elector that throws fails the rebuild with its error and leaves the table free on this instance, so the
  /// next rebuild of it is voted for rather than refused as already in flight.
  /// </summary>
  [Test]
  public async Task BlueGreen_WhenTheElectorThrows_FailsAndLeavesTheTableFreeHereAsync() {
    var swapper = new FakeSwapper();
    var elector = new FakeElector(swapper.Calls) { Failure = new InvalidOperationException("the vote failed") };
    var fixture = new Fixture(swapper, elector: elector);
    fixture.Add(_a, 1);

    var failed = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);
    elector.Failure = null;
    var rebuilt = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(failed.Error).IsEqualTo("the vote failed");
    await Assert.That(rebuilt.Success).IsTrue().Because(rebuilt.Error ?? "");
  }

  /// <summary>#2301: the duty's name is the prefix and the bare table, and a table must be named.</summary>
  [Test]
  public async Task TheDutyName_IsThePrefixAndTheTableAsync() {
    await Assert.That(PerspectiveRebuildDuty.For(TABLE)).IsEqualTo("perspective-rebuild:" + TABLE);
    await Assert.That(() => PerspectiveRebuildDuty.For(" ")).ThrowsExactly<ArgumentException>();
  }

  /// <summary>#2301: a driver that cannot say which perspective writes a table says none, so no rebuild rewrites it.</summary>
  [Test]
  public async Task TheDefaultLookup_FindsNoPerspectiveAsync() {
    IPerspectiveTableSwapper plain = new PlainSwapper();

    await Assert.That(await plain.FindPerspectiveAsync(TABLE, CancellationToken.None)).IsNull();
  }

  private sealed class PlainSwapper : IPerspectiveTableSwapper {
    public Task<string?> FindTableAsync(string modelTypeName, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string> CreateShadowAsync(string liveTable, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task DeleteRowsAsync(string table, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string?> SwapAsync(PerspectiveTableSwap swap, Func<CancellationToken, Task> underWriteLock, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task DropAsync(string table, CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  /// <summary>Records each vote and release in the swapper's call list; refuses as told, or grants.</summary>
  private sealed class FakeElector(List<string> calls) : IDutyElector {
    public DutyRefusal? Refuse { get; init; }
    public TimeSpan? RenewInterval { get; init; }
    private int _verifications;

    /// <summary>How many times a grant was verified, which is how the keepalive renews it.</summary>
    public int Verifications => Volatile.Read(ref _verifications);
    public Exception? Failure { get; set; }

    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      calls.Add("acquire " + duty);
      if (Failure is not null) {
        return Task.FromException<DutyAttempt>(Failure);
      }
      return Task.FromResult(Refuse is { } refusal
        ? DutyAttempt.Lost(refusal, "detail of " + refusal)
        : DutyAttempt.Granted(new Grant(calls, duty, this)));
    }

    private sealed class Grant(List<string> calls, string duty, FakeElector elector) : IDutyGrant {
      public string Duty => duty;
      public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
      public TimeSpan? RenewInterval => elector.RenewInterval;

      public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) {
        Interlocked.Increment(ref elector._verifications);
        return Task.FromResult(true);
      }

      public ValueTask DisposeAsync() {
        calls.Add("release " + duty);
        return ValueTask.CompletedTask;
      }
    }
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

    public Fixture(
        FakeSwapper? swapper, BlueGreenRebuildOptions? options = null, FakeCollectives? collectives = null, bool withInfo = true,
        IDutyElector? elector = null, TimeProvider? time = null,
        Microsoft.Extensions.Logging.ILogger<PerspectiveRebuilder>? logger = null) {
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
      if (elector is not null) {
        services.AddSingleton(elector);
      }
      if (time is not null) {
        services.AddSingleton(time);
      }
      var provider = services.BuildServiceProvider();
      Rebuilder = new PerspectiveRebuilder(provider.GetRequiredService<IServiceScopeFactory>(),
        logger ?? NullLogger<PerspectiveRebuilder>.Instance);
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
        EventData = JsonElement.Parse("{}"),
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
        RowChanged = true,
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

    /// <summary>Runs inside the swap, before the write lock's catch-up, while the rebuild still holds its table (#2301).</summary>
    public Func<Task>? BeforeSwapAsync { get; set; }

    /// <summary>The name the rebuilder asked for. Recorded because asking for the wrong one is #1308.</summary>
    public string? FoundFor { get; private set; }

    public Task<string?> FindTableAsync(string modelTypeName, CancellationToken cancellationToken) {
      Calls.Add("find");
      FoundFor = modelTypeName;
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
      if (BeforeSwapAsync is { } beforeSwap) {
        await beforeSwap();
      }
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
