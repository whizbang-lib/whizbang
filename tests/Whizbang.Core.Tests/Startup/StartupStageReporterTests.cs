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
    coordinator.Failure = new InvalidOperationException("function wh_record_startup_stage does not exist");
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
  public async Task WithoutAnInstanceIdentity_NothingIsWrittenAsync() {
    var (reporter, coordinator, observer, _, _) = _build(withIdentity: false);
    await reporter.EnterAsync(StartupStage.Running, "serving");

    await reporter.InstanceRegisteredAsync();

    await Assert.That(coordinator.Writes).IsEmpty()
      .Because("a host with no instance identity has no row to record on");
    await Assert.That(observer.Changes[^1].Recorded).IsFalse();
  }

  [Test]
  public async Task WithoutAWorkCoordinator_NothingIsWrittenAsync() {
    var (reporter, _, observer, _, _) = _build(withCoordinator: false);
    await reporter.InstanceRegisteredAsync();

    await reporter.EnterAsync(StartupStage.Running, "serving");

    await Assert.That(observer.Changes[^1].Recorded).IsFalse()
      .Because("a host with no storage has no instance rows");
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
  public async Task Construct_WithoutItsCollaborators_IsRefusedAsync() {
    var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var logger = new FakeLogger<StartupStageReporter>();

    await Assert.That(() => new StartupStageReporter(null!, null, [], logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new StartupStageReporter(scopes, null, null!, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new StartupStageReporter(scopes, null, [], null!)).Throws<ArgumentNullException>();
  }

  // ─────────────────────────────────────────────────────────────────────────

  private static (StartupStageReporter Reporter, RecordingCoordinator Coordinator, RecordingObserver Observer,
      FakeLogger<StartupStageReporter> Logger, FakeTimeProvider Clock) _build(
      bool withIdentity = true, bool withCoordinator = true) {
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
      scopes, withIdentity ? instance : null, [observer], logger, clock);
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
    public List<(StartupStage Stage, string Message)> Writes { get; } = [];
    public List<Guid> InstanceIds { get; } = [];
    public bool RowExists { get; set; } = true;
    public Exception? Failure { get; set; }
    public bool Hang { get; set; }
    public Action? OnWrite { get; set; }

    public async Task<bool> RecordStartupStageAsync(
        Guid instanceId, StartupStage stage, string message, CancellationToken cancellationToken = default) {
      OnWrite?.Invoke();
      if (Failure is not null) {
        throw Failure;
      }
      if (Hang) {
        await Task.Delay(Timeout.Infinite, cancellationToken);
      }
      if (!RowExists) {
        return false;
      }
      InstanceIds.Add(instanceId);
      Writes.Add((stage, message));
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
