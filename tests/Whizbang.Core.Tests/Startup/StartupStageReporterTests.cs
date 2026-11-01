// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The one owner of this instance's stage (#1636): stages only move forward; each is logged at Information as
/// <c>Stage X of 7: &lt;name&gt; - &lt;message&gt;</c> (or <c>Shutdown stage: ...</c>); one entered before the
/// instance's row exists is held and written when the row is registered; a write that fails is held until the next
/// registration signal; and every change is observed.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageReporter.cs</code-under-test>
[Category("Startup")]
public class StartupStageReporterTests {
  private static readonly DateTimeOffset _start = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
  private const string VERSION = "1.2.3";

  // --- the log line ----------------------------------------------------------------------------

  [Test]
  public async Task Enter_AStartupStage_LogsStageXOfSeven_AtInformationAsync() {
    var (reporter, _, _, logger, _) = _build();

    await reporter.EnterAsync(StartupStage.Migrate, "migration 42 of 231");

    var record = logger.Collector.LatestRecord;
    await Assert.That(record.Level).IsEqualTo(LogLevel.Information);
    await Assert.That(record.Message).IsEqualTo("Stage 3 of 7: Migrate - migration 42 of 231");
  }

  [Test]
  public async Task Enter_AShutdownStage_LogsAShutdownStageLineAsync() {
    var (reporter, _, _, logger, _) = _build();

    await reporter.EnterAsync(StartupStage.Draining, "host stopping");

    await Assert.That(logger.Collector.LatestRecord.Level).IsEqualTo(LogLevel.Information);
    await Assert.That(logger.Collector.LatestRecord.Message).IsEqualTo("Shutdown stage: Draining - host stopping");
  }

