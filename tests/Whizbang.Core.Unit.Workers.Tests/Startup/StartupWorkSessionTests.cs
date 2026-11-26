// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// #2596: a start runs its heavy work only through a session. Per-schema-per-build work runs once per schema per build
/// (a finished or running claim of this build is skipped, an unfinished run releases its claim), every unit of heavy work
/// that runs holds one of the fleet's permits, and an instance past the cap waits for one, then runs without one at
/// Warning once its wait limit passes. A schema without the functions, or a start without an identity, runs every unit
/// directly, as before.
/// </summary>
/// <remarks>The wait between permit requests is the test's own delay, completed at once, with the fake clock advanced by it.</remarks>
/// <code-under-test>src/Whizbang.Core/Startup/StartupWorkSession.cs</code-under-test>
[Category("Startup")]
public class StartupWorkSessionTests {
  private static readonly Guid _instance = TrackedGuid.New();

  [Test]
  public async Task AnUndeclaredName_IsRefused_AndItsWorkNeverRunsAsync() {
    var ledger = new FakeStartupWorkLedger();
    await using var session = _session(ledger);
    var ran = false;

    await Assert.That(async () => await session.RunAsync("nightly-report", "", _ => {
      ran = true;
      return Task.FromResult(true);
    }, CancellationToken.None)).Throws<InvalidOperationException>();

    await Assert.That(ran).IsFalse();
    await Assert.That(ledger.Calls).IsEmpty();
  }

  [Test]
  public async Task WithNoLedger_TheWorkRunsDirectly_WithNoPermitAsync() {
    await using var session = new StartupWorkSession(
      null, _instance, new StartupWorkOptions(), passApplied: false, null, new FakeTimeProvider(), null);

    var outcome = await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x", _ => Task.FromResult(true), CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(StartupWorkOutcome.Ran);
    await Assert.That(session.HoldsPermit).IsFalse();
  }

  [Test]
  public async Task WithNoInstanceId_TheWorkRunsDirectly_AndTheLedgerIsNotAskedAsync() {
    var ledger = new FakeStartupWorkLedger();
    await using var session = new StartupWorkSession(
      ledger, null, new StartupWorkOptions(), passApplied: false, null, new FakeTimeProvider(), null);

    var outcome = await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x", _ => Task.FromResult(true), CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(StartupWorkOutcome.Ran);
    await Assert.That(ledger.Calls).IsEmpty();
  }

  [Test]
  public async Task OnASchemaWithoutTheFunctions_EveryUnitRunsDirectly_AndAvailabilityIsAskedOnceAsync() {
    // An older release migrated this schema: the rolling deploy keeps working, uncapped, as before the cap existed.
    var ledger = new FakeStartupWorkLedger { Available = false };
    await using var session = _session(ledger);
    var runs = 0;

    await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x", _ => Task.FromResult(++runs > 0), CancellationToken.None);
    await session.RunAsync(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE, "", _ => Task.FromResult(++runs > 0), CancellationToken.None);

    await Assert.That(runs).IsEqualTo(2);
    await Assert.That(ledger.Calls).IsEquivalentTo(["available"]);
  }

