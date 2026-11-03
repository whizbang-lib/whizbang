// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// #1992: <c>Assess</c> is the first stage of startup and produces one verdict, once. The Assess step, the schema
/// initializer and the driver's schema pass all read it through one <see cref="StartupAssessmentSource"/>, so none of
/// them can see a different answer and none reads the version record again. A temporary failure to read the record is
/// retried within Preflight; any other fails startup loudly and is never latched.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupAssessmentSource.cs</code-under-test>
[Category("Startup")]
public class StartupAssessmentSourceTests {

  private static readonly StartupAssessment _serve = new(StartupVerdict.Serve, "nothing newer recorded");

  [Test]
  public async Task EveryCaller_ReadsTheOneVerdict_AndTheRecordIsReadOnceAsync() {
    var assessor = new CountingAssessor(_ => Task.FromResult(_serve));
    var source = _source(assessor);

    var first = await source.AssessAsync(CancellationToken.None);
    var second = await source.AssessAsync(CancellationToken.None);

    await Assert.That(assessor.Calls).IsEqualTo(1)
      .Because("one verdict, once: a second reader is given the answer the first read produced");
    await Assert.That(second).IsSameReferenceAs(first);
    await Assert.That(source.Verdict).IsSameReferenceAs(first);
  }

  [Test]
  public async Task CallersArrivingTogether_ShareOneReadAsync() {
    var release = new TaskCompletionSource<StartupAssessment>(TaskCreationOptions.RunContinuationsAsynchronously);
    var assessor = new CountingAssessor(_ => release.Task);
    var source = _source(assessor);

    var first = source.AssessAsync(CancellationToken.None);
    var second = source.AssessAsync(CancellationToken.None);
    release.SetResult(_serve);

    await Assert.That(await first).IsSameReferenceAs(_serve);
    await Assert.That(await second).IsSameReferenceAs(_serve);
    await Assert.That(assessor.Calls).IsEqualTo(1);
  }

  [Test]
  public async Task BeforeAnyRead_NoVerdictIsHeld_AndConfigurationFollowsTheAssessorAsync() {
    var source = _source(new CountingAssessor(_ => Task.FromResult(_serve)));
    var unassessed = _source(NullStartupAssessor.Instance);

    await Assert.That(source.Verdict).IsNull();
    await Assert.That(source.IsConfigured).IsTrue();
    await Assert.That(unassessed.IsConfigured).IsFalse()
      .Because("no driver supplied an assessor: callers take the unassessed path, exactly as before");
  }

  [Test]
  public async Task ATemporaryFailure_IsRetriedWithinPreflight_UntilTheVerdictLandsAsync() {
    var clock = new FakeTimeProvider();
    var retrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var stages = new SignalingReporter("could not be read for a moment", retrying);
    var assessor = new CountingAssessor(call => call == 1
      ? Task.FromException<StartupAssessment>(FakeDbException.WithSqlState("57P03"))
      : Task.FromResult(_serve));
    var source = new StartupAssessmentSource(assessor, stages, clock, NullLogger<StartupAssessmentSource>.Instance);

    var assessing = source.AssessAsync(CancellationToken.None);
    await retrying.Task;
    clock.Advance(StartupAssessmentSource.RetryDelay(1));

    await Assert.That(await assessing).IsSameReferenceAs(_serve);
    await Assert.That(assessor.Calls).IsEqualTo(2);
    await Assert.That(stages.Calls[0]).StartsWith("Enter Preflight: the schema version record could not be read for a moment")
      .Because("the retry is progress within Preflight, said where the operator looks");
  }

  [Test]
  public async Task AnyOtherFailure_FailsStartupLoudly_AndIsNotLatchedAsync() {
    var stages = new RecordingStageReporter();
    var cause = new InvalidOperationException("permission denied for table wh_schema_versions");
    var assessor = new CountingAssessor(call => call == 1
      ? Task.FromException<StartupAssessment>(cause)
      : Task.FromResult(_serve));
    var source = new StartupAssessmentSource(assessor, stages, new FakeTimeProvider(), NullLogger<StartupAssessmentSource>.Instance);

    var failure = await Assert.That(async () => await source.AssessAsync(CancellationToken.None))
      .ThrowsExactly<StartupAssessmentException>();

    await Assert.That(failure!.InnerException).IsSameReferenceAs(cause);
    await Assert.That(failure.Message).Contains("never migrates blind");
    await Assert.That(stages.Calls).Contains(
      "Enter Preflight: the schema version record could not be read; this attempt stops here rather than migrate blind");
    await Assert.That(source.Verdict).IsNull();
    await Assert.That(await source.AssessAsync(CancellationToken.None)).IsSameReferenceAs(_serve)
      .Because("a failed read must not latch: the next attempt reads the record again");
    await Assert.That(assessor.Calls).IsEqualTo(2);
  }

