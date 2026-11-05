// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Signals;
using Whizbang.Core.Startup;
using Whizbang.Core.Versioning;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// #2022: the cluster-wide stand-down when a newer version comes online, from the running instance's side. The standby
/// watcher keeps the Assess verdict at runtime: a newer version online (its durable signal), an instance that died or
/// left, and the slow cadence each make it ask the one verdict again. A StandDown verdict stands the instance down
/// through the one run-control path, saying why on its row; a verdict that no longer says StandDown resumes it. A newer
/// instance that comes online announces itself once, and only when an older instance is online to hear it.
/// </summary>
/// <remarks>
/// Deterministic by construction: the signals are delivered by hand through a recording bus, and each decision is one
/// call to <see cref="StandbyWatcher.TickForTestsAsync"/>; nothing waits on a clock.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Startup/StandbyWatcher.cs</code-under-test>
[Category("Core")]
[Category("Startup")]
public class StandbyWatcherUpgradeStandDownTests {
  private static readonly Guid _us = Guid.Parse("0199cccc-0000-0000-0000-000000000001");
  private static readonly Guid _older = Guid.Parse("0199cccc-0000-0000-0000-000000000002");
  private static readonly Guid _newer = Guid.Parse("0199cccc-0000-0000-0000-000000000003");
  private static readonly DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
  private const string NEWER_ONLINE = "instance 0199cccc-0000-0000-0000-000000000003 is online with application version 2.0.0, newer than this instance's 1.0.0";

  private sealed class Instance : IServiceInstanceProvider {
    public Guid InstanceId => _us;
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class Library(string version) : ILibraryVersionProvider {
    public string LibraryVersion => version;
  }

  private sealed class Lifecycle : IWhizbangLifecycleState {
    public List<LifecyclePhase> Advanced { get; } = [];
    public LifecyclePhase Phase { get; set; } = LifecyclePhase.Running;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Advanced.Add(phase);
      Phase = phase;
      return ValueTask.CompletedTask;
    }
    public ValueTask FaultAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }

  private sealed class Host : IHostApplicationLifetime {
    public int StopCalls { get; private set; }
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => StopCalls++;
  }

