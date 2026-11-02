// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The stage gate (#1636): a startup stage is reached once this instance enters or skips it or any later startup
/// stage, never by a shutdown stage, and every stage up to Coordinate is reached once the schema-ready gate opens.
/// What a worker that must not touch the database before its stage waits on.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageGate.cs</code-under-test>
[Category("Startup")]
public class StartupStageGateTests {
  private static StartupStageSnapshot _at(StartupStage stage, bool skipped = false) =>
    new(stage, "message", skipped, DateTimeOffset.UnixEpoch);

  /// <summary>
  /// The gap the data plane's start point leaves open (#1636): the reporter tells its observers of Accept commands after
  /// it has tried to write the row, and says whether the write landed. When it did not (the database did not answer
  /// within the write's budget, so the stage is held and written again on the next accepted heartbeat), the row still
  /// records the stage before, so the instance is not eligible for work, yet the gate opens and the data plane starts.
  /// A started claim worker then claims: claim_work counts its caller whatever its row says and, with the assigner
  /// leaving an ineligible instance out, falls back to ranking itself (229; locked by
  /// WorkEligibilitySqlTests.Rank_ACallerThatIsNotYetEligible_StillCountsItselfAsync). An eligible stage is reached only
  /// once the row records it; the reporter tells the observers again with recorded = true when the held write lands.
  /// </summary>
  /// <remarks>
  /// RED, for the owner's decision on the fix: a host with no storage never records (its reporter has no coordinator),
  /// so the fix has to tell "nothing to record" from "the write failed" before keying the gate on the record.
  /// </remarks>
  [Test]
  public async Task AnEligibleStage_WhoseRowWriteFailed_IsNotReachedUntilTheRowRecordsItAsync() {
    var gate = new StartupStageGate(SchemaReadyGate.AlreadyReady());
    var accepting = gate.WaitForAsync(StartupStage.AcceptingCommands, CancellationToken.None);

    await gate.OnStageChangedAsync(_at(StartupStage.AcceptingCommands), recorded: false, CancellationToken.None);

    await Assert.That(gate.HasReached(StartupStage.AcceptingCommands)).IsFalse()
      .Because("the row still records the stage before Accept commands, so the instance is not eligible for work, and "
        + "a data-plane worker started now claims a share its peers are also given");
    await Assert.That(accepting.IsCompleted).IsFalse();

    await gate.OnStageChangedAsync(_at(StartupStage.AcceptingCommands), recorded: true, CancellationToken.None);

    await Assert.That(gate.HasReached(StartupStage.AcceptingCommands)).IsTrue()
      .Because("the held write landed, so the row says the instance is eligible");
    await accepting.WaitAsync(CancellationToken.None);
  }

  [Test]
  public async Task BeforeAnyStage_NothingIsReached_AndAWaitIsPendingAsync() {
    var gate = new StartupStageGate(new SchemaReadyGate());

    var wait = gate.WaitForAsync(StartupStage.Preflight, CancellationToken.None);

    await Assert.That(gate.HasReached(StartupStage.Preflight)).IsFalse();
    await Assert.That(wait.IsCompleted).IsFalse();
  }

