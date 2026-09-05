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
    private readonly TaskCompletionSource _secondRead =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _readEntered =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reads;

    public StandbyRequest? Request { get; set; } = request;

    /// <summary>Set to make the next standby read throw, as a transient database error would.</summary>
    public bool FailNextRead { get; set; }

    /// <summary>Completes once the watcher has read the ledger a second time.</summary>
    public Task SecondRead => _secondRead.Task;

    /// <summary>Set to make a read hang until the watcher is cancelled.</summary>
    public bool BlockNextRead { get; set; }

    /// <summary>Completes once a blocking read has begun.</summary>
    public Task ReadEntered => _readEntered.Task;

    /// <summary>How many times the watcher has consulted the standby ledger.</summary>
    public int Reads => Volatile.Read(ref _reads);

    public async Task<StandbyRequest?> GetStandbyRequestAsync(CancellationToken cancellationToken = default) {
      if (Interlocked.Increment(ref _reads) >= 2) {
        _secondRead.TrySetResult();
      }
      if (FailNextRead) {
        FailNextRead = false;
        throw new InvalidOperationException("transient standby-ledger read failure");
      }
      if (BlockNextRead) {
        _readEntered.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
      }
      return Request;
    }

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
  public async Task ExecuteAsync_CanceledBeforeSchemaReady_ReturnsCleanlyAsync(
      CancellationToken testToken) {
    // A host that fails during migration stops everything it built. The watcher reads the
    // standby table, which may not exist yet, so it waits on the schema gate first.
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => new StandbyCoordinator(null));
    await using var sp = services.BuildServiceProvider();

    var gate = new BlockingSchemaGate();
    var watcher = new StandbyWatcher(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new RecordingLifecycle(),
      new RecordingHostLifetime(),
      new StubInstanceProvider(),
      gate,
      versionProvider: new StubVersionProvider("1.0.0"));

    using var cts = new CancellationTokenSource();
    await watcher.StartAsync(cts.Token);
    // StartAsync returning does not mean ExecuteAsync has run -- the host starts it on the thread
    // pool. Cancelling before it reaches the gate would leave a task that was cancelled before it
    // began, and "not faulted" is true of that too, so the assertions below would hold without the
    // watcher ever having handled anything.
    await gate.WaitEntered.WaitAsync(testToken);
    var executeTask = watcher.ExecuteTask;
    await cts.CancelAsync();
    await watcher.StopAsync(CancellationToken.None);

    await Assert.That(executeTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("the watcher absorbs the cancellation and returns; a faulted or cancelled task "
             + "turns an ordinary shutdown into a reported crash");
  }

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_ATickThatThrows_DoesNotKillTheWatcherAsync(
      CancellationToken testToken) {
    // The watcher is the only thing that answers a standby request. If one failed ledger read
    // ended its loop, the instance would go on serving against a schema another node is about to
    // migrate underneath it, and nothing would say so -- the host still sees a running service.
    // A transient read failure must cost one tick, not the watcher.
    var coordinator = new StandbyCoordinator(null) { FailNextRead = true };
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    await using var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var watcher = new StandbyWatcher(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new RecordingLifecycle(),
      new RecordingHostLifetime(),
      new StubInstanceProvider(),
      gate,
      versionProvider: new StubVersionProvider("1.0.0"),
      options: new StandbyWatcherOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

    using var cts = new CancellationTokenSource();
    await watcher.StartAsync(cts.Token);

    // The second read can only happen if the loop survived the first one throwing.
    await coordinator.SecondRead.WaitAsync(testToken);

    await cts.CancelAsync();
    await watcher.StopAsync(CancellationToken.None);

    await Assert.That(watcher.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("the loop swallows a tick failure and keeps watching, so shutdown is still clean");
  }

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_WithNoInstanceIdentity_NeverConsultsTheLedgerAsync(
      CancellationToken testToken) {
    // Standing by is answering a request addressed to a specific instance. Without an identity
    // this process cannot be the one being asked, so acting on a request it found would mean
    // taking itself out of rotation on the strength of someone else's handshake.
    var coordinator = new StandbyCoordinator(_request());
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    await using var sp = services.BuildServiceProvider();

    var lifecycle = new RecordingLifecycle();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var watcher = new StandbyWatcher(
      sp.GetRequiredService<IServiceScopeFactory>(),
      lifecycle,
      new RecordingHostLifetime(),
      instanceProvider: null!,
      gate,
      versionProvider: new StubVersionProvider("1.0.0"));

    using var cts = new CancellationTokenSource();
    await watcher.StartAsync(cts.Token);
    await watcher.ExecuteTask!.WaitAsync(testToken);
    await watcher.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.Reads).IsEqualTo(0)
      .Because("an instance with no identity has no handshake to participate in, and a live "
             + "request sitting in the ledger is not its to answer");
    await Assert.That(lifecycle.Advanced).IsEmpty()
      .Because("standing down here would take a healthy instance out of rotation on a request "
             + "that was never addressed to it");
  }

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_CanceledMidTick_EndsTheLoopRatherThanRetryingAsync(
      CancellationToken testToken) {
    // Shutdown lands while a ledger read is in flight, which is the common case: the read is where
    // the loop spends its time. The cancellation must end the loop, not fall into the guardian
    // catch that treats a tick failure as transient -- that would log an error and retry on every
    // ordinary shutdown, teaching operators that watcher errors are normal.
    var coordinator = new StandbyCoordinator(null) { BlockNextRead = true };
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    await using var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var watcher = new StandbyWatcher(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new RecordingLifecycle(),
      new RecordingHostLifetime(),
      new StubInstanceProvider(),
      gate,
      versionProvider: new StubVersionProvider("1.0.0"),
      options: new StandbyWatcherOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

    using var cts = new CancellationTokenSource();
    await watcher.StartAsync(cts.Token);
    await coordinator.ReadEntered.WaitAsync(testToken);
    await cts.CancelAsync();
    await watcher.StopAsync(CancellationToken.None);

    await Assert.That(watcher.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("a cancellation arriving mid-tick is a shutdown, not a tick failure");
  }

  /// <summary>A schema gate that never opens, and says when the watcher started waiting on it.</summary>
  private sealed class BlockingSchemaGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _waitEntered =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the watcher has actually reached the gate.</summary>
    public Task WaitEntered => _waitEntered.Task;

    public bool IsReady => false;
    public void MarkReady() { }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _waitEntered.TrySetResult();
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
  }
}
