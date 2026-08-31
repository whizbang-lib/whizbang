using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 4 of zero-idle-polling — locks the
/// <see cref="BackupTickCoordinator"/> contract for two methods that
/// don't require spinning up the BackgroundService loop:
/// <list type="bullet">
/// <item><description><c>FireOneCycleAsync</c> — invokes every registered, enabled tick exactly once, isolating per-tick failures, updating counters.</description></item>
/// <item><description><c>ComputeEffectivePollingInterval</c> — chooses fast/slow cadence based on the gate's <c>IsAvailable</c>.</description></item>
/// </list>
///
/// <para>
/// The full state machine (ASLEEP/POLLING transitions driven by
/// <see cref="IdleActivityTracker.TimeSinceLastActivity"/>) is exercised
/// integration-style in a separate suite when the wiring lands; this
/// class covers the unit-testable pure-behavior surface area.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/backup-tick-coordinator</docs>
public class BackupTickCoordinatorTests {

  private sealed class FakeGate(bool isAvailable) : INotifySignalingGate {
    public bool IsAvailable { get; private set; } = isAvailable;
    public DateTimeOffset? LastVerifiedAt => null;
    public DateTimeOffset? LastFailureAt => null;
    public string? LastFailureReason => null;
    public event Action<bool>? OnAvailabilityChanged;
    public Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsAvailable);
    public void Set(bool available) {
      if (IsAvailable == available) {
        return;
      }
      IsAvailable = available;
      OnAvailabilityChanged?.Invoke(available);
    }
  }

  private static BackupTickCoordinator _build(
      IBackupTickRegistry registry,
      INotifySignalingGate? gate = null,
      BackupTickCoordinatorOptions? options = null,
      TimeProvider? timeProvider = null) =>
    new(
      new IdleActivityTracker(timeProvider),
      registry,
      Options.Create(options ?? new BackupTickCoordinatorOptions()),
      NullLogger<BackupTickCoordinator>.Instance,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      gate: gate,
      timeProvider: timeProvider);

  // ============================================================================
  // FireOneCycleAsync
  // ============================================================================

  [Test]
  public async Task FireOneCycleAsync_NoRegistrations_InvokesNothingAsync() {
    var registry = new BackupTickRegistry();
    var coordinator = _build(registry);

    var invoked = await coordinator.FireOneCycleAsync(CancellationToken.None);

    await Assert.That(invoked).IsEqualTo(0);
    await Assert.That(coordinator.TotalTickCycles).IsEqualTo(1L)
      .Because("Cycle still counts as 'one run' even when there's nothing to run — observability needs to see the loop ticking.");
  }

  [Test]
  public async Task FireOneCycleAsync_OneEnabledRegistration_FiresOnceAsync() {
    var registry = new BackupTickRegistry();
    var count = 0;
    registry.Register("counter", _ => { count++; return Task.CompletedTask; }, () => true);
    var coordinator = _build(registry);

    var invoked = await coordinator.FireOneCycleAsync(CancellationToken.None);

    await Assert.That(invoked).IsEqualTo(1);
    await Assert.That(count).IsEqualTo(1);
    await Assert.That(coordinator.TotalDelegateInvocations).IsEqualTo(1L);
  }

  [Test]
  public async Task FireOneCycleAsync_DisabledRegistration_SkippedAsync() {
    var registry = new BackupTickRegistry();
    var ranEnabled = false;
    var ranDisabled = false;
    registry.Register("disabled", _ => { ranDisabled = true; return Task.CompletedTask; }, () => false);
    registry.Register("enabled", _ => { ranEnabled = true; return Task.CompletedTask; }, () => true);
    var coordinator = _build(registry);

    var invoked = await coordinator.FireOneCycleAsync(CancellationToken.None);

    await Assert.That(ranDisabled).IsFalse()
      .Because("isEnabled() predicate must be evaluated immediately before each tick fires — a false return means skip.");
    await Assert.That(ranEnabled).IsTrue();
    await Assert.That(invoked).IsEqualTo(1);
  }

  [Test]
  public async Task FireOneCycleAsync_OneTickThrows_OthersStillRunAsync() {
    var registry = new BackupTickRegistry();
    var ranBefore = false;
    var ranAfter = false;
    registry.Register("before", _ => { ranBefore = true; return Task.CompletedTask; }, () => true);
    registry.Register("throws", _ => throw new InvalidOperationException("boom"), () => true);
    registry.Register("after", _ => { ranAfter = true; return Task.CompletedTask; }, () => true);
    var coordinator = _build(registry);

    var invoked = await coordinator.FireOneCycleAsync(CancellationToken.None);

    await Assert.That(ranBefore).IsTrue();
    await Assert.That(ranAfter).IsTrue()
      .Because("A failure in 'throws' must not prevent 'after' from firing — that's the entire point of per-tick try/catch isolation.");
    await Assert.That(invoked).IsEqualTo(3)
      .Because("'invoked' counts every delegate that was actually called, including the one that threw.");
    await Assert.That(coordinator.TotalDelegateFailures).IsEqualTo(1L)
      .Because("Failures are counted separately so observability can alert on a misbehaving registration.");
  }

  [Test]
  public async Task FireOneCycleAsync_PreservesRegistrationOrderAsync() {
    var registry = new BackupTickRegistry();
    var sequence = new List<string>();
    registry.Register("a", _ => { sequence.Add("a"); return Task.CompletedTask; }, () => true);
    registry.Register("b", _ => { sequence.Add("b"); return Task.CompletedTask; }, () => true);
    registry.Register("c", _ => { sequence.Add("c"); return Task.CompletedTask; }, () => true);
    var coordinator = _build(registry);

    await coordinator.FireOneCycleAsync(CancellationToken.None);

    await Assert.That(sequence).IsEquivalentTo(["a", "b", "c"]);
  }

  [Test]
  public async Task FireOneCycleAsync_StoppingTokenCancelled_HaltsIterationAsync() {
    var registry = new BackupTickRegistry();
    var ranFirst = false;
    var ranSecond = false;
    using var cts = new CancellationTokenSource();
    registry.Register("first", _ => { ranFirst = true; cts.Cancel(); return Task.CompletedTask; }, () => true);
    registry.Register("second", _ => { ranSecond = true; return Task.CompletedTask; }, () => true);
    var coordinator = _build(registry);

    await coordinator.FireOneCycleAsync(cts.Token);

    await Assert.That(ranFirst).IsTrue();
    await Assert.That(ranSecond).IsFalse()
      .Because("Cancellation during a cycle must short-circuit the iteration so shutdown is bounded.");
  }

  // ============================================================================
  // ComputeEffectivePollingInterval
  // ============================================================================

  [Test]
  public async Task ComputeEffectivePollingInterval_NoGate_UsesPollingIntervalAsync() {
    var registry = new BackupTickRegistry();
    var options = new BackupTickCoordinatorOptions {
      PollingInterval = TimeSpan.FromSeconds(30),
      FastPollingInterval = TimeSpan.FromSeconds(5),
    };
    var coordinator = _build(registry, gate: null, options: options);

    var interval = coordinator.ComputeEffectivePollingInterval();

    await Assert.That(interval).IsEqualTo(TimeSpan.FromSeconds(30))
      .Because("When no gate is wired, default to the relaxed cadence — pre-Slice-4 behavior preserved.");
  }

  [Test]
  public async Task ComputeEffectivePollingInterval_GateHealthy_UsesPollingIntervalAsync() {
    var registry = new BackupTickRegistry();
    var options = new BackupTickCoordinatorOptions {
      PollingInterval = TimeSpan.FromSeconds(30),
      FastPollingInterval = TimeSpan.FromSeconds(5),
    };
    var coordinator = _build(registry, gate: new FakeGate(true), options: options);

    var interval = coordinator.ComputeEffectivePollingInterval();

    await Assert.That(interval).IsEqualTo(TimeSpan.FromSeconds(30))
      .Because("Gate healthy means NOTIFY is delivering signals — the backstop cadence can stay relaxed.");
  }

  [Test]
  public async Task ComputeEffectivePollingInterval_GateBroken_UsesFastPollingIntervalAsync() {
    var registry = new BackupTickRegistry();
    var options = new BackupTickCoordinatorOptions {
      PollingInterval = TimeSpan.FromSeconds(30),
      FastPollingInterval = TimeSpan.FromSeconds(5),
    };
    var coordinator = _build(registry, gate: new FakeGate(false), options: options);

    var interval = coordinator.ComputeEffectivePollingInterval();

    await Assert.That(interval).IsEqualTo(TimeSpan.FromSeconds(5))
      .Because("Gate broken means no NOTIFY signals are arriving — the backstop must tighten to compensate.");
  }
}