  [Test]
  public async Task EnteringAStage_ReachesItAndEveryStageBeforeIt_ButNotTheOnesAfterAsync() {
    var gate = new StartupStageGate(new SchemaReadyGate());
    var coordinate = gate.WaitForAsync(StartupStage.Coordinate, CancellationToken.None);
    var accepting = gate.WaitForAsync(StartupStage.AcceptingCommands, CancellationToken.None);

    await gate.OnStageChangedAsync(_at(StartupStage.Coordinate), recorded: true, CancellationToken.None);

    await coordinate;
    await Assert.That(StartupStages.Startup.Where(gate.HasReached)).IsEquivalentTo([
      StartupStage.Preflight, StartupStage.Elect, StartupStage.Migrate, StartupStage.Converge, StartupStage.Coordinate,
    ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(accepting.IsCompleted).IsFalse();
  }

  [Test]
  public async Task ASkippedStage_IsReachedLikeAnEnteredOneAsync() {
    var gate = new StartupStageGate(new SchemaReadyGate());
    var elect = gate.WaitForAsync(StartupStage.Elect, CancellationToken.None);

    await gate.OnStageChangedAsync(_at(StartupStage.Elect, skipped: true), recorded: false, CancellationToken.None);

    await elect;
    await Assert.That(gate.HasReached(StartupStage.Elect)).IsTrue();
    await Assert.That(gate.HasReached(StartupStage.Migrate)).IsFalse();
  }

  [Test]
  public async Task AWaitForAStageAlreadyReached_CompletesAtOnceAsync() {
    var gate = new StartupStageGate(new SchemaReadyGate());
    await gate.OnStageChangedAsync(_at(StartupStage.Running), recorded: true, CancellationToken.None);

    var wait = gate.WaitForAsync(StartupStage.Elect, CancellationToken.None);

    await Assert.That(wait.IsCompletedSuccessfully).IsTrue();
  }

  [Test]
  public async Task AShutdownStage_ReachesNothing_SoAWorkerWaitingForItsStageNeverStartsAsync() {
    var gate = new StartupStageGate(new SchemaReadyGate());
    var coordinate = gate.WaitForAsync(StartupStage.Coordinate, CancellationToken.None);

    await gate.OnStageChangedAsync(_at(StartupStage.Draining), recorded: true, CancellationToken.None);
    await gate.OnStageChangedAsync(_at(StartupStage.Stopped), recorded: true, CancellationToken.None);

    await Assert.That(coordinate.IsCompleted).IsFalse();
    await Assert.That(gate.HasReached(StartupStage.Preflight)).IsFalse();
  }

  [Test]
  public async Task TheSchemaReadyGate_ReachesEveryStageUpToCoordinate_AndNoLaterOneAsync() {
    var schemaReady = new SchemaReadyGate();
    var gate = new StartupStageGate(schemaReady);
    var coordinate = gate.WaitForAsync(StartupStage.Coordinate, CancellationToken.None);
    var accepting = gate.WaitForAsync(StartupStage.AcceptingCommands, CancellationToken.None);

    await Assert.That(gate.HasReached(StartupStage.Elect)).IsFalse().Because("the schema gate is still closed");
    schemaReady.MarkReady();

    await coordinate;
    await Assert.That(gate.HasReached(StartupStage.Coordinate)).IsTrue()
      .Because("the gate opens only at the end of Coordinate, so every stage up to it has been passed");
    await Assert.That(gate.HasReached(StartupStage.AcceptingCommands)).IsFalse();
    await Assert.That(accepting.IsCompleted).IsFalse();
  }

  [Test]
  public async Task AnAlreadyOpenSchemaGate_MeansNoWaitAtAllAsync() {
    var gate = new StartupStageGate(SchemaReadyGate.AlreadyReady());

    var wait = gate.WaitForAsync(StartupStage.Coordinate, CancellationToken.None);

    await Assert.That(wait.IsCompletedSuccessfully).IsTrue();
    await Assert.That(gate.HasReached(StartupStage.Preflight)).IsTrue();
  }

  [Test]
  public async Task AWait_EndsWithItsTokenAsync() {
    var gate = new StartupStageGate(new SchemaReadyGate());
    using var cts = new CancellationTokenSource();

    var before = gate.WaitForAsync(StartupStage.Elect, cts.Token);
    var after = gate.WaitForAsync(StartupStage.Running, cts.Token);
    await cts.CancelAsync();

    await Assert.That(async () => await before).Throws<OperationCanceledException>();
    await Assert.That(async () => await after).Throws<OperationCanceledException>();
  }

  [Test]
  public async Task AShutdownStage_CannotBeWaitedForOrAskedAboutAsync() {
    var gate = new StartupStageGate(new SchemaReadyGate());

    await Assert.That(() => gate.HasReached(StartupStage.Draining)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => gate.WaitForAsync(StartupStage.Stopped, CancellationToken.None))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => gate.HasReached((StartupStage)15)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task ANullSchemaGate_IsRefused_BecauseAMissingOneWouldBeAWaitThatNeverHappensAsync() {
    await Assert.That(() => new StartupStageGate(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ThroughTheReporter_EachStageEnteredIsReachedAsItIsLoggedAsync() {
    var gate = new StartupStageGate(new SchemaReadyGate());
    await using var services = new ServiceCollection().BuildServiceProvider();
    using var reporter = new StartupStageReporter(
      services.GetRequiredService<IServiceScopeFactory>(), new FixedInstance(), [gate], NullLogger<StartupStageReporter>.Instance,
      new Whizbang.Core.Observability.LibraryVersionProvider("1.0.0"));

    await reporter.EnterAsync(StartupStage.Preflight, "host starting");
    await Assert.That(gate.HasReached(StartupStage.Preflight)).IsTrue();
    await Assert.That(gate.HasReached(StartupStage.Elect)).IsFalse();
    await reporter.SkipAsync(StartupStage.Elect, "no election");
    await Assert.That(gate.HasReached(StartupStage.Elect)).IsTrue();
  }

  private sealed class FixedInstance : Whizbang.Core.Observability.IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();
    public string ServiceName => "stages";
    public string HostName => "host";
    public int ProcessId => 1;
    public Whizbang.Core.Observability.ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }
}