  [Test]
  public async Task ACallerWhoseReadWasCanceled_DoesNotCancelAnotherCallerAsync() {
    using var firstCaller = new CancellationTokenSource();
    var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var assessor = new CountingAssessor(async (call, ct) => {
      if (call == 1) {
        firstStarted.SetResult();
        await new TaskCompletionSource().Task.WaitAsync(ct);
      }
      return _serve;
    });
    var source = _source(assessor);

    var first = source.AssessAsync(firstCaller.Token);
    await firstStarted.Task;
    var second = source.AssessAsync(CancellationToken.None);
    await firstCaller.CancelAsync();

    await Assert.That(async () => await first).Throws<OperationCanceledException>();
    await Assert.That(await second).IsSameReferenceAs(_serve)
      .Because("the read was the first caller's; a caller still waiting starts its own");
    await Assert.That(assessor.Calls).IsEqualTo(2);
  }

  [Test]
  public async Task ACanceledCaller_StopsTheRetryWaitAsync() {
    using var cancel = new CancellationTokenSource();
    var retrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var stages = new SignalingReporter("could not be read for a moment", retrying);
    var assessor = new CountingAssessor(_ => Task.FromException<StartupAssessment>(FakeDbException.WithSqlState("40P01")));
    var source = new StartupAssessmentSource(assessor, stages, new FakeTimeProvider(), NullLogger<StartupAssessmentSource>.Instance);

    var assessing = source.AssessAsync(cancel.Token);
    await retrying.Task;
    await cancel.CancelAsync();

    await Assert.That(async () => await assessing).Throws<OperationCanceledException>();
    await Assert.That(source.Verdict).IsNull();
  }

  [Test]
  [Arguments(1, 1)]
  [Arguments(2, 2)]
  [Arguments(3, 4)]
  [Arguments(6, 30)]
  [Arguments(40, 30)]
  public async Task RetryDelay_DoublesFromOneSecond_CappedAtThirtyAsync(int failures, int seconds) {
    await Assert.That(StartupAssessmentSource.RetryDelay(failures)).IsEqualTo(TimeSpan.FromSeconds(seconds));
  }

  [Test]
  public async Task ConstructedWithoutALogger_StillAssessesAsync() {
    var source = new StartupAssessmentSource(
      new CountingAssessor(_ => Task.FromException<StartupAssessment>(new InvalidOperationException("broken"))),
      NullStartupStageReporter.Instance, new FakeTimeProvider(), logger: null!);

    await Assert.That(async () => await source.AssessAsync(CancellationToken.None)).ThrowsExactly<StartupAssessmentException>();
  }

  [Test]
  public async Task TheRefusal_CarriesItsStandardShapesAsync() {
    var cause = new InvalidOperationException("x");

    await Assert.That(new StartupAssessmentException().Message).Contains("never migrates blind");
    await Assert.That(new StartupAssessmentException("custom").Message).IsEqualTo("custom");
    await Assert.That(new StartupAssessmentException("custom", cause).InnerException).IsSameReferenceAs(cause);
  }

  [Test]
  public async Task TheDefaults_RegisterOneSourceOverTheRegisteredAssessor_AndTheAssessStepReadsThroughItAsync() {
    var assessor = new CountingAssessor(_ => Task.FromResult(_serve));
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IStartupAssessor>(assessor);
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var step = provider.GetServices<IStartupStep>().OfType<AssessStartupStep>().Single();
    var report = await step.ExecuteAsync(CancellationToken.None);
    var verdict = await provider.GetRequiredService<StartupAssessmentSource>().AssessAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(verdict).IsSameReferenceAs(_serve);
    await Assert.That(assessor.Calls).IsEqualTo(1)
      .Because("the Assess step and the schema initializer read one verdict: the step reads it through the source");
  }

  private static StartupAssessmentSource _source(IStartupAssessor assessor) =>
    new(assessor, NullStartupStageReporter.Instance, new FakeTimeProvider(), NullLogger<StartupAssessmentSource>.Instance);

  private sealed class CountingAssessor(Func<int, CancellationToken, Task<StartupAssessment>> assess) : IStartupAssessor {
    private int _calls;

    public CountingAssessor(Func<int, Task<StartupAssessment>> assess) : this((call, _) => assess(call)) { }

    public int Calls => Volatile.Read(ref _calls);

    public Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) =>
      assess(Interlocked.Increment(ref _calls), cancellationToken);
  }

  /// <summary>Records like <see cref="RecordingStageReporter"/> and signals when a message containing a phrase is entered.</summary>
  private sealed class SignalingReporter(string phrase, TaskCompletionSource signal) : IStartupStageReporter {
    public List<string> Calls { get; } = [];
    public StartupStageSnapshot? Current => null;

    public ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default) {
      Calls.Add($"Enter {stage}: {message}");
      if (message.Contains(phrase, StringComparison.Ordinal)) {
        signal.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }

    public ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;

    public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }
}
