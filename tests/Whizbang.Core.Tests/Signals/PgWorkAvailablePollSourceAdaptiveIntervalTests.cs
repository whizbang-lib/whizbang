using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Locks the adaptive-interval contract: when the NOTIFY signaling gate flips to unavailable,
/// PgWorkAvailablePollSourceBase reschedules its timer to a tight interval so the pull path
/// carries the wake load. When the gate recovers, the source relaxes back to the ctor interval.
/// </summary>
public class PgWorkAvailablePollSourceAdaptiveIntervalTests {
  private sealed class FakeSignalingGate : INotifySignalingGate {
    private bool _available;
    public bool IsAvailable => _available;
    public DateTimeOffset? LastVerifiedAt { get; private set; }
    public DateTimeOffset? LastFailureAt { get; private set; }
    public string? LastFailureReason { get; private set; }
    public event Action<bool>? OnAvailabilityChanged;
    public void Set(bool available) {
      if (available == _available) { return; }
      _available = available;
      if (available) {
        LastVerifiedAt = DateTimeOffset.UtcNow;
      } else {
        LastFailureAt = DateTimeOffset.UtcNow;
        LastFailureReason = "test-flip";
      }
      OnAvailabilityChanged?.Invoke(available);
    }
    public Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(_available);
  }

  private static PgOutboxWorkAvailablePollSource _create(FakeSignalingGate gate) {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = "Host=fake;Database=fake;Username=u;Password=p" };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "utest-svc", "utest-host", processId: 1);
    return new PgOutboxWorkAvailablePollSource(
      TimeProvider.System, Options.Create(opts), cfg, instance,
      NullLogger<PgOutboxWorkAvailablePollSource>.Instance,
      connectionStringFallback: null,
      signalingGate: gate);
  }

  [Test]
  public async Task Ctor_DefaultsToRelaxedIntervalAsync() {
    var gate = new FakeSignalingGate();
    var source = _create(gate);

    // The default interval is 5s (WorkAvailablePollDefaults.INTERVAL_MILLISECONDS).
    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromMilliseconds(5_000));
  }

  [Test]
  public async Task GateFlipsUnavailable_TightensIntervalAsync() {
    var gate = new FakeSignalingGate();
    gate.Set(true);   // start available (default is false, so this raises the event)
    var source = _create(gate);

    gate.Set(false);

    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromMilliseconds(500))
      .Because("when NOTIFY is down the pull source must carry the wake load at a tight cadence");
  }

  [Test]
  public async Task GateFlipsAvailableAgain_RelaxesToOriginalAsync() {
    var gate = new FakeSignalingGate();
    gate.Set(true);   // start available so the flip below actually flips
    var source = _create(gate);

    gate.Set(false);   // tighten
    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromMilliseconds(500));

    gate.Set(true);    // relax back
    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromMilliseconds(5_000))
      .Because("when NOTIFY recovers the pull source returns to relaxed cadence — the doorbell path carries latency");
  }

  [Test]
  public async Task NoGateInjected_KeepsCtorIntervalAsync() {
    // Without a gate, the source stays at its ctor interval — no dynamic tightening.
    var opts = new WhizbangNotificationOptions { DirectConnectionString = "Host=fake;Database=fake;Username=u;Password=p" };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "utest-svc", "utest-host", processId: 1);
    var source = new PgOutboxWorkAvailablePollSource(
      TimeProvider.System, Options.Create(opts), cfg, instance,
      NullLogger<PgOutboxWorkAvailablePollSource>.Instance);

    await Assert.That(source.Interval).IsEqualTo(TimeSpan.FromMilliseconds(5_000));
  }
}
