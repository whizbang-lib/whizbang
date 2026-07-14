using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;
using Whizbang.Core.Temporal;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Unit tests for <see cref="ScheduleWorker"/> — the drain loop resolves an <see cref="IScheduleClaimer"/>
/// per pass and claims in batches until a claim returns fewer than the limit, no-ops when no claimer is
/// registered, and wakes early when a <see cref="ScheduleDueSignal"/> arrives on the bus.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public class ScheduleWorkerTests {
  private sealed class FakeClaimer : IScheduleClaimer {
    private readonly Queue<int> _returns;
    public int Calls { get; private set; }
    public DateTimeOffset? NextFireTime { get; set; }
    public FakeClaimer(params int[] returns) => _returns = new Queue<int>(returns);
    public Task<int> ClaimDueSchedulesAsync(int limit, CancellationToken cancellationToken = default) {
      Calls++;
      return Task.FromResult(_returns.Count > 0 ? _returns.Dequeue() : 0);
    }
    public Task<DateTimeOffset?> GetNextFireTimeAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(NextFireTime);
  }

  private static (ScheduleWorker Worker, FakeClaimer? Claimer) _create(
      TemporalOptions? options = null, ISignalBus? bus = null, FakeClaimer? claimer = null, TimeProvider? clock = null) {
    var services = new ServiceCollection();
    if (claimer is not null) {
      services.AddSingleton<IScheduleClaimer>(claimer);
    }
    var provider = services.BuildServiceProvider();
    var worker = new ScheduleWorker(
      provider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(options ?? new TemporalOptions()),
      NullLogger<ScheduleWorker>.Instance,
      schemaReadyGate: null,
      signalBus: bus,
      timeProvider: clock);
    return (worker, claimer);
  }

  [Test]
  public async Task TickOnce_NoClaimer_ReturnsZeroAsync() {
    var (worker, _) = _create();
    await Assert.That(await worker.TickOnceAsync()).IsEqualTo(0);
  }

  [Test]
  public async Task TickOnce_StopsWhenBelowBatchLimitAsync() {
    var claimer = new FakeClaimer(2);   // one short batch => drained after a single call
    var (worker, _) = _create(new TemporalOptions { ClaimBatchLimit = 100 }, claimer: claimer);

    var total = await worker.TickOnceAsync();

    await Assert.That(total).IsEqualTo(2);
    await Assert.That(claimer.Calls).IsEqualTo(1);
  }

  [Test]
  public async Task TickOnce_DrainsWhileBatchIsFullAsync() {
    // Two full batches (== limit) then a short one => three claim calls, then stop.
    var claimer = new FakeClaimer(10, 10, 4);
    var (worker, _) = _create(new TemporalOptions { ClaimBatchLimit = 10 }, claimer: claimer);

    var total = await worker.TickOnceAsync();

    await Assert.That(total).IsEqualTo(24);
    await Assert.That(claimer.Calls).IsEqualTo(3);
  }

  [Test]
  public async Task RequestImmediateRun_ReleasesWakeAsync() {
    var (worker, _) = _create();

    worker.RequestImmediateRun();

    await Assert.That(worker.TryConsumeWakeForTests()).IsTrue();
    await Assert.That(worker.TryConsumeWakeForTests()).IsFalse();   // consumed
  }

  [Test]
  public async Task RequestImmediateRun_CoalescesMultipleWakesAsync() {
    var (worker, _) = _create();

    worker.RequestImmediateRun();
    worker.RequestImmediateRun();   // second is coalesced (semaphore capacity 1)

    await Assert.That(worker.TryConsumeWakeForTests()).IsTrue();
    await Assert.That(worker.TryConsumeWakeForTests()).IsFalse();
  }

  [Test]
  public async Task ScheduleDueSignal_Received_WakesWorkerAsync() {
    var bus = new SignalBus([]);
    var (worker, _) = _create(bus: bus);   // ctor subscribes to ScheduleDueSignal

    await ((ISignalSink)bus).ReceiveAsync(new ScheduleDueSignal());

    await Assert.That(worker.TryConsumeWakeForTests()).IsTrue();
  }

  [Test]
  public async Task ArmTimerOnce_ArmsToClaimerNextTimeAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var next = clock.GetUtcNow().AddSeconds(30);
    var claimer = new FakeClaimer { NextFireTime = next };
    var (worker, _) = _create(claimer: claimer, clock: clock);

    await worker.ArmTimerOnceAsync();

    await Assert.That(worker.TimerForTests.ArmedFor).IsEqualTo(next);
  }

  [Test]
  public async Task ArmTimerOnce_NoClaimer_LeavesTimerDisarmedAsync() {
    var (worker, _) = _create();   // no claimer registered

    await worker.ArmTimerOnceAsync();

    await Assert.That(worker.TimerForTests.ArmedFor).IsNull();
  }

  [Test]
  public async Task Timer_FiresAtNextFireTime_WakesWorkerAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 13, 12, 00, 00, TimeSpan.Zero));
    var claimer = new FakeClaimer { NextFireTime = clock.GetUtcNow().AddSeconds(30) };
    var (worker, _) = _create(claimer: claimer, clock: clock);
    await worker.ArmTimerOnceAsync();

    clock.Advance(TimeSpan.FromSeconds(29));
    await Assert.That(worker.TryConsumeWakeForTests()).IsFalse();   // not yet due

    clock.Advance(TimeSpan.FromSeconds(1));                          // now at the fire time
    await Assert.That(worker.TryConsumeWakeForTests()).IsTrue();     // timer rang the doorbell
    await Assert.That(worker.TimerForTests.WakeCount).IsEqualTo(1L);
  }
}