  /// <summary>Answers each assessment with the next scripted verdict, repeating the last.</summary>
  private sealed class ScriptedAssessor(params StartupVerdict[] verdicts) : IStartupAssessor {
    private int _next;
    public int Calls { get; private set; }
    public Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) {
      Calls++;
      var verdict = verdicts[Math.Min(_next++, verdicts.Length - 1)];
      return Task.FromResult(new StartupAssessment(verdict,
        verdict == StartupVerdict.StandDown ? NEWER_ONLINE : "nothing newer recorded or online"));
    }
  }

  private sealed class NoRequests : IWorkCoordinator {
    public Task<StandbyRequest?> GetStandbyRequestAsync(CancellationToken cancellationToken = default) => Task.FromResult<StandbyRequest?>(null);
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  /// <summary>A bus that records publishes and hands each subscribed signal over when a test delivers it.</summary>
  private sealed class RecordingBus : ISignalBus {
    private readonly List<(Type Type, Delegate Handler)> _handlers = [];
    public List<string> Published { get; } = [];
    public bool FailNextPublish { get; set; }
    public int ActiveSubscriptions => _handlers.Count;

    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
        where TSignal : ISignal {
      if (FailNextPublish) {
        FailNextPublish = false;
        throw new InvalidOperationException("the bus could not reach the database");
      }
      Published.Add(typeof(TSignal).Name);
      return ValueTask.CompletedTask;
    }

    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal {
      var entry = (typeof(TSignal), (Delegate)handler);
      _handlers.Add(entry);
      return new Subscription(() => _handlers.Remove(entry));
    }

    public async Task DeliverAsync<TSignal>() where TSignal : ISignal {
      foreach (var (_, handler) in _handlers.Where(h => h.Type == typeof(TSignal)).ToList()) {
        await ((Func<TSignal, ValueTask>)handler)(default!);
      }
    }

    private sealed class Subscription(Action dispose) : ISignalSubscription {
      public void Dispose() => dispose();
    }
  }

  private sealed class OnlineVersions(params VersionSighting[] online) : IOnlineVersionReader {
    public IReadOnlyList<VersionSighting> Online { get; set; } = online;
    public int Reads { get; private set; }
    public bool FailNextRead { get; set; }
    public Task<IReadOnlyList<VersionSighting>> ReadOnlineAsync(CancellationToken cancellationToken) {
      Reads++;
      if (FailNextRead) {
        FailNextRead = false;
        throw new InvalidOperationException("the fleet could not be read");
      }
      return Task.FromResult(Online);
    }
  }

  private sealed class Stages(StartupStage? current = StartupStage.Running) : IStartupStageReporter {
    public List<(StartupStage Stage, string Message)> Entered { get; } = [];
    public StartupStageSnapshot? Current { get; private set; } =
      current is { } stage ? new StartupStageSnapshot(stage, "running", false, _now) : null;
    public ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default) {
      Entered.Add((stage, message));
      Current = new StartupStageSnapshot(stage, message, false, _now);
      return ValueTask.CompletedTask;
    }
    public ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed record Harness(
      StandbyWatcher Watcher, WhizbangRunController RunControl, Lifecycle Lifecycle, RecordingBus Bus, OnlineVersions Online,
      Stages Stages, FakeLogger<StandbyWatcher> Logger, ScriptedAssessor Assessor, ServiceProvider Provider) : IDisposable {
    public void Dispose() {
      Watcher.Dispose();
      RunControl.Dispose();
      Provider.Dispose();
    }

    public int StandDownsLogged => _logged(3);
    public int ResumesLogged => _logged(6);
    public int AnnouncementsLogged => _logged(7);
    private int _logged(int eventId) => Logger.Collector.GetSnapshot().Count(r => r.Id.Id == eventId);
  }

  private static Harness _harness(
      ScriptedAssessor? assessor = null, OnlineVersions? online = null, Stages? stages = null,
      TimeSpan? obsolescenceInterval = null, bool wired = true, bool withLifecycle = true, bool withApplicationVersion = true) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => new NoRequests());
    var provider = services.BuildServiceProvider();
    var lifecycle = new Lifecycle();
    var runControl = new WhizbangRunController(new WhizbangRunPermit(), lifecycle, [], [], new WhizbangLifecycleOptions(), null,
      Microsoft.Extensions.Logging.Abstractions.NullLogger<WhizbangRunController>.Instance);
    var bus = new RecordingBus();
    online ??= new OnlineVersions();
    stages ??= new Stages();
    assessor ??= new ScriptedAssessor(StartupVerdict.Current);
    var logger = new FakeLogger<StandbyWatcher>();
    var watcher = new StandbyWatcher(
      provider.GetRequiredService<IServiceScopeFactory>(), runControl, new Host(), new Instance(), new Library("1.0.0"),
      assessor, logger, new FakeTimeProvider(_now), pipelineRunner: null,
      options: new StandbyWatcherOptions { ObsolescenceInterval = obsolescenceInterval ?? TimeSpan.FromHours(1) }) {
      Signals = wired ? bus : null,
      OnlineVersions = wired ? online : null,
      ApplicationVersion = withApplicationVersion
        ? new ApplicationVersionProvider(Whizbang.Core.Versioning.ApplicationVersion.Resolve(null, null, "1.0.0"))
        : null,
      Lifecycle = wired && withLifecycle ? lifecycle : null,
      Stages = stages,
    };
    return new Harness(watcher, runControl, lifecycle, bus, online, stages, logger, assessor, provider);
  }

  private static async Task _tickAsync(Harness h) => await h.Watcher.TickForTestsAsync(CancellationToken.None);

  // ============================================================
  // Standing down when a newer version comes online
  // ============================================================

  [Test]
  public async Task ANewerVersionOnlineSignal_IsAskedAboutOnTheNextTick_InsideTheSlowCadenceAsync() {
    using var h = _harness(new ScriptedAssessor(StartupVerdict.Current, StartupVerdict.StandDown));
    await _tickAsync(h);
    await _tickAsync(h);
    await Assert.That(h.Assessor.Calls).IsEqualTo(1).Because("inside the slow cadence nothing prompts a second read");

    await h.Bus.DeliverAsync<NewerVersionOnlineSignal>();
    await _tickAsync(h);

    await Assert.That(h.Assessor.Calls).IsEqualTo(2).Because("the signal is a doorbell: the verdict is read again at once");
    await Assert.That(h.RunControl.HaltedFor).IsEqualTo(RunHaltReason.StandDown)
      .Because("the same path as a pause and a stop: the permit closes, held work and roles are released");
    await Assert.That(h.Lifecycle.Phase).IsEqualTo(LifecyclePhase.StandingBy);
  }

  [Test]
  public async Task StandingDown_SaysWhyOnTheRow_BeforeThePhase_AndIsLoggedOnceAsync() {
    using var h = _harness(new ScriptedAssessor(StartupVerdict.StandDown), obsolescenceInterval: TimeSpan.Zero);

    await _tickAsync(h);
    await h.Bus.DeliverAsync<NewerVersionOnlineSignal>();
    await _tickAsync(h);
    await _tickAsync(h);

    await Assert.That(h.Stages.Entered).IsEquivalentTo([(StartupStage.Running, "standing down: " + NEWER_ONLINE)])
      .Because("the row's stage message names the newer version and the instance it runs on");
    await Assert.That(h.Lifecycle.Advanced.Count(p => p == LifecyclePhase.StandingBy)).IsEqualTo(1);
    await Assert.That(h.StandDownsLogged).IsEqualTo(1).Because("logged once, at the transition, with a stable event id");
    var line = h.Logger.Collector.GetSnapshot().Single(r => r.Id.Id == 3);
    await Assert.That(line.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(line.Message).Contains(NEWER_ONLINE);
  }

  [Test]
  public async Task TwoNewerInstancesComingOnlineAtOnce_StandTheInstanceDownOnceAsync() {
    using var h = _harness(new ScriptedAssessor(StartupVerdict.Current, StartupVerdict.StandDown));
    await _tickAsync(h);

    await h.Bus.DeliverAsync<NewerVersionOnlineSignal>();
    await h.Bus.DeliverAsync<NewerVersionOnlineSignal>();
    await _tickAsync(h);
    await h.Bus.DeliverAsync<NewerVersionOnlineSignal>();
    await _tickAsync(h);

    await Assert.That(h.Lifecycle.Advanced.Count(p => p == LifecyclePhase.StandingBy)).IsEqualTo(1);
    await Assert.That(h.StandDownsLogged).IsEqualTo(1);
  }

  [Test]
  public async Task WithoutAStageRecorded_TheStandDownStillTakesThePathAsync() {
    using var h = _harness(new ScriptedAssessor(StartupVerdict.StandDown), stages: new Stages(current: null));

    await _tickAsync(h);

    await Assert.That(h.Stages.Entered).IsEmpty().Because("with no stage entered there is no stage to say it in");
    await Assert.That(h.RunControl.HaltedFor).IsEqualTo(RunHaltReason.StandDown);
  }

  [Test]
  public async Task WithoutAStageReporter_TheStandDownAndTheResumeStillTakeThePathAsync() {
    using var h = _harness(new ScriptedAssessor(StartupVerdict.StandDown, StartupVerdict.Current));
    var watcher = new StandbyWatcher(
      h.Provider.GetRequiredService<IServiceScopeFactory>(), h.RunControl, new Host(), new Instance(), new Library("1.0.0"),
      h.Assessor, h.Logger, new FakeTimeProvider(_now), options: new StandbyWatcherOptions { ObsolescenceInterval = TimeSpan.Zero });

    await watcher.TickForTestsAsync(CancellationToken.None);
    await Assert.That(h.RunControl.HaltedFor).IsEqualTo(RunHaltReason.StandDown);
    await watcher.TickForTestsAsync(CancellationToken.None);
    await Assert.That(h.RunControl.HaltedFor).IsNull();
  }

  // ============================================================
  // The newer instance leaves: the decision for the owner (#2022)
  // ============================================================

  [Test]
  public async Task TheNewerInstanceDies_TheStoodDownInstanceResumes_BecauseAFreshStartWouldServeAsync() {
    // The resume decision: a stood-down instance resumes when the verdict no longer says StandDown, the same verdict a
    // fresh start of this binary would get. A newer instance that died leaves nothing newer online; if it migrated the
    // schema, the ledger still records its newer library version and the verdict keeps the instance down (next test).
    using var h = _harness(new ScriptedAssessor(StartupVerdict.Current, StartupVerdict.StandDown, StartupVerdict.Current));
    await _tickAsync(h);
    await h.Bus.DeliverAsync<NewerVersionOnlineSignal>();
    await _tickAsync(h);

    await h.Bus.DeliverAsync<InstanceDiedSignal>();
    await _tickAsync(h);

    await Assert.That(h.RunControl.HaltedFor).IsNull().Because("the permit reopens; the roles are voted for normally again");
    await Assert.That(h.Lifecycle.Advanced).IsEquivalentTo(
      [LifecyclePhase.Pausing, LifecyclePhase.StandingBy, LifecyclePhase.Resuming, LifecyclePhase.Running]);
    await Assert.That(h.Stages.Entered[^1]).IsEqualTo((StartupStage.Running, "resumed: nothing newer recorded or online"));
    await Assert.That(h.ResumesLogged).IsEqualTo(1);
    await Assert.That(h.Logger.Collector.GetSnapshot().Single(r => r.Id.Id == 6).Level).IsEqualTo(LogLevel.Information);
  }

  [Test]
  public async Task AGracefulLeave_AlsoPromptsTheQuestionAsync() {
    using var h = _harness(new ScriptedAssessor(StartupVerdict.StandDown, StartupVerdict.Current));
    await _tickAsync(h);

    await h.Bus.DeliverAsync<InstanceLeavingSignal>();
    await _tickAsync(h);

    await Assert.That(h.RunControl.HaltedFor).IsNull();
  }

  [Test]
  public async Task WhileTheVerdictStillSaysStandDown_ADeathResumesNothingAsync() {
    // A newer instance that migrated and then died: the ledger records its newer library version, so the schema is newer
    // than this binary and the instance stays down until the rollout replaces it.
    using var h = _harness(new ScriptedAssessor(StartupVerdict.StandDown));
    await _tickAsync(h);

    await h.Bus.DeliverAsync<InstanceDiedSignal>();
    await _tickAsync(h);

    await Assert.That(h.RunControl.HaltedFor).IsEqualTo(RunHaltReason.StandDown);
    await Assert.That(h.Lifecycle.Advanced).DoesNotContain(LifecyclePhase.Resuming);
    await Assert.That(h.ResumesLogged).IsEqualTo(0);
  }

  [Test]
  public async Task OnceStoodDown_ATickInsideTheSlowCadence_WithNoSignal_ReadsNothingAsync() {
    using var h = _harness(new ScriptedAssessor(StartupVerdict.StandDown));
    await _tickAsync(h);

    await _tickAsync(h);
    await _tickAsync(h);

    await Assert.That(h.Assessor.Calls).IsEqualTo(1).Because("a stood-down instance asks again on a signal or the slow cadence");
  }

  [Test]
  public async Task AFlappingDeploy_TakesEachTransitionOnce_AndEndsWhereTheLastVerdictSaysAsync() {
    using var h = _harness(new ScriptedAssessor(
      StartupVerdict.StandDown, StartupVerdict.StandDown, StartupVerdict.Current, StartupVerdict.Current,
      StartupVerdict.StandDown, StartupVerdict.Serve));

    for (var i = 0; i < 6; i++) {
      await h.Bus.DeliverAsync<NewerVersionOnlineSignal>();
      await _tickAsync(h);
    }

    await Assert.That(h.Lifecycle.Advanced.Count(p => p == LifecyclePhase.StandingBy)).IsEqualTo(2);
    await Assert.That(h.Lifecycle.Advanced.Count(p => p == LifecyclePhase.Resuming)).IsEqualTo(2);
    await Assert.That(h.StandDownsLogged).IsEqualTo(2);
    await Assert.That(h.ResumesLogged).IsEqualTo(2);
    await Assert.That(h.RunControl.HaltedFor).IsNull();
  }

  [Test]
  public async Task AnOperatorsPause_OutlivesTheResumeAsync() {
    using var h = _harness(new ScriptedAssessor(StartupVerdict.StandDown, StartupVerdict.Current));
    await _tickAsync(h);
    await h.RunControl.HaltAsync(RunHaltReason.Pause, CancellationToken.None);

    await h.Bus.DeliverAsync<InstanceDiedSignal>();
    await _tickAsync(h);

    await Assert.That(h.RunControl.HaltedFor).IsEqualTo(RunHaltReason.Pause)
      .Because("the resume lifts only the stand-down; the operator's pause still holds the permit closed");
  }

  // ============================================================
  // Announcing a newer version
  // ============================================================

  private static OnlineVersions _fleet(params VersionSighting[] online) => new(online);
  private static VersionSighting _olderPeer() => new("1.0.0", "0.9.0", _older);

  [Test]
  public async Task OnceServing_ANewerInstance_AnnouncesItselfOnce_WhenAnOlderOneIsOnlineAsync() {
    using var h = _harness(online: _fleet(_olderPeer(), new VersionSighting("1.0.0", "1.0.0", _us)));

    await _tickAsync(h);
    await _tickAsync(h);

    await Assert.That(h.Bus.Published).IsEquivalentTo([nameof(NewerVersionOnlineSignal)])
      .Because("a decision, published once: the durable log replays it to an older instance that missed it");
    await Assert.That(h.Online.Reads).IsEqualTo(1);
    await Assert.That(h.AnnouncementsLogged).IsEqualTo(1);
  }

  [Test]
  public async Task WithNoOlderInstanceOnline_NothingIsAnnouncedAsync() {
    using var h = _harness(online: _fleet(
      new VersionSighting("1.0.0", "1.0.0", _older),      // equal
      new VersionSighting("2.0.0", "1.0.0", _newer)));    // newer: never made to stand down by us

    await _tickAsync(h);
    await _tickAsync(h);

    await Assert.That(h.Bus.Published).IsEmpty().Because("one-way: equal versions do nothing, and an older one never stands a newer one down");
    await Assert.That(h.Online.Reads).IsEqualTo(1).Because("the instance came online once; it asks once");
  }

  [Test]
  [Arguments(LifecyclePhase.Migrating)]
  [Arguments(LifecyclePhase.Paused)]
  public async Task BeforeTheInstanceServes_NothingIsReadOrAnnouncedAsync(LifecyclePhase phase) {
    using var h = _harness(online: _fleet(_olderPeer()));
    h.Lifecycle.Phase = phase;

    await _tickAsync(h);
    await Assert.That(h.Online.Reads).IsEqualTo(0)
      .Because("an instance is online once it serves: an older one standing down for it before then is an outage");

    h.Lifecycle.Phase = LifecyclePhase.AcceptingCommands;
    await _tickAsync(h);
    await Assert.That(h.Bus.Published).IsEquivalentTo([nameof(NewerVersionOnlineSignal)]);
  }

  [Test]
  public async Task AFailedReadOrPublish_IsTriedAgainOnTheNextTickAsync() {
    using var h = _harness(online: _fleet(_olderPeer()));
    h.Online.FailNextRead = true;

    await Assert.That(async () => await _tickAsync(h)).Throws<InvalidOperationException>()
      .Because("the watcher's loop logs a failed tick and tries again");
    h.Bus.FailNextPublish = true;
    await Assert.That(async () => await _tickAsync(h)).Throws<InvalidOperationException>();
    await _tickAsync(h);

    await Assert.That(h.Bus.Published).IsEquivalentTo([nameof(NewerVersionOnlineSignal)]);
  }

  [Test]
  public async Task AnInstanceThatResumes_AnnouncesAgain_SoAnOlderOneThatResumedMeanwhileStandsDownAsync() {
    // Three versions: ours stood down for a newer one that then died. Older instances that resumed meanwhile are older
    // than ours, and only hear about it if ours announces again once it is serving.
    using var h = _harness(new ScriptedAssessor(StartupVerdict.StandDown, StartupVerdict.Current), online: _fleet(_olderPeer()));
    h.Lifecycle.Phase = LifecyclePhase.Migrating;
    await _tickAsync(h);
    await Assert.That(h.Bus.Published).IsEmpty().Because("a stood-down instance announces nothing");

    await h.Bus.DeliverAsync<InstanceDiedSignal>();
    await _tickAsync(h);
    await _tickAsync(h);

    await Assert.That(h.Bus.Published).IsEquivalentTo([nameof(NewerVersionOnlineSignal)]);
  }

  [Test]
  public async Task AWatcherWithoutTheBusOrTheFleet_NeitherAnnouncesNorSubscribesAsync() {
    using var h = _harness(new ScriptedAssessor(StartupVerdict.StandDown), online: _fleet(_olderPeer()), wired: false);

    await _tickAsync(h);

    await Assert.That(h.Online.Reads).IsEqualTo(0);
    await Assert.That(h.Bus.ActiveSubscriptions).IsEqualTo(0);
    await Assert.That(h.RunControl.HaltedFor).IsEqualTo(RunHaltReason.StandDown).Because("the slow cadence still keeps the verdict");
  }

  [Test]
  public async Task Disposing_TheWatcher_ReleasesItsSubscriptionsAsync() {
    using var h = _harness();
    await _tickAsync(h);
    await Assert.That(h.Bus.ActiveSubscriptions).IsEqualTo(3)
      .Because("a newer version online, an instance that died, and one that left each prompt the question");

    h.Watcher.Dispose();

    await Assert.That(h.Bus.ActiveSubscriptions).IsEqualTo(0);
  }

  [Test]
  public async Task WithoutALifecycleToTellWhenItServes_NothingIsAnnouncedAsync() {
    using var h = _harness(online: _fleet(_olderPeer()), withLifecycle: false);

    await _tickAsync(h);

    await Assert.That(h.Online.Reads).IsEqualTo(0).Because("an instance that cannot tell it serves never announces itself");
  }

  [Test]
  public async Task WithoutAnApplicationVersion_OnlyTheLibraryVersionCanMakeItNewerAsync() {
    // An unknown application version never stands another instance down: only a newer library version announces.
    using var h = _harness(online: _fleet(_olderPeer()), withApplicationVersion: false);
    await _tickAsync(h);
    await Assert.That(h.Bus.Published).IsEmpty();

    using var newerLibrary = _harness(online: _fleet(new VersionSighting("0.9.0", "0.9.0", _older)), withApplicationVersion: false);
    await _tickAsync(newerLibrary);
    await Assert.That(newerLibrary.Bus.Published).IsEquivalentTo([nameof(NewerVersionOnlineSignal)]);
  }
}
