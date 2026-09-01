using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The rolling-deploy standby handshake, from the older instance's side.
/// </summary>
/// <remarks>
/// When a newer binary needs to migrate the schema, it asks its peers to stand by: stop serving,
/// hold the data plane, and wait to be told whether the migration committed or rolled back. This
/// watcher is what an older instance runs to answer that.
///
/// <para>
/// Both mistakes it can make are outages. Standing down when it should not have takes a healthy
/// instance out of rotation on someone else's say-so — and the request comes from another process,
/// so the decision has to be defensible against a stale, self-addressed, or unrankable one.
/// Failing to stand down when it should leaves an old binary serving against a schema that has
/// moved beneath it. Every guard below is one of those two.
/// </para>
/// </remarks>
[Category("Core")]
[Category("Startup")]
public class StandbyWatcherTests {

  private static readonly Guid _us = Guid.Parse("0199aaaa-0000-0000-0000-000000000001");
  private static readonly Guid _peer = Guid.Parse("0199aaaa-0000-0000-0000-000000000002");

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId => _us;
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class StubVersionProvider(string version) : ILibraryVersionProvider {
    public string LibraryVersion => version;
  }

  private sealed class RecordingLifecycle : IWhizbangLifecycleState {
    public List<LifecyclePhase> Advanced { get; } = [];
    public LifecyclePhase Phase { get; private set; } = LifecyclePhase.Running;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Advanced.Add(phase);
      Phase = phase;
      return ValueTask.CompletedTask;
    }
    public ValueTask FaultAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }

  private sealed class RecordingHostLifetime : IHostApplicationLifetime {
    public int StopCalls { get; private set; }
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => StopCalls++;
  }

  private sealed class StubAssessor(StartupVerdict verdict, string reason = "test") : IStartupAssessor {
    public int Calls { get; private set; }
    public Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) {
      Calls++;
      return Task.FromResult(new StartupAssessment(verdict, reason));
    }
  }

  /// <summary>The posted request, which the migrator withdraws when the handshake ends.</summary>
  private sealed class StandbyCoordinator(StandbyRequest? request) : IWorkCoordinator {
    public StandbyRequest? Request { get; set; } = request;

    public Task<StandbyRequest?> GetStandbyRequestAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(Request);

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

  private sealed record Harness(
    StandbyWatcher Watcher,
    RecordingLifecycle Lifecycle,
    RecordingHostLifetime Host,
    StandbyCoordinator? Coordinator,
    ServiceProvider Provider) : IDisposable {
    public void Dispose() => Provider.Dispose();
  }

  private static Harness _harness(
      StandbyRequest? request,
      string ourVersion = "1.0.0",
      IStartupAssessor? assessor = null,
      bool withCoordinator = true,
      StandbyWatcherOptions? options = null) {
    var services = new ServiceCollection();
    var coordinator = withCoordinator ? new StandbyCoordinator(request) : null;
    if (coordinator is not null) {
      services.AddScoped<IWorkCoordinator>(_ => coordinator);
    }
    var sp = services.BuildServiceProvider();

    var lifecycle = new RecordingLifecycle();
    var host = new RecordingHostLifetime();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var watcher = new StandbyWatcher(
      sp.GetRequiredService<IServiceScopeFactory>(),
      lifecycle,
      host,
      new StubInstanceProvider(),
      gate,
      versionProvider: new StubVersionProvider(ourVersion),
      assessor: assessor,
      pipelineRunner: null,
      options: options ?? new StandbyWatcherOptions());

    return new Harness(watcher, lifecycle, host, coordinator, sp);
  }

  private static StandbyRequest _request(
      Guid? by = null, string version = "2.0.0", int heartbeatSecondsAgo = 1) =>
    new(by ?? _peer, version, DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddSeconds(-heartbeatSecondsAgo));

  // ============================================================
  // Standing by
  // ============================================================

  [Test]
  public async Task ANewerLivePeersRequest_BindsUsAsync() {
    using var h = _harness(_request(version: "2.0.0"), ourVersion: "1.0.0");

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).Contains(LifecyclePhase.StandingBy)
      .Because("a newer binary is migrating the schema this instance is serving against");
  }

  [Test]
  public async Task OurOwnRequest_DoesNotBindUsAsync() {
    // The migrator posts the request that drains its peers. Binding on it would make the
    // instance doing the migration stand itself down, and the deploy would never complete.
    using var h = _harness(_request(by: _us, version: "2.0.0"), ourVersion: "1.0.0");

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).IsEmpty()
      .Because("our own request binds our peers, not us — binding on it deadlocks the deploy");
  }

  [Test]
  public async Task ADeadRequestersRequest_IsVoidAsync() {
    // A migrator that crashed mid-deploy leaves its request behind. Honoring it would drain the
    // fleet on behalf of a process that is not coming back.
    using var h = _harness(
      _request(version: "2.0.0", heartbeatSecondsAgo: 600), ourVersion: "1.0.0");

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).IsEmpty()
      .Because("a dead migrator's request is void — otherwise a crash drains the whole fleet");
  }

  [Test]
  public async Task ARequestWithNoHeartbeatAtAll_IsVoidAsync() {
    using var h = _harness(
      new StandbyRequest(_peer, "2.0.0", DateTimeOffset.UtcNow, RequesterLastHeartbeatAt: null),
      ourVersion: "1.0.0");

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).IsEmpty();
  }

  [Test]
  [Arguments("1.0.0")]
  [Arguments("0.9.0")]
  public async Task AnOlderOrEqualPeer_HasNoStandingToDrainUsAsync(string theirVersion) {
    // Standby is for making way for a newer schema. An equal or older peer draining us is either
    // a stale request or a rollback in progress, and complying would remove capacity for nothing.
    using var h = _harness(_request(version: theirVersion), ourVersion: "1.0.0");

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).IsEmpty();
  }

  [Test]
  public async Task AnUnparseableRequestedVersion_RefusesToBindAsync() {
    // Never stand down on a guess: if the request's version cannot be ranked, the safe reading
    // is that it has not been shown to be newer.
    using var h = _harness(_request(version: "not-a-version"), ourVersion: "1.0.0");

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).IsEmpty()
      .Because("an unrankable request must not drain a healthy instance");
  }

  [Test]
  public async Task WithoutAVersionOfOurOwn_WeComplyAsync() {
    // The one place the conservative reading flips: an instance that cannot rank itself has no
    // basis to refuse, and refusing would leave an unknown binary serving through a migration.
    using var h = _harness(_request(version: "2.0.0"), ourVersion: "not-a-version");

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).Contains(LifecyclePhase.StandingBy)
      .Because("unable to rank ourselves, complying is safer than serving through a migration");
  }

  [Test]
  public async Task NoRequestAtAll_ChangesNothingAsync() {
    using var h = _harness(request: null);

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).IsEmpty();
    await Assert.That(h.Host.StopCalls).IsEqualTo(0);
  }

  [Test]
  public async Task WithNoCoordinator_TheTickIsInertAsync() {
    // Before the data layer is wired there is nothing to read a request from, and the watcher
    // still runs as a hosted service.
    using var h = _harness(request: null, withCoordinator: false);

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).IsEmpty();
  }

  // ============================================================
  // The outcome, once standing by
  // ============================================================

  [Test]
  public async Task WhileTheRequestIsStillLive_WeHoldAsync() {
    // The handshake is in flight. Deciding an outcome now would pre-empt the migration.
    using var h = _harness(_request(version: "2.0.0"), ourVersion: "1.0.0");
    await h.Watcher.TickForTestsAsync(CancellationToken.None);
    h.Lifecycle.Advanced.Clear();

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).IsEmpty();
    await Assert.That(h.Host.StopCalls).IsEqualTo(0);
  }

  [Test]
  public async Task AWithdrawnRequestAndACommittedMigration_ShutsUsDownAsync() {
    // The migrator withdrew the request and the assessor says the schema is ahead of us: the
    // migration committed. Serving on would run old code against a new schema, which is the
    // exact outcome the handshake exists to prevent.
    var assessor = new StubAssessor(StartupVerdict.StandDown, "schema is ahead of this binary");
    using var h = _harness(_request(version: "2.0.0"), ourVersion: "1.0.0", assessor: assessor);
    await h.Watcher.TickForTestsAsync(CancellationToken.None);
    await Assert.That(h.Lifecycle.Advanced).Contains(LifecyclePhase.StandingBy);

    h.Coordinator!.Request = null;
    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Host.StopCalls).IsEqualTo(1)
      .Because("the handshake promised to shut down once the migration committed");
  }

  [Test]
  public async Task AWithdrawnRequestAndARollback_RevivesUsAsync() {
    // The migration rolled back, so the schema is unchanged and this instance is still
    // serviceable. Killing it here would turn a rollback — the safe outcome — into lost capacity.
    var assessor = new StubAssessor(StartupVerdict.Serve, "ledger unchanged");
    using var h = _harness(_request(version: "2.0.0"), ourVersion: "1.0.0", assessor: assessor);
    await h.Watcher.TickForTestsAsync(CancellationToken.None);
    h.Lifecycle.Advanced.Clear();

    h.Coordinator!.Request = null;
    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Host.StopCalls).IsEqualTo(0);
    await Assert.That(h.Lifecycle.Advanced).Contains(LifecyclePhase.Running)
      .Because("a rollback resumes the instance rather than costing the fleet its capacity");
  }

  [Test]
  public async Task AfterRevival_AFreshRequestBindsUsAgainAsync() {
    // Revival has to actually clear the standby flag. Leaving it set would make the instance
    // ignore the next deploy's request and serve straight through the following migration.
    var assessor = new StubAssessor(StartupVerdict.Serve, "ledger unchanged");
    using var h = _harness(_request(version: "2.0.0"), ourVersion: "1.0.0", assessor: assessor);
    await h.Watcher.TickForTestsAsync(CancellationToken.None);
    h.Coordinator!.Request = null;
    await h.Watcher.TickForTestsAsync(CancellationToken.None);
    h.Lifecycle.Advanced.Clear();

    h.Coordinator.Request = _request(version: "3.0.0");
    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).Contains(LifecyclePhase.StandingBy)
      .Because("a revived instance must still answer the next deploy's handshake");
  }

  [Test]
  public async Task AMigratorThatDiedMidHandshake_RevivesUsAsync() {
    // The request is still posted but its heartbeat has gone stale — the migrator crashed. That
    // is a rollback in effect: nothing changed, so the instance holding the data plane resumes.
    var assessor = new StubAssessor(StartupVerdict.Serve, "ledger unchanged");
    using var h = _harness(_request(version: "2.0.0"), ourVersion: "1.0.0", assessor: assessor);
    await h.Watcher.TickForTestsAsync(CancellationToken.None);
    h.Lifecycle.Advanced.Clear();

    h.Coordinator!.Request = _request(version: "2.0.0", heartbeatSecondsAgo: 600);
    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).Contains(LifecyclePhase.Running)
      .Because("a crashed migrator must not leave the fleet standing by forever");
  }

  // ============================================================
  // Standing down as obsolete
  // ============================================================

  [Test]
  public async Task AStandDownVerdictOnTheSlowCadence_StandsUsDownAsync() {
    // The verdict is not a startup-only fact: a schema can move under a running instance, so
    // the watcher re-asks periodically rather than trusting what it learned at boot.
    var assessor = new StubAssessor(StartupVerdict.StandDown, "schema moved");
    using var h = _harness(request: null, assessor: assessor,
      options: new StandbyWatcherOptions { ObsolescenceInterval = TimeSpan.Zero });

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(assessor.Calls).IsGreaterThan(0);
    await Assert.That(h.Lifecycle.Advanced).Contains(LifecyclePhase.StandingBy);
  }

  [Test]
  public async Task AServeVerdict_LeavesUsRunningAsync() {
    var assessor = new StubAssessor(StartupVerdict.Serve);
    using var h = _harness(request: null, assessor: assessor,
      options: new StandbyWatcherOptions { ObsolescenceInterval = TimeSpan.Zero });

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).IsEmpty();
  }

  [Test]
  public async Task TheObsolescenceCheckIsRateLimitedAsync() {
    // Assess reads the migration ledger. Running it every poll would turn a five-second watcher
    // into a five-second query against the database for every instance in the fleet.
    var assessor = new StubAssessor(StartupVerdict.Serve);
    using var h = _harness(request: null, assessor: assessor,
      options: new StandbyWatcherOptions { ObsolescenceInterval = TimeSpan.FromHours(1) });

    await h.Watcher.TickForTestsAsync(CancellationToken.None);
    await h.Watcher.TickForTestsAsync(CancellationToken.None);
    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(assessor.Calls).IsEqualTo(1)
      .Because("the ledger read is paced — every tick would query the database per instance");
  }

  [Test]
  public async Task OnceStoodDown_FurtherTicksDoNothingAsync() {
    // Alive but not ready and reapable: replacing it is the orchestrator's decision, and
    // re-advancing the phase every tick would churn the lifecycle for no reason.
    var assessor = new StubAssessor(StartupVerdict.StandDown, "schema moved");
    using var h = _harness(request: null, assessor: assessor,
      options: new StandbyWatcherOptions { ObsolescenceInterval = TimeSpan.Zero });
    await h.Watcher.TickForTestsAsync(CancellationToken.None);
    var callsAfterFirst = assessor.Calls;
    h.Lifecycle.Advanced.Clear();

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).IsEmpty();
    await Assert.That(assessor.Calls).IsEqualTo(callsAfterFirst)
      .Because("a stood-down instance waits for the orchestrator, it does not keep re-assessing");
  }

  [Test]
  public async Task WithoutAnAssessor_ObsolescenceIsNeverCheckedAsync() {
    using var h = _harness(request: null, assessor: null,
      options: new StandbyWatcherOptions { ObsolescenceInterval = TimeSpan.Zero });

    await h.Watcher.TickForTestsAsync(CancellationToken.None);

    await Assert.That(h.Lifecycle.Advanced).IsEmpty();
  }

  // ============================================================
  // Loop resilience
  // ============================================================

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_CancelledBeforeSchemaReady_ReturnsCleanlyAsync(
      CancellationToken testToken) {
    // A host that fails during migration stops everything it built. The watcher reads the
    // standby table, which may not exist yet.
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => new StandbyCoordinator(null));
    await using var sp = services.BuildServiceProvider();

    var watcher = new StandbyWatcher(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new RecordingLifecycle(),
      new RecordingHostLifetime(),
      new StubInstanceProvider(),
      new SchemaReadyGate(),   // never marked ready
      versionProvider: new StubVersionProvider("1.0.0"));

    using var cts = new CancellationTokenSource();
    await watcher.StartAsync(cts.Token);
    var executeTask = watcher.ExecuteTask;
    await cts.CancelAsync();
    await watcher.StopAsync(CancellationToken.None);

    await Assert.That(executeTask!.IsCompleted).IsTrue();
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("a faulted watcher turns an ordinary shutdown into a reported crash");
  }
}