  [Test]
  public async Task Enter_EveryStartupStageInOrder_LogsEachLine_AndObservesEachOnceAsync() {
    var (reporter, _, observer, logger, clock) = _build();

    foreach (var stage in StartupStages.Startup) {
      await reporter.EnterAsync(stage, $"in {stage}");
      clock.Advance(TimeSpan.FromSeconds(1));
    }

    var lines = logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Information).Select(r => r.Message).ToList();
    await Assert.That(lines).IsEquivalentTo([
      "Stage 1 of 7: Preflight - in Preflight",
      "Stage 2 of 7: Elect - in Elect",
      "Stage 3 of 7: Migrate - in Migrate",
      "Stage 4 of 7: Converge - in Converge",
      "Stage 5 of 7: Coordinate - in Coordinate",
      "Stage 6 of 7: Accept commands - in AcceptingCommands",
      "Stage 7 of 7: Running - in Running",
    ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(observer.Changes.Select(c => c.Snapshot.Stage)).IsEquivalentTo(
      StartupStages.Startup, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(observer.Changes.Select(c => c.Snapshot.EnteredAt)).IsEquivalentTo(
      Enumerable.Range(0, 7).Select(i => _start.AddSeconds(i)), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("each stage is stamped when it is entered, by the reporter's clock");
  }

  // --- forward only ----------------------------------------------------------------------------

  [Test]
  public async Task Enter_AnEarlierStage_ChangesNothingAsync() {
    var (reporter, coordinator, observer, logger, _) = _build();
    await reporter.InstanceRegisteredAsync();
    await reporter.EnterAsync(StartupStage.Converge, "analyzing");
    var logged = logger.Collector.Count;

    await reporter.EnterAsync(StartupStage.Elect, "voting");

    await Assert.That(reporter.Current!.Stage).IsEqualTo(StartupStage.Converge)
      .Because("a second initialization or a retry passes back through stages this instance has already left");
    await Assert.That(logger.Collector.Count).IsEqualTo(logged);
    await Assert.That(observer.Changes.Count).IsEqualTo(1);
    await Assert.That(coordinator.Writes.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Enter_TheCurrentStage_WithANewMessage_IsProgress_AndKeepsWhenItWasEnteredAsync() {
    var (reporter, _, observer, logger, clock) = _build();
    await reporter.EnterAsync(StartupStage.Migrate, "migration 1 of 3");
    clock.Advance(TimeSpan.FromSeconds(5));

    await reporter.EnterAsync(StartupStage.Migrate, "migration 2 of 3");

    await Assert.That(reporter.Current!.Message).IsEqualTo("migration 2 of 3");
    await Assert.That(reporter.Current.EnteredAt).IsEqualTo(_start)
      .Because("progress within a stage is not a new stage: the time in stage keeps counting");
    await Assert.That(logger.Collector.LatestRecord.Message).IsEqualTo("Stage 3 of 7: Migrate - migration 2 of 3");
    await Assert.That(observer.Changes.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Enter_TheCurrentStage_WithTheSameMessage_ChangesNothingAsync() {
    var (reporter, _, observer, logger, _) = _build();
    await reporter.EnterAsync(StartupStage.Running, "serving");
    var logged = logger.Collector.Count;

    await reporter.EnterAsync(StartupStage.Running, "serving");

    await Assert.That(logger.Collector.Count).IsEqualTo(logged)
      .Because("resuming from a pause re-enters Running, and that is not a transition worth a line");
    await Assert.That(observer.Changes.Count).IsEqualTo(1);
  }

  // --- skipped ---------------------------------------------------------------------------------

  [Test]
  public async Task Skip_ALaterStage_RecordsItSkipped_WithTheReasonAsync() {
    var (reporter, coordinator, observer, logger, _) = _build();
    await reporter.InstanceRegisteredAsync();
    await reporter.EnterAsync(StartupStage.Elect, "voting");

    await reporter.SkipAsync(StartupStage.Migrate, "the schema is already current");

    await Assert.That(reporter.Current!.Stage).IsEqualTo(StartupStage.Migrate);
    await Assert.That(reporter.Current.Skipped).IsTrue();
    await Assert.That(reporter.Current.Message).IsEqualTo("skipped: the schema is already current");
    await Assert.That(logger.Collector.LatestRecord.Message)
      .IsEqualTo("Stage 3 of 7: Migrate - skipped: the schema is already current");
    await Assert.That(coordinator.Writes[^1]).IsEqualTo((StartupStage.Migrate, "skipped: the schema is already current"))
      .Because("a skipped stage is recorded on the row like any other, with why");
    await Assert.That(observer.Changes[^1].Snapshot.Skipped).IsTrue();
  }

  [Test]
  public async Task Skip_TheCurrentOrAnEarlierStage_ChangesNothingAsync() {
    var (reporter, _, observer, _, _) = _build();
    await reporter.EnterAsync(StartupStage.Migrate, "waiting for the migrator");

    await reporter.SkipAsync(StartupStage.Migrate, "current");
    await reporter.SkipAsync(StartupStage.Elect, "no election");

    await Assert.That(reporter.Current!.Message).IsEqualTo("waiting for the migrator");
    await Assert.That(reporter.Current.Skipped).IsFalse();
    await Assert.That(observer.Changes.Count).IsEqualTo(1);
  }

  // --- the row: held before registration, written at it ----------------------------------------

  [Test]
  public async Task Enter_BeforeTheInstanceRegisters_HoldsTheStage_AndWritesNothingAsync() {
    var (reporter, coordinator, observer, _, _) = _build();

    await reporter.EnterAsync(StartupStage.Preflight, "host starting");
    await reporter.EnterAsync(StartupStage.Elect, "voting");

    await Assert.That(coordinator.Writes).IsEmpty()
      .Because("the instance's row does not exist until it registers, so there is nothing to write to");
    await Assert.That(observer.Changes.Select(c => c.Recorded)).IsEquivalentTo([false, false]);
  }

  [Test]
  public async Task InstanceRegistered_WritesTheHeldStage_OnceAsync() {
    var (reporter, coordinator, observer, _, _) = _build();
    await reporter.EnterAsync(StartupStage.Preflight, "host starting");
    await reporter.EnterAsync(StartupStage.Elect, "voting");

    await reporter.InstanceRegisteredAsync();
    await reporter.InstanceRegisteredAsync();

    await Assert.That(coordinator.Writes).IsEquivalentTo([(StartupStage.Elect, "voting")])
      .Because("the stage held until the row existed is the current one, written once");
    await Assert.That(coordinator.InstanceIds.Distinct()).IsEquivalentTo([coordinator.ExpectedInstanceId]);
    await Assert.That(observer.Changes[^1]).IsEqualTo((reporter.Current!, true));
    await Assert.That(observer.Changes.Count).IsEqualTo(3);
  }

  [Test]
  public async Task InstanceRegistered_BeforeAnyStage_WritesNothingAsync() {
    var (reporter, coordinator, observer, _, _) = _build();

    await reporter.InstanceRegisteredAsync();

    await Assert.That(coordinator.Writes).IsEmpty();
    await Assert.That(observer.Changes).IsEmpty();
  }

  [Test]
  public async Task AfterRegistration_EachStageIsWrittenAsItIsEnteredAsync() {
    var (reporter, coordinator, observer, _, _) = _build();
    await reporter.InstanceRegisteredAsync();

    await reporter.EnterAsync(StartupStage.Converge, "analyzing");
    await reporter.EnterAsync(StartupStage.Coordinate, "recomputing");

    await Assert.That(coordinator.Writes).IsEquivalentTo(
      [(StartupStage.Converge, "analyzing"), (StartupStage.Coordinate, "recomputing")],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(observer.Changes.Select(c => c.Recorded)).IsEquivalentTo([true, true]);
  }

  [Test]
  public async Task AWriteThatFindsNoRow_IsHeld_AndWrittenOnTheNextRegistrationSignalAsync() {
    var (reporter, coordinator, observer, _, _) = _build();
    await reporter.InstanceRegisteredAsync();
    coordinator.RowExists = false;
    await reporter.EnterAsync(StartupStage.Migrate, "applying");
    coordinator.RowExists = true;

    await reporter.InstanceRegisteredAsync();

    await Assert.That(coordinator.Writes).IsEquivalentTo([(StartupStage.Migrate, "applying")]);
    await Assert.That(observer.Changes.Select(c => c.Recorded)).IsEquivalentTo(
      [false, true], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task AWriteThatFails_IsHeld_AndWrittenOnTheNextRegistrationSignalAsync() {
    var (reporter, coordinator, observer, logger, _) = _build();
    await reporter.InstanceRegisteredAsync();
    coordinator.Failure = new InvalidOperationException("function wh_record_instance_transition does not exist");
    await reporter.EnterAsync(StartupStage.Migrate, "applying");
    coordinator.Failure = null;

    await reporter.InstanceRegisteredAsync();

    await Assert.That(observer.Changes.Select(c => c.Recorded)).IsEquivalentTo(
      [false, true], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a lost write must not leave the row reading an earlier stage for good");
    await Assert.That(logger.Collector.GetSnapshot().Any(r =>
      r.Level == LogLevel.Debug && r.Exception is InvalidOperationException
      && r.Message.Contains("Migrate", StringComparison.Ordinal))).IsTrue()
      .Because("a failed write says which stage it was, and why");
  }

  [Test]
  public async Task AWriteThatDoesNotAnswer_IsAbandonedAfterItsBudget_AndHeldAsync() {
    var (reporter, coordinator, observer, _, clock) = _build();
    await reporter.InstanceRegisteredAsync();
    coordinator.Hang = true;

    var entering = reporter.EnterAsync(StartupStage.AcceptingCommands, "accepting").AsTask();
    await Assert.That(entering.IsCompleted).IsFalse();
    clock.Advance(TimeSpan.FromSeconds(5));
    await entering;

    await Assert.That(observer.Changes[^1].Recorded).IsFalse()
      .Because("a write is bounded so a stalled database cannot hold a lifecycle transition");
  }

  [Test]
  public async Task AWriteCanceledByTheCaller_PropagatesTheCancellationAsync() {
    var (reporter, coordinator, _, _, _) = _build();
    await reporter.InstanceRegisteredAsync();
    using var cts = new CancellationTokenSource();
    coordinator.OnWrite = () => {
      cts.Cancel();
      throw new OperationCanceledException(cts.Token);
    };

    await Assert.That(async () => await reporter.EnterAsync(StartupStage.Running, "serving", cts.Token))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task WithoutAWorkCoordinator_NothingIsWrittenAsync() {
    var (reporter, _, observer, _, _) = _build(withCoordinator: false);
    await reporter.InstanceRegisteredAsync();

    await reporter.EnterAsync(StartupStage.Running, "serving");

    await Assert.That(observer.Changes[^1].Recorded).IsFalse()
      .Because("a host with no storage has no instance rows");
  }

  // --- one write per transition: stage, phase and version together (#1636) -------------------------

  [Test]
  public async Task AStageWrite_CarriesTheStagesPhase_AndTheLibraryVersion_InOneCallAsync() {
    var (reporter, coordinator, _, _, _) = _build();
    await reporter.InstanceRegisteredAsync();

    await reporter.EnterAsync(StartupStage.AcceptingCommands, "accepting");

    await Assert.That(coordinator.Transitions).IsEquivalentTo([
      new InstanceTransition(coordinator.ExpectedInstanceId, StartupStage.AcceptingCommands, "accepting",
        LifecyclePhase.AcceptingCommands, VERSION),
    ]).Because("the stage record is the only writer of the phase, and the version rides the same update");
  }

  [Test]
  public async Task WithoutAVersionProvider_NoVersionIsWrittenAsync() {
    var (reporter, coordinator, _, _, _) = _build(libraryVersion: null);
    await reporter.InstanceRegisteredAsync();

    await reporter.EnterAsync(StartupStage.Running, "serving");

    await Assert.That(coordinator.Transitions.Single().LibraryVersion).IsNull()
      .Because("a null version keeps whatever the row records");
  }

  [Test]
  [Arguments(LifecyclePhase.Pausing)]
  [Arguments(LifecyclePhase.Paused)]
  [Arguments(LifecyclePhase.StandingBy)]
  [Arguments(LifecyclePhase.Resuming)]
  [Arguments(LifecyclePhase.Faulted)]
  [Arguments(LifecyclePhase.Halted)]
  public async Task EnterPhase_OffTheLadder_WritesThePhase_AndKeepsTheStageAsync(LifecyclePhase phase) {
    var (reporter, coordinator, _, _, clock) = _build();
    await reporter.InstanceRegisteredAsync();
    await reporter.EnterAsync(StartupStage.Running, "serving");
    var entered = reporter.Current!;
    clock.Advance(TimeSpan.FromSeconds(3));

    await reporter.EnterPhaseAsync(phase);

    await Assert.That(coordinator.Transitions[^1]).IsEqualTo(
      new InstanceTransition(coordinator.ExpectedInstanceId, StartupStage.Running, "serving", phase, VERSION))
      .Because("a pause or a stand-by is a phase-only write: one update that keeps the stage");
    await Assert.That(coordinator.Transitions.Count).IsEqualTo(2);
    await Assert.That(reporter.Current).IsEqualTo(entered).Because("the stage, and when it was entered, stay");
  }

  [Test]
  public async Task EnterPhase_IsNotAStage_SoNothingIsLogged_AndNoObserverIsToldAsync() {
    var (reporter, _, observer, logger, _) = _build();
    await reporter.InstanceRegisteredAsync();
    await reporter.EnterAsync(StartupStage.Running, "serving");
    var lines = logger.Collector.GetSnapshot().Count(r => r.Level >= LogLevel.Information);

    await reporter.EnterPhaseAsync(LifecyclePhase.Paused);

    await Assert.That(logger.Collector.GetSnapshot().Count(r => r.Level >= LogLevel.Information)).IsEqualTo(lines);
    await Assert.That(observer.Changes.Count).IsEqualTo(1).Because("observers watch stages, and the stage did not change");
  }

  [Test]
  public async Task EnterPhase_TheSamePhaseAgain_WritesNothingAsync() {
    var (reporter, coordinator, _, _, _) = _build();
    await reporter.InstanceRegisteredAsync();
    await reporter.EnterAsync(StartupStage.Running, "serving");

    await reporter.EnterPhaseAsync(LifecyclePhase.Paused);
    await reporter.EnterPhaseAsync(LifecyclePhase.Paused);

    await Assert.That(coordinator.Transitions.Select(t => t.Phase)).IsEquivalentTo(
      [LifecyclePhase.Running, LifecyclePhase.Paused], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Enter_TheCurrentStageAfterAPause_WritesItsOwnPhaseAgain_AndKeepsWhenItWasEnteredAsync() {
    var (reporter, coordinator, observer, _, clock) = _build();
    await reporter.InstanceRegisteredAsync();
    await reporter.EnterAsync(StartupStage.Running, "serving");
    await reporter.EnterPhaseAsync(LifecyclePhase.Paused);
    clock.Advance(TimeSpan.FromSeconds(3));

    await reporter.EnterAsync(StartupStage.Running, "serving");

    await Assert.That(coordinator.Transitions.Select(t => t.Phase)).IsEquivalentTo(
      [LifecyclePhase.Running, LifecyclePhase.Paused, LifecyclePhase.Running],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a resume back into Running is a transition the row has to record, though the stage never left Running");
    await Assert.That(reporter.Current!.EnteredAt).IsEqualTo(_start);
    await Assert.That(observer.Changes.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Enter_AnEarlierStageDuringAPause_ChangesNothingAsync() {
    var (reporter, coordinator, _, _, _) = _build();
    await reporter.InstanceRegisteredAsync();
    await reporter.EnterAsync(StartupStage.Running, "serving");
    await reporter.EnterPhaseAsync(LifecyclePhase.Paused);

    await reporter.EnterAsync(StartupStage.Migrate, "applying");

    await Assert.That(coordinator.Transitions.Select(t => t.Phase)).IsEquivalentTo(
      [LifecyclePhase.Running, LifecyclePhase.Paused], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Enter_ALaterStageDuringAPause_WritesThatStagesPhaseAsync() {
    var (reporter, coordinator, _, _, _) = _build();
    await reporter.InstanceRegisteredAsync();
    await reporter.EnterAsync(StartupStage.Running, "serving");
    await reporter.EnterPhaseAsync(LifecyclePhase.Paused);

    await reporter.EnterAsync(StartupStage.Draining, "stopping");

    await Assert.That(coordinator.Transitions[^1].Phase).IsEqualTo(LifecyclePhase.Stopping);
  }

  [Test]
  public async Task EnterPhase_BeforeRegistration_IsHeld_AndWrittenWithTheStageAtRegistrationAsync() {
    var (reporter, coordinator, _, _, _) = _build();
    await reporter.EnterAsync(StartupStage.Preflight, "host starting");
    await reporter.EnterPhaseAsync(LifecyclePhase.StandingBy);

    await reporter.InstanceRegisteredAsync();

    await Assert.That(coordinator.Transitions).IsEquivalentTo([
      new InstanceTransition(coordinator.ExpectedInstanceId, StartupStage.Preflight, "host starting",
        LifecyclePhase.StandingBy, VERSION),
    ]);
  }

  [Test]
  public async Task EnterPhase_BeforeAnyStage_IsWrittenWithNoStageAsync() {
    var (reporter, coordinator, observer, _, _) = _build();
    await reporter.InstanceRegisteredAsync();

    await reporter.EnterPhaseAsync(LifecyclePhase.Halted);

    await Assert.That(coordinator.Transitions).IsEquivalentTo([
      new InstanceTransition(coordinator.ExpectedInstanceId, null, null, LifecyclePhase.Halted, VERSION),
    ]).Because("a null stage keeps whatever the row records");
    await Assert.That(observer.Changes).IsEmpty();
  }

  [Test]
  public async Task EnterPhase_ThatFindsNoRow_BeforeAnyStage_IsHeld_AndWrittenOnTheNextRegistrationSignalAsync() {
    var (reporter, coordinator, observer, _, _) = _build();
    await reporter.InstanceRegisteredAsync();
    coordinator.RowExists = false;
    await reporter.EnterPhaseAsync(LifecyclePhase.Paused);
    coordinator.RowExists = true;

    await reporter.InstanceRegisteredAsync();

    await Assert.That(coordinator.Transitions.Select(t => t.Phase)).IsEquivalentTo([LifecyclePhase.Paused]);
    await Assert.That(observer.Changes).IsEmpty().Because("with no stage there is nothing for a stage observer to see");
  }

  [Test]
  [Arguments(LifecyclePhase.Starting)]
  [Arguments(LifecyclePhase.Connecting)]
  [Arguments(LifecyclePhase.Migrating)]
  [Arguments(LifecyclePhase.AcceptingCommands)]
  [Arguments(LifecyclePhase.Running)]
  [Arguments(LifecyclePhase.Stopping)]
  [Arguments(LifecyclePhase.Stopped)]
  [Arguments((LifecyclePhase)99)]
  public async Task EnterPhase_OnTheLadderOrUndefined_IsRefusedAsync(LifecyclePhase phase) {
    var (reporter, coordinator, _, _, _) = _build();
    await reporter.InstanceRegisteredAsync();

    await Assert.That(async () => await reporter.EnterPhaseAsync(phase)).Throws<ArgumentOutOfRangeException>()
      .Because("a phase on the ladder is the stage's own: entering the stage records it");
    await Assert.That(coordinator.Transitions).IsEmpty();
  }

  // --- arguments -------------------------------------------------------------------------------

  [Test]
  public async Task Enter_WithoutAMessage_IsRefusedAsync() {
    var (reporter, _, _, _, _) = _build();

    await Assert.That(async () => await reporter.EnterAsync(StartupStage.Running, " ")).Throws<ArgumentException>();
    await Assert.That(async () => await reporter.SkipAsync(StartupStage.Running, "")).Throws<ArgumentException>();
  }

  [Test]
  public async Task Enter_AnUndefinedStage_IsRefusedAsync() {
    var (reporter, _, _, _, _) = _build();

    await Assert.That(async () => await reporter.EnterAsync((StartupStage)15, "x"))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(reporter.Current).IsNull();
  }

  [Test]
  public async Task WithoutAClock_TheSystemClockStampsTheStageAsync() {
    var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var reporter = new StartupStageReporter(scopes, new StubInstanceProvider(), [], new FakeLogger<StartupStageReporter>());

    await reporter.EnterAsync(StartupStage.Preflight, "host starting");

    await Assert.That(reporter.Current!.EnteredAt).IsNotEqualTo(DateTimeOffset.MinValue);
  }

  [Test]
  public async Task Construct_WithoutItsCollaborators_IsRefusedAsync() {
    var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var logger = new FakeLogger<StartupStageReporter>();

    var instance = new StubInstanceProvider();

    await Assert.That(() => new StartupStageReporter(null!, instance, [], logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new StartupStageReporter(scopes, null!, [], logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new StartupStageReporter(scopes, instance, null!, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new StartupStageReporter(scopes, instance, [], null!)).Throws<ArgumentNullException>();
  }

  // ─────────────────────────────────────────────────────────────────────────

  private static (StartupStageReporter Reporter, RecordingCoordinator Coordinator, RecordingObserver Observer,
      FakeLogger<StartupStageReporter> Logger, FakeTimeProvider Clock) _build(
      bool withCoordinator = true, string? libraryVersion = VERSION) {
    var instance = new StubInstanceProvider();
    var coordinator = new RecordingCoordinator(instance.InstanceId);
    var services = new ServiceCollection();
    if (withCoordinator) {
      services.AddScoped<IWorkCoordinator>(_ => coordinator);
    }
    var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var observer = new RecordingObserver();
    var logger = new FakeLogger<StartupStageReporter>();
    var clock = new FakeTimeProvider(_start);
    var reporter = new StartupStageReporter(
      scopes, instance, [observer], logger, clock,
      libraryVersion is null ? null : new LibraryVersionProvider(libraryVersion));
    return (reporter, coordinator, observer, logger, clock);
  }

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class RecordingObserver : IStartupStageObserver {
    public List<(StartupStageSnapshot Snapshot, bool Recorded)> Changes { get; } = [];

    public ValueTask OnStageChangedAsync(StartupStageSnapshot snapshot, bool recorded, CancellationToken cancellationToken) {
      Changes.Add((snapshot, recorded));
      return ValueTask.CompletedTask;
    }
  }

  private sealed class RecordingCoordinator(Guid expectedInstanceId) : IWorkCoordinator {
    public Guid ExpectedInstanceId { get; } = expectedInstanceId;
    /// <summary>Every transition written, whole: one call per write.</summary>
    public List<InstanceTransition> Transitions { get; } = [];
    /// <summary>The stage and message of each write that named a stage.</summary>
    public List<(StartupStage Stage, string Message)> Writes =>
      [.. Transitions.Where(t => t.Stage is not null).Select(t => (t.Stage!.Value, t.Message!))];
    public List<Guid> InstanceIds { get; } = [];
    public bool RowExists { get; set; } = true;
    public Exception? Failure { get; set; }
    public bool Hang { get; set; }
    public Action? OnWrite { get; set; }

    public async Task<bool> RecordInstanceTransitionAsync(
        InstanceTransition transition, CancellationToken cancellationToken = default) {
      OnWrite?.Invoke();
      if (Failure is not null) {
        throw Failure;
      }
      if (Hang) {
        // Completed by the reporter's write budget on the test's fake clock, inline in the test's own flow.
        var canceled = new TaskCompletionSource();
        await using var _ = cancellationToken.Register(() => canceled.TrySetCanceled(cancellationToken));
        await canceled.Task;
      }
      if (!RowExists) {
        return false;
      }
      InstanceIds.Add(transition.InstanceId);
      Transitions.Add(transition);
      return true;
    }

    // Unused surface for this test.
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }
}
