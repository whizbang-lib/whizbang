#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Adaptive cadence decision for <see cref="HeartbeatWorker"/>. The worker
/// resolves the cadence on each tick via <c>_resolveCadenceSeconds</c> which
/// reads the alive-lock state plus the configured options. This locks the
/// fast↔slow transition matrix without needing to drive the Task.Delay loop.
/// </summary>
/// <docs>fundamentals/workers/instance-liveness</docs>
public class HeartbeatWorkerAdaptiveCadenceTests {

  [Test]
  public async Task LockHeld_AdvisoryLockMode_UsesSlowCadenceAsync() {
    var worker = _newWorker(opts => {
      opts.IntervalSeconds = 5;
      opts.SlowIntervalSeconds = 60;
      opts.LivenessSourceMode = HeartbeatLivenessSourceMode.AdvisoryLockWhenAvailable;
    }, aliveLockHeld: true);

    await Assert.That(worker.CurrentCadenceSeconds).IsEqualTo(60)
      .Because("Lock held + AdvisoryLockWhenAvailable MUST use SlowIntervalSeconds — that's the whole DISCARD-ALL-saving point of the adaptive heartbeat.");
  }

  [Test]
  public async Task LockNotHeld_AdvisoryLockMode_UsesFastCadenceAsync() {
    var worker = _newWorker(opts => {
      opts.IntervalSeconds = 5;
      opts.SlowIntervalSeconds = 60;
      opts.LivenessSourceMode = HeartbeatLivenessSourceMode.AdvisoryLockWhenAvailable;
    }, aliveLockHeld: false);

    await Assert.That(worker.CurrentCadenceSeconds).IsEqualTo(5)
      .Because("Lock not held → primary liveness signal absent → MUST fall back to fast IntervalSeconds to preserve the 30 s cleanup_stale_instances recovery guarantee.");
  }

  [Test]
  public async Task NoLockSource_AdvisoryLockMode_UsesFastCadenceAsync() {
    var worker = _newWorker(opts => {
      opts.IntervalSeconds = 5;
      opts.SlowIntervalSeconds = 60;
      opts.LivenessSourceMode = HeartbeatLivenessSourceMode.AdvisoryLockWhenAvailable;
    }, aliveLockSource: null);

    await Assert.That(worker.CurrentCadenceSeconds).IsEqualTo(5)
      .Because("No IInstanceAliveLockSource registered (legacy / no-direct-conn host) MUST behave bit-for-bit like the pre-slice-7b path — IntervalSeconds always wins.");
  }

  [Test]
  public async Task HeartbeatTableOnlyMode_IgnoresLockAndUsesFastCadenceAsync() {
    var worker = _newWorker(opts => {
      opts.IntervalSeconds = 5;
      opts.SlowIntervalSeconds = 60;
      opts.LivenessSourceMode = HeartbeatLivenessSourceMode.HeartbeatTableOnly;
    }, aliveLockHeld: true);

    await Assert.That(worker.CurrentCadenceSeconds).IsEqualTo(5)
      .Because("HeartbeatTableOnly is the legacy opt-out — operators who don't trust the adaptive behaviour set this and the lock signal MUST be ignored.");
  }

  [Test]
  public async Task LockTransitionsHeldToNotHeld_NextResolveReturnsFastCadenceAsync() {
    var lockSource = new _toggleableLockSource(initialHeld: true);
    var worker = _newWorker(opts => {
      opts.IntervalSeconds = 5;
      opts.SlowIntervalSeconds = 60;
      opts.LivenessSourceMode = HeartbeatLivenessSourceMode.AdvisoryLockWhenAvailable;
    }, lockSource: lockSource);

    await Assert.That(worker.CurrentCadenceSeconds).IsEqualTo(60);

    lockSource.IsAliveLockHeld = false;

    await Assert.That(worker.CurrentCadenceSeconds).IsEqualTo(5)
      .Because("Disconnect causes the next-tick resolve to revert to fast cadence — the user explicitly called out that we must preserve 30 s recovery on disconnect.");
  }

  [Test]
  public async Task LockTransitionsNotHeldToHeld_NextResolveReturnsSlowCadenceAsync() {
    var lockSource = new _toggleableLockSource(initialHeld: false);
    var worker = _newWorker(opts => {
      opts.IntervalSeconds = 5;
      opts.SlowIntervalSeconds = 60;
    }, lockSource: lockSource);

    await Assert.That(worker.CurrentCadenceSeconds).IsEqualTo(5);

    lockSource.IsAliveLockHeld = true;

    await Assert.That(worker.CurrentCadenceSeconds).IsEqualTo(60)
      .Because("Reconnect causes the next-tick resolve to slow back down — symmetric with the disconnect direction.");
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static HeartbeatWorker _newWorker(
      Action<HeartbeatWorkerOptions> configure,
      bool? aliveLockHeld = null,
      IInstanceAliveLockSource? lockSource = null,
      IInstanceAliveLockSource? aliveLockSource = null) {
    var opts = new HeartbeatWorkerOptions();
    configure(opts);

    IInstanceAliveLockSource? source = aliveLockSource;
    if (source is null && lockSource is not null) {
      source = lockSource;
    }
    if (source is null && aliveLockHeld is not null) {
      source = new _toggleableLockSource(aliveLockHeld.Value);
    }

    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    var sp = services.BuildServiceProvider();

    return new HeartbeatWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
      schemaReadyGate: new _stubSchemaReadyGate(),
      options: Options.Create(opts),
      logger: NullLogger<HeartbeatWorker>.Instance,
      pinnedPool: null,
      aliveLockSource: source);
  }

  private sealed class _toggleableLockSource : IInstanceAliveLockSource {
    public _toggleableLockSource(bool initialHeld) {
      IsAliveLockHeld = initialHeld;
    }
    public bool IsAliveLockHeld { get; set; }
  }

  private sealed class _stubSchemaReadyGate : ISchemaReadyGate {
    public Task WaitForReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public bool IsReady => true;
    public void MarkReady() { }
  }
}
