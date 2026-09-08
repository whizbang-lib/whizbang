#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// What a heartbeat carries besides "I am here". The registry row can be created by the first
/// beat (or by a claim) after the last lifecycle transition already happened, in which case the
/// phase and version recorded by the transition path are lost and the row stays blank for the
/// life of the instance. Carrying the live phase and version on every beat lets the database
/// backfill them (migration 147). The beat also carries the stale threshold the writer's own
/// cadence implies, so the peer reap inside the heartbeat uses the same number every reader does.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/HeartbeatWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/HeartbeatRequest.cs</code-under-test>
[Category("Core")]
[Category("Workers")]
public class HeartbeatWorkerRequestFieldsTests {

  [Test]
  public async Task BuildRequest_CarriesThePhaseVersionAndDerivedThresholdAsync() {
    var worker = _worker(new StubLifecycle(LifecyclePhase.Running), new StubVersion("1.2.3-alpha.4"));

    var request = worker.BuildRequest();

    await Assert.That(request.LifecyclePhase).IsEqualTo("Running")
      .Because("the phase text is the same rendering the transition path records, so a backfill and a transition agree");
    await Assert.That(request.LibraryVersion).IsEqualTo("1.2.3-alpha.4");
    await Assert.That(request.StaleThresholdSeconds).IsEqualTo(150)
      .Because("2 * 60 + 30 with the default cadence; the peer reap must not use a tighter number than the monitor");
  }

  [Test]
  public async Task Constructor_WithoutLifecycleOrVersionProviders_ThrowsAsync() {
    // Both inputs are what peers and operators read from the registry to tell a live instance from
    // a dead one. An optional dependency is silently null wherever the worker is hand-built, so the
    // worker refuses to start without them; the worker pipeline registers defaults for both.
    await Assert.That(() => _worker(lifecycle: null, version: HeartbeatTestDependencies.Version))
      .Throws<ArgumentNullException>();
    await Assert.That(() => _worker(lifecycle: HeartbeatTestDependencies.LifecycleState, version: null))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task BuildRequest_TrackingThreshold_FollowsTheOptionsAsync() {
    var worker = _worker(HeartbeatTestDependencies.LifecycleState, HeartbeatTestDependencies.Version, new HeartbeatWorkerOptions {
      IntervalSeconds = 10,
      SlowIntervalSeconds = 20,
      LivenessSourceMode = HeartbeatLivenessSourceMode.HeartbeatTableOnly,
    });

    var request = worker.BuildRequest();

    await Assert.That(request.StaleThresholdSeconds).IsEqualTo(30)
      .Because("table-only mode never slows down, so 2 * 10 + 10");
  }

  [Test]
  public async Task BuildRequest_IdentityFieldsComeFromTheInstanceProviderAsync() {
    var provider = new ServiceInstanceProvider(Guid.CreateVersion7(), "svc-a", "host-a", processId: 42);
    var worker = _worker(HeartbeatTestDependencies.LifecycleState, HeartbeatTestDependencies.Version, provider: provider);

    var request = worker.BuildRequest();

    await Assert.That(request.InstanceId).IsEqualTo(provider.InstanceId);
    await Assert.That(request.ServiceName).IsEqualTo("svc-a");
    await Assert.That(request.HostName).IsEqualTo("host-a");
    await Assert.That(request.ProcessId).IsEqualTo(42);
  }

  [Test]
  public async Task HeartbeatRequest_LegacyPositionalConstruction_StillCompilesWithNullExtrasAsync() {
    // Existing callers construct the request with four arguments; the new fields default to null
    // and the database keeps its defaults for them.
    var request = new HeartbeatRequest(Guid.Empty, "svc", "host", 1);

    await Assert.That(request.Metadata).IsNull();
    await Assert.That(request.LifecyclePhase).IsNull();
    await Assert.That(request.LibraryVersion).IsNull();
    await Assert.That(request.StaleThresholdSeconds).IsNull();
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static HeartbeatWorker _worker(
      IWhizbangLifecycleState? lifecycle,
      ILibraryVersionProvider? version,
      HeartbeatWorkerOptions? options = null,
      IServiceInstanceProvider? provider = null) {
    var services = new ServiceCollection();
    services.AddSingleton(provider ?? new ServiceInstanceProvider(configuration: null));
    var sp = services.BuildServiceProvider();

    return new HeartbeatWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
      schemaReadyGate: _readyGate(),
      options: Options.Create(options ?? new HeartbeatWorkerOptions()),
      logger: NullLogger<HeartbeatWorker>.Instance,
      lifecycleState: lifecycle!,
      libraryVersion: version!,
      pinnedPool: null,
      aliveLockSource: null,
      signalBus: null,
      timeProvider: null);
  }

  private sealed class StubLifecycle(LifecyclePhase phase) : IWhizbangLifecycleState {
    public LifecyclePhase Phase => phase;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask FaultAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }

  private sealed class StubVersion(string version) : ILibraryVersionProvider {
    public string LibraryVersion => version;
  }

  /// <summary>The worker refuses a null gate; a ready one lets the loop past its startup wait.</summary>
  private static SchemaReadyGate _readyGate() {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return gate;
  }
}