  [Test]
  public async Task WhenAvailabilityCannotBeRead_EveryUnitRunsDirectly_AtWarningAsync() {
    var ledger = new FakeStartupWorkLedger { AvailableThrows = new InvalidOperationException("no route") };
    var logger = new FakeLogger<StartupWorkSession>();
    await using var session = _session(ledger, logger: logger);

    var outcome = await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x", _ => Task.FromResult(true), CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(StartupWorkOutcome.Ran);
    await Assert.That(ledger.Calls).IsEquivalentTo(["available"]);
    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level == LogLevel.Warning)).IsTrue();
  }

  [Test]
  public async Task SharedWorkThisBuildFinished_IsSkipped_AndTakesNoPermitAsync() {
    var ledger = new FakeStartupWorkLedger { Claim = StartupWorkClaim.Done };
    await using var session = _session(ledger);
    var ran = false;

    var outcome = await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x", _ => { ran = true; return Task.FromResult(true); }, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(StartupWorkOutcome.SkippedDone);
    await Assert.That(ran).IsFalse();
    await Assert.That(session.HoldsPermit).IsFalse();
    await Assert.That(ledger.Calls).IsEquivalentTo(["available", "claim"]);
  }

  [Test]
  public async Task SharedWorkALivePeerOfThisBuildRuns_IsSkippedAsync() {
    var ledger = new FakeStartupWorkLedger { Claim = StartupWorkClaim.RunningElsewhere };
    await using var session = _session(ledger);
    var ran = false;

    var outcome = await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_STORAGE_SETTINGS, "x", _ => { ran = true; return Task.FromResult(true); }, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(StartupWorkOutcome.SkippedRunningElsewhere);
    await Assert.That(ran).IsFalse();
    await Assert.That(session.HoldsPermit).IsFalse();
  }

  [Test]
  public async Task ClaimedSharedWork_RunsUnderAPermit_AndIsRecordedDoneForThisBuildsFingerprintAsync() {
    var ledger = new FakeStartupWorkLedger();
    await using var session = _session(ledger);
    var heldWhileRunning = false;

    var outcome = await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "payloads", _ => {
      heldWhileRunning = session.HoldsPermit;
      return Task.FromResult(true);
    }, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(StartupWorkOutcome.Ran);
    await Assert.That(heldWhileRunning).IsTrue();
    await Assert.That(ledger.Calls).IsEquivalentTo(["available", "claim", "acquire", "complete:True"]);
    var fingerprint = StartupWorkSession.Fingerprint(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "payloads");
    await Assert.That(ledger.ClaimedFingerprint).IsEqualTo(fingerprint);
    await Assert.That(ledger.CompletedFingerprint).IsEqualTo(fingerprint);
    await Assert.That(ledger.Forced).IsFalse();
    await Assert.That(ledger.Cap).IsEqualTo(StartupWorkOptions.DEFAULT_MAX_CONCURRENT_HEAVY_STARTS);
  }

  [Test]
  public async Task ARunThatDidNotFinish_ReleasesItsClaim_SoTheNextStartRunsItAgainAsync() {
    var ledger = new FakeStartupWorkLedger();
    await using var session = _session(ledger);

    await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x", _ => Task.FromResult(false), CancellationToken.None);

    await Assert.That(ledger.Calls[^1]).IsEqualTo("complete:False");
  }

  [Test]
  public async Task ARunThatThrows_ReleasesItsClaim_AndTheFailureReachesTheCallerAsync() {
    var ledger = new FakeStartupWorkLedger();
    await using var session = _session(ledger);

    await Assert.That(async () => await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x",
      _ => throw new InvalidOperationException("boom"), CancellationToken.None)).Throws<InvalidOperationException>();

    await Assert.That(ledger.Calls[^1]).IsEqualTo("complete:False");
  }

  [Test]
  public async Task ACanceledRun_ReleasesItsClaim_OnAnUncanceledTokenAsync() {
    var ledger = new FakeStartupWorkLedger();
    await using var session = _session(ledger);
    using var cts = new CancellationTokenSource();

    await Assert.That(async () => await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x", _ => {
      cts.Cancel();
      throw new OperationCanceledException(cts.Token);
    }, cts.Token)).Throws<OperationCanceledException>();

    await Assert.That(ledger.Calls[^1]).IsEqualTo("complete:False");
    await Assert.That(ledger.CompleteToken.CanBeCanceled).IsFalse();
  }

  [Test]
  public async Task AStartWhosePassAppliedSomething_ClaimsSharedWorkEvenWhenItIsDoneAsync() {
    var ledger = new FakeStartupWorkLedger();
    await using var session = _session(ledger, passApplied: true);

    await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x", _ => Task.FromResult(true), CancellationToken.None);

    await Assert.That(ledger.Forced).IsTrue();
  }

  [Test]
  public async Task AClaimThatCannotBeMade_RunsTheWorkDirectly_AtWarningAsync() {
    var ledger = new FakeStartupWorkLedger { ClaimThrows = new InvalidOperationException("timeout") };
    var logger = new FakeLogger<StartupWorkSession>();
    await using var session = _session(ledger, logger: logger);
    var ran = false;

    var outcome = await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x", _ => { ran = true; return Task.FromResult(true); }, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(StartupWorkOutcome.Ran);
    await Assert.That(ran).IsTrue();
    await Assert.That(ledger.Calls.Any(c => c.StartsWith("complete", StringComparison.Ordinal))).IsFalse()
      .Because("nothing was claimed, so nothing is ended");
    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level == LogLevel.Warning)).IsTrue();
  }

  [Test]
  public async Task AClaimThatCannotBeEnded_IsSaidAtWarning_AndTheOutcomeStandsAsync() {
    var ledger = new FakeStartupWorkLedger { CompleteThrows = new InvalidOperationException("gone") };
    var logger = new FakeLogger<StartupWorkSession>();
    await using var session = _session(ledger, logger: logger);

    var outcome = await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x", _ => Task.FromResult(true), CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(StartupWorkOutcome.Ran);
    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level == LogLevel.Warning)).IsTrue();
  }

  [Test]
  public async Task PerInstanceWork_IsNeverClaimed_ButRunsUnderAPermitAsync() {
    var ledger = new FakeStartupWorkLedger();
    await using var session = _session(ledger);
    var held = false;

    var outcome = await session.RunAsync(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE, "", _ => { held = session.HoldsPermit; return Task.FromResult(true); }, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(StartupWorkOutcome.Ran);
    await Assert.That(held).IsTrue();
    await Assert.That(ledger.Calls).IsEquivalentTo(["available", "acquire"]);
  }

  [Test]
  public async Task OnePermit_CoversEveryUnitOfTheStart_AndIsGivenBackOnDisposeAsync() {
    var ledger = new FakeStartupWorkLedger();
    var session = _session(ledger);

    await session.RunAsync(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE, "", _ => Task.FromResult(true), CancellationToken.None);
    await session.RunAsync(FrameworkStartupWork.ANALYZE_NEW_INDEXES, "", _ => Task.FromResult(true), CancellationToken.None);
    await session.DisposeAsync();
    await session.DisposeAsync();

    await Assert.That(ledger.Calls.Count(c => c == "acquire")).IsEqualTo(1);
    await Assert.That(ledger.Calls.Count(c => c == "release")).IsEqualTo(1).Because("a second dispose has nothing to give back");
    await Assert.That(session.HoldsPermit).IsFalse();
  }

  [Test]
  public async Task ASessionThatTookNoPermit_ReleasesNothingAsync() {
    var ledger = new FakeStartupWorkLedger { Claim = StartupWorkClaim.Done };
    var session = _session(ledger);

    await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x", _ => Task.FromResult(true), CancellationToken.None);
    await session.DisposeAsync();

    await Assert.That(ledger.Calls.Contains("release")).IsFalse();
  }

  [Test]
  public async Task APermitThatCannotBeGivenBack_IsSaidAtWarningAsync() {
    var ledger = new FakeStartupWorkLedger { ReleaseThrows = new InvalidOperationException("gone") };
    var logger = new FakeLogger<StartupWorkSession>();
    var session = _session(ledger, logger: logger);

    await session.RunAsync(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE, "", _ => Task.FromResult(true), CancellationToken.None);
    await session.DisposeAsync();

    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level == LogLevel.Warning)).IsTrue();
    await Assert.That(session.HoldsPermit).IsFalse();
  }

  [Test]
  public async Task PastTheCap_TheStartWaitsForAPermit_SaysSoOnce_AndRunsOnceOneIsFreeAsync() {
    var ledger = new FakeStartupWorkLedger();
    ledger.Permits.Enqueue(new StartupPermitAnswer(false, 2));
    ledger.Permits.Enqueue(new StartupPermitAnswer(false, 2));
    ledger.Permits.Enqueue(new StartupPermitAnswer(true, 2));
    var stages = new RecordingStageReporter();
    var waits = new List<TimeSpan>();
    var options = new StartupWorkOptions { PermitPollInterval = TimeSpan.FromSeconds(3) };
    var time = new FakeTimeProvider();
    await using var session = new StartupWorkSession(ledger, _instance, options, false, stages, time, null) {
      Delay = (wait, _) => {
        waits.Add(wait);
        time.Advance(wait);
        return Task.CompletedTask;
      },
    };

    var outcome = await session.RunAsync(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE, "", _ => Task.FromResult(true), CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(StartupWorkOutcome.Ran);
    await Assert.That(waits).IsEquivalentTo([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)]);
    await Assert.That(stages.Calls).IsEquivalentTo([
      "Enter Converge: waiting for a heavy-start permit (2 of 2 held across the fleet)",
    ]);
    await Assert.That(session.HoldsPermit).IsTrue();
  }

  [Test]
  public async Task AWaitPastItsLimit_RunsTheWorkWithoutAPermit_AtWarningAsync() {
    var ledger = new FakeStartupWorkLedger();
    for (var i = 0; i < 10; i++) {
      ledger.Permits.Enqueue(new StartupPermitAnswer(false, 1));
    }
    var logger = new FakeLogger<StartupWorkSession>();
    var options = new StartupWorkOptions {
      MaxConcurrentHeavyStarts = 1,
      PermitPollInterval = TimeSpan.FromSeconds(1),
      PermitWaitLimit = TimeSpan.FromSeconds(3),
    };
    var time = new FakeTimeProvider();
    var session = new StartupWorkSession(ledger, _instance, options, false, null, time, logger) {
      Delay = (wait, _) => {
        time.Advance(wait);
        return Task.CompletedTask;
      },
    };
    var ran = false;

    await session.RunAsync(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE, "", _ => { ran = true; return Task.FromResult(true); }, CancellationToken.None);
    await session.RunAsync(FrameworkStartupWork.ANALYZE_NEW_INDEXES, "", _ => Task.FromResult(true), CancellationToken.None);
    await session.DisposeAsync();

    await Assert.That(ran).IsTrue();
    await Assert.That(session.HoldsPermit).IsFalse();
    await Assert.That(ledger.Calls.Count(c => c == "acquire")).IsEqualTo(4)
      .Because("asked at 0, 1, 2 and 3 seconds; at the limit the start goes on, and asks no more for the rest of it");
    await Assert.That(ledger.Calls.Contains("release")).IsFalse();
    await Assert.That(logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Warning)).IsEqualTo(1);
  }

  [Test]
  public async Task APermitThatCannotBeAskedFor_RunsTheWorkWithoutOne_AtWarningAsync() {
    var ledger = new FakeStartupWorkLedger { AcquireThrows = new InvalidOperationException("timeout") };
    var logger = new FakeLogger<StartupWorkSession>();
    await using var session = _session(ledger, logger: logger);
    var ran = false;

    await session.RunAsync(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE, "", _ => { ran = true; return Task.FromResult(true); }, CancellationToken.None);

    await Assert.That(ran).IsTrue();
    await Assert.That(session.HoldsPermit).IsFalse();
    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level == LogLevel.Warning)).IsTrue();
  }

  [Test]
  public async Task TheDefaultWait_IsADelayOnTheSessionsClockAsync() {
    var ledger = new FakeStartupWorkLedger();
    ledger.Permits.Enqueue(new StartupPermitAnswer(false, 2));
    var time = new FakeTimeProvider();
    await using var session = new StartupWorkSession(ledger, _instance, new StartupWorkOptions(), false, null, time, null);
    var acquiredTwice = ledger.AcquireCalled(2);

    var run = session.RunAsync(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE, "", _ => Task.FromResult(true), CancellationToken.None);
    await ledger.AcquireCalled(1);
    await Assert.That(acquiredTwice.IsCompleted).IsFalse();
    // The delay is a timer on the fake clock: it fires only when the clock passes the poll interval.
    while (!acquiredTwice.IsCompleted) {
      time.Advance(TimeSpan.FromSeconds(1));
      await Task.Yield();
    }

    await Assert.That(await run).IsEqualTo(StartupWorkOutcome.Ran);
  }

  [Test]
  public async Task TheFingerprint_FollowsTheNameAndTheInputs_AndIsStableAsync() {
    var a = StartupWorkSession.Fingerprint(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x");

    await Assert.That(StartupWorkSession.Fingerprint(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x")).IsEqualTo(a);
    await Assert.That(StartupWorkSession.Fingerprint(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "y")).IsNotEqualTo(a);
    await Assert.That(StartupWorkSession.Fingerprint(FrameworkStartupWork.PERSPECTIVE_STORAGE_SETTINGS, "x")).IsNotEqualTo(a);
    await Assert.That(a.Length).IsEqualTo(64);
    await Assert.That(a).IsEqualTo(a.ToLowerInvariant());
  }

  [Test]
  public async Task TheSession_RefusesMissingArgumentsAsync() {
    await Assert.That(() => new StartupWorkSession(null, null, null!, false, null, new FakeTimeProvider(), null))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new StartupWorkSession(null, null, new StartupWorkOptions(), false, null, null!, null))
      .Throws<ArgumentNullException>();
    await using var session = _session(new FakeStartupWorkLedger());
    await Assert.That(async () => await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x", null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, null!, _ => Task.FromResult(true), CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  private static StartupWorkSession _session(
      FakeStartupWorkLedger ledger, bool passApplied = false, ILogger? logger = null) =>
    new(ledger, _instance, new StartupWorkOptions(), passApplied, null, new FakeTimeProvider(), logger);
}

/// <summary>A ledger that answers as it is told and records each call, in order.</summary>
internal sealed class FakeStartupWorkLedger : IStartupWorkLedger {
  private readonly List<TaskCompletionSource> _acquireSignals = [];
  private int _acquires;

  public List<string> Calls { get; } = [];
  public bool Available { get; init; } = true;
  public Exception? AvailableThrows { get; init; }
  public StartupWorkClaim Claim { get; init; } = StartupWorkClaim.Claimed;
  public Exception? ClaimThrows { get; init; }
  public Exception? CompleteThrows { get; init; }
  public Exception? AcquireThrows { get; init; }
  public Exception? ReleaseThrows { get; init; }

  /// <summary>The permit answers, in order; granted once they run out.</summary>
  public Queue<StartupPermitAnswer> Permits { get; } = new();

  public string? ClaimedFingerprint { get; private set; }
  public string? CompletedFingerprint { get; private set; }
  public bool Forced { get; private set; }
  public int Cap { get; private set; }
  public CancellationToken CompleteToken { get; private set; }

  /// <summary>Completes once the permit has been asked for <paramref name="count"/> times.</summary>
  public Task AcquireCalled(int count) {
    lock (_acquireSignals) {
      while (_acquireSignals.Count < count) {
        _acquireSignals.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      }
      return _acquireSignals[count - 1].Task;
    }
  }

  public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) {
    Calls.Add("available");
    return AvailableThrows is { } ex ? Task.FromException<bool>(ex) : Task.FromResult(Available);
  }

  public Task<StartupWorkClaim> ClaimAsync(string work, string fingerprint, Guid instanceId, bool force, CancellationToken cancellationToken) {
    Calls.Add("claim");
    ClaimedFingerprint = fingerprint;
    Forced = force;
    return ClaimThrows is { } ex ? Task.FromException<StartupWorkClaim>(ex) : Task.FromResult(Claim);
  }

  public Task CompleteAsync(string work, string fingerprint, Guid instanceId, bool succeeded, CancellationToken cancellationToken) {
    Calls.Add($"complete:{succeeded}");
    CompletedFingerprint = fingerprint;
    CompleteToken = cancellationToken;
    return CompleteThrows is { } ex ? Task.FromException(ex) : Task.CompletedTask;
  }

  public Task<StartupPermitAnswer> TryAcquirePermitAsync(Guid instanceId, int cap, CancellationToken cancellationToken) {
    Calls.Add("acquire");
    Cap = cap;
    var count = ++_acquires;
    _ = AcquireCalled(count);
    lock (_acquireSignals) {
      _acquireSignals[count - 1].TrySetResult();
    }
    if (AcquireThrows is { } ex) {
      return Task.FromException<StartupPermitAnswer>(ex);
    }
    return Task.FromResult(Permits.Count > 0 ? Permits.Dequeue() : new StartupPermitAnswer(true, 1));
  }

  public Task ReleasePermitAsync(Guid instanceId, CancellationToken cancellationToken) {
    Calls.Add("release");
    return ReleaseThrows is { } ex ? Task.FromException(ex) : Task.CompletedTask;
  }
}
