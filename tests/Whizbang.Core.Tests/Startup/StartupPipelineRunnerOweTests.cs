// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The rolling-deploy gap (#966): a <c>Skip</c> step that a non-holder skips is OWED to the duty,
/// so the instance that holds it, now or later, runs it. Owing is best effort, so a store that
/// fails leaves the step exactly as skipped as it was before owing existed.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineRunner.cs</code-under-test>
[Category("Startup")]
public class StartupPipelineRunnerOweTests {

  private sealed class SkipStep : IStartupStep {
    public int Runs { get; private set; }
    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = "Rewrite",
      RequiredCapability = StartupDuties.MAINTAINER,
      NonHolderBehavior = NonHolderBehavior.Skip,
    };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
      Runs++;
      return ValueTask.FromResult(new StartupStepReport(StartupStepOutcome.Completed));
    }
  }

  private sealed class ContendedElector : IDutyElector {
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) =>
      Task.FromResult(DutyAttempt.Lost(DutyRefusal.Contended, "held by an older instance"));
  }

  private sealed class Store(Func<Task>? onOwe = null) : IPendingDutyWorkStore {
    public List<(string Role, string Key)> Owes { get; } = [];
    public Task OweAsync(string role, string workKey, CancellationToken cancellationToken) {
      Owes.Add((role, workKey));
      return onOwe is null ? Task.CompletedTask : onOwe();
    }
    public Task<IReadOnlyList<PendingDutyWork>> ListOwedAsync(string role, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<DutyWorkCompletion> CompleteAsync(PendingDutyWork work, IDutyGrant grant, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> RecordFailureAsync(PendingDutyWork work, IDutyGrant grant, string failure, CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  [Test]
  public async Task ASkippedDutyStep_IsOwedToTheDutysHolderAsync() {
    var store = new Store();
    var step = new SkipStep();
    var runner = new StartupPipelineRunner([step], [], new ContendedElector(), store);

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(step.Runs).IsEqualTo(0);
    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(results[0].Reason).IsEqualTo("capability not held; owed to the duty's holder");
    await Assert.That(store.Owes).IsEquivalentTo([(StartupDuties.MAINTAINER, "Rewrite")]);
  }

  private sealed class FailingElector : IDutyElector {
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) =>
      Task.FromException<DutyAttempt>(new TimeoutException("read timeout"));
  }

  [Test]
  public async Task AStepSkippedBecauseTheElectorCouldNotBeAsked_IsOwedTooAsync() {
    var store = new Store();
    var runner = new StartupPipelineRunner([new SkipStep()], [], new FailingElector(), store);

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(results[0].Reason)
      .IsEqualTo("capability undetermined: the elector failed with TimeoutException: read timeout; owed to the duty's holder")
      .Because("whoever holds the duty should run a step nobody here could decide about");
    await Assert.That(store.Owes).Count().IsEqualTo(1);
  }

  [Test]
  public async Task WithoutAStore_ASkippedStepIsSimplySkippedAsync() {
    var results = await new StartupPipelineRunner([new SkipStep()], [], new ContendedElector()).RunAsync(CancellationToken.None);

    await Assert.That(results[0].Reason).IsEqualTo("capability not held");
  }

  [Test]
  public async Task TheNullDefaultStore_OwesNothing_AndRefusesToCompleteAsync() {
    var store = NullPendingDutyWorkStore.Instance;
    var work = new PendingDutyWork(StartupDuties.MAINTAINER, "Rewrite", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, null, true);

    await Assert.That(store.IsConfigured).IsFalse();
    await store.OweAsync(StartupDuties.MAINTAINER, "Rewrite", CancellationToken.None);
    await Assert.That(await store.ListOwedAsync(StartupDuties.MAINTAINER, CancellationToken.None)).IsEmpty();
    await Assert.That(() => store.CompleteAsync(work, null!, CancellationToken.None)).Throws<InvalidOperationException>();
    await Assert.That(() => store.RecordFailureAsync(work, null!, "x", CancellationToken.None)).Throws<InvalidOperationException>();
    await Assert.That(((IPendingDutyWorkStore)new Store()).IsConfigured).IsTrue();
    await Assert.That(() => new StartupPipelineRunner([], [], new ContendedElector(), null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AStoreThatCannotOwe_LeavesTheStepSkipped_AndSaysSoAsync() {
    var runner = new StartupPipelineRunner([new SkipStep()], [], new ContendedElector(), new Store(() => Task.FromException(new InvalidOperationException("down"))));

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(results[0].Reason).IsEqualTo("capability not held; could not be owed to the duty's holder (InvalidOperationException)");
  }

  [Test]
  public async Task CancellationWhileOwing_PropagatesAsync() {
    using var canceled = new CancellationTokenSource();
    var runner = new StartupPipelineRunner([new SkipStep()], [], new ContendedElector(),
      new Store(async () => {
        // The host stops while the owe is in flight.
        await canceled.CancelAsync();
        throw new OperationCanceledException(canceled.Token);
      }));

    await Assert.That(async () => await runner.RunAsync(canceled.Token)).Throws<OperationCanceledException>();
  }
}
