using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The BACKLOG-AGE DUTY (topology arc phase 10): a cheap scheduled peek of subscription depth and
/// oldest-enqueue age per class, degrading the managed-health component with the ENTITY NAMED.
/// <para>
/// This closes the third structural lesson of the incident that motivated the arc: the machinery
/// that consumed a whole namespace quota logged nothing while it was healthy, and the subscription
/// backlogs it created were <em>hostage, not poison</em> — they drained to zero untouched once the
/// churn stopped. Depth alone cannot tell those apart; AGE can. A deep-but-young backlog is a
/// burst, a shallow-but-ancient one is a stuck consumer, and only the second needs an operator.
/// </para>
/// <para>
/// Peeking is admin-plane only and per entity per interval, so the duty can never become the kind
/// of idle churn it exists to detect.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/BacklogAgeWorker.cs</code-under-test>
[Category("Core")]
[Category("Observability")]
public class BacklogAgeDutyTests {
  [Test]
  public async Task PeekOnce_AgedBacklog_DegradesHealthNamingTheEntityAsync() {
    var state = new BacklogAgeState();
    var worker = _worker(state, new RecordingPeek([
      new BacklogSample("inbox.orders", Depth: 42, OldestAge: TimeSpan.FromHours(2)),
    ]), ageThreshold: TimeSpan.FromMinutes(15));

    await worker.PeekOnceAsync(CancellationToken.None);

    var health = await new BacklogAgeHealthSource(state).ReportAsync(CancellationToken.None);
    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(health.Detail).Contains("inbox.orders")
      .Because("'a backlog is old somewhere' is not actionable — the entity name is the whole "
             + "value of the signal");
  }

  [Test]
  public async Task PeekOnce_FreshBacklog_StaysOperationalAsync() {
    // Depth alone must NEVER degrade: a deep, young backlog is a burst draining normally, and
    // alarming on it is how a useful signal becomes noise operators learn to ignore.
    var state = new BacklogAgeState();
    var worker = _worker(state, new RecordingPeek([
      new BacklogSample("inbox.orders", Depth: 16_642, OldestAge: TimeSpan.FromSeconds(30)),
    ]), ageThreshold: TimeSpan.FromMinutes(15));

    await worker.PeekOnceAsync(CancellationToken.None);

    var health = await new BacklogAgeHealthSource(state).ReportAsync(CancellationToken.None);
    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
  }

  [Test]
  public async Task PeekOnce_EmptyEntity_ClearsAPreviousFindingAsync() {
    // The signal must go DOWN when things heal — a latched alarm is indistinguishable from a
    // stuck one after the first hour.
    var state = new BacklogAgeState();
    var peek = new RecordingPeek([new BacklogSample("inbox.orders", 42, TimeSpan.FromHours(2))]);
    var worker = _worker(state, peek, ageThreshold: TimeSpan.FromMinutes(15));
    await worker.PeekOnceAsync(CancellationToken.None);

    peek.Samples = [new BacklogSample("inbox.orders", 0, OldestAge: null)];
    await worker.PeekOnceAsync(CancellationToken.None);

    var health = await new BacklogAgeHealthSource(state).ReportAsync(CancellationToken.None);
    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
  }

  [Test]
  public async Task PeekOnce_UnknownAge_DoesNotDegradeButIsReportedAsCapabilityGapAsync() {
    // Capability honesty, exactly as phase 8.5 handled a missing first-enqueue timestamp: a
    // transport that cannot supply an age must say so, never go silently inert and never invent
    // an alarm from depth.
    var state = new BacklogAgeState();
    var worker = _worker(state, new RecordingPeek([
      new BacklogSample("orders-queue", Depth: 500, OldestAge: null),
    ]), ageThreshold: TimeSpan.FromMinutes(15));

    await worker.PeekOnceAsync(CancellationToken.None);

    var health = await new BacklogAgeHealthSource(state).ReportAsync(CancellationToken.None);
    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
    await Assert.That(state.HasUnknownAgeSurface).IsTrue();
  }

  [Test]
  public async Task PeekOnce_PerClassAndPerNamespace_AreCarriedThroughToTheGaugesAsync() {
    var metrics = _metrics();
    var state = new BacklogAgeState();
    var worker = _worker(state, new RecordingPeek([
      new BacklogSample("inbox.whizbang.control", 3, TimeSpan.FromSeconds(5)) {
        TransportNamespace = "control",
        TrafficClass = "sys-control",
        Transport = "asb",
      },
    ]), metrics: metrics);

    await worker.PeekOnceAsync(CancellationToken.None);

    var sample = metrics.GetBacklogForTest("inbox.whizbang.control");
    await Assert.That(sample).IsNotNull();
    await Assert.That(sample!.Value.Depth).IsEqualTo(3L);
    await Assert.That(sample.Value.TrafficClass).IsEqualTo("sys-control");
    await Assert.That(sample.Value.TransportNamespace).IsEqualTo("control");
  }

  [Test]
  public async Task PeekOnce_PeekThrows_DoesNotFaultTheDutyAsync() {
    // A management-plane hiccup must never take down the observer; the next tick retries.
    var state = new BacklogAgeState();
    var worker = _worker(state, new ThrowingPeek());

    await worker.PeekOnceAsync(CancellationToken.None);

    var health = await new BacklogAgeHealthSource(state).ReportAsync(CancellationToken.None);
    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
  }

  [Test]
  public async Task PeekOnce_NoPeekWired_IsAPureNoOpAsync() {
    var state = new BacklogAgeState();
    var worker = new BacklogAgeWorker(
      [], [], Options.Create(new BacklogAgeOptions()), state, _metrics(),
      NullLogger<BacklogAgeWorker>.Instance);

    await worker.PeekOnceAsync(CancellationToken.None);

    await Assert.That(state.HasAgedBacklog).IsFalse();
  }

  [Test]
  public async Task Options_DefaultsAreTheDocumentedPostureAsync() {
    var options = new BacklogAgeOptions();

    await Assert.That(options.Enabled).IsTrue();
    await Assert.That(options.Interval).IsEqualTo(TimeSpan.FromMinutes(1))
      .Because("a peek per entity per minute is a rounding error against a Standard namespace's "
             + "credit pool — the duty must never become the churn it detects");
    await Assert.That(options.AgeThreshold).IsEqualTo(TimeSpan.FromMinutes(15));
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersTheDutyAndItsHealthSourceAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangWorkers();
    using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetService<BacklogAgeState>()).IsNotNull();
    await Assert.That(provider.GetService<BacklogAgeMetrics>()).IsNotNull();
    await Assert.That(provider.GetServices<IWhizbangHealthSource>()
      .Any(s => s.Component == "backlog")).IsTrue();
  }

  [Test]
  public async Task PeekOnce_PublishesTheOpsRateGaugePerNamespaceAsync() {
    // The incident's signature: the receive machinery demanded thousands of operations per second
    // against a ~1,000/sec pool WHILE IDLE, and the only witness was the provider's billing meter.
    // Per NAMESPACE, never summed — each pool is its own budget.
    var metrics = _metrics();
    var worker = _worker(new BacklogAgeState(), new RecordingPeek([]), metrics: metrics,
      opsRates: new StubOpsRateSource([
        new TrafficClassOpsRate("default", TrafficClasses.DOMAIN, 12.5),
        new TrafficClassOpsRate("control", "sys-control", 0.4),
      ]));

    await worker.PeekOnceAsync(CancellationToken.None);

    var control = metrics.GetOpsRateForTest("control");
    await Assert.That(control).IsNotNull();
    await Assert.That(control!.Value.OpsPerSecond).IsEqualTo(0.4);
    await Assert.That(control.Value.TrafficClass).IsEqualTo("sys-control");
    await Assert.That(metrics.GetOpsRateForTest("default")!.Value.OpsPerSecond).IsEqualTo(12.5);
  }

  private sealed class StubOpsRateSource(IReadOnlyList<TrafficClassOpsRate> rates)
      : ITrafficClassOpsRateSource {
    public string TransportName => "test";
    public IReadOnlyList<TrafficClassOpsRate> Project() => rates;
  }

  private static BacklogAgeMetrics _metrics() => new(new WhizbangMetrics());

  private static BacklogAgeWorker _worker(
      BacklogAgeState state,
      IBacklogPeek peek,
      TimeSpan? ageThreshold = null,
      BacklogAgeMetrics? metrics = null,
      ITrafficClassOpsRateSource? opsRates = null) =>
    new([peek],
      opsRates is null ? [] : [opsRates],
      Options.Create(new BacklogAgeOptions {
        AgeThreshold = ageThreshold ?? TimeSpan.FromMinutes(15),
      }),
      state,
      metrics ?? _metrics(),
      NullLogger<BacklogAgeWorker>.Instance);

  private sealed class RecordingPeek(IReadOnlyList<BacklogSample> samples) : IBacklogPeek {
    public IReadOnlyList<BacklogSample> Samples { get; set; } = samples;
    public string TransportName => "test";

    public Task<IReadOnlyList<BacklogSample>> PeekAsync(CancellationToken cancellationToken) =>
      Task.FromResult(Samples);
  }

  private sealed class ThrowingPeek : IBacklogPeek {
    public string TransportName => "test";

    public Task<IReadOnlyList<BacklogSample>> PeekAsync(CancellationToken cancellationToken) =>
      Task.FromException<IReadOnlyList<BacklogSample>>(new InvalidOperationException("management plane down"));
  }

  [Test]
  [Timeout(30000)]
  public async Task WhenDisabled_TheDutyNeverTouchesTheManagementPlaneAsync(
      CancellationToken testToken) {
    // Disabled has to mean silent, not merely "reports nothing". Each peek is a management
    // operation against the broker, and a duty that kept issuing them while switched off would
    // spend a namespace's request budget on readings nobody can see.
    var peek = new SignallingPeek([]);
    var worker = new BacklogAgeWorker(
      [peek], [],
      Options.Create(new BacklogAgeOptions { Enabled = false }),
      new BacklogAgeState(), _metrics(), NullLogger<BacklogAgeWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.ExecuteTask!.WaitAsync(testToken);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(peek.Peeks).IsEqualTo(0)
      .Because("a disabled duty must issue no management operations at all, not just withhold "
             + "the gauges it would have produced from them");
  }

  [Test]
  [Timeout(30000)]
  public async Task WithNothingWired_TheDutyExitsInsteadOfTickingForeverAsync(
      CancellationToken testToken) {
    // No transports and no ops-rate sources means every tick would peek nothing and publish
    // nothing. Parking a timer on that is a hosted service that looks alive and observes
    // nothing -- indistinguishable, from the outside, from one that is working.
    var worker = new BacklogAgeWorker(
      [], [],
      Options.Create(new BacklogAgeOptions()),
      new BacklogAgeState(), _metrics(), NullLogger<BacklogAgeWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.ExecuteTask!.WaitAsync(testToken);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("with nothing to observe the duty returns rather than holding a timer open");
  }

  [Test]
  [Timeout(30000)]
  public async Task TheDutyKeepsPeeking_NotOnlyOnceAsync(CancellationToken testToken) {
    // A backlog-age detector that peeks once and stops is the exact failure this arc keeps
    // re-learning: it reports healthy forever, because the reading that would have degraded
    // health is never taken. Only a second peek proves the cadence, not just the first pass.
    var peek = new SignallingPeek([]);
    var worker = new BacklogAgeWorker(
      [peek], [],
      Options.Create(new BacklogAgeOptions { Interval = TimeSpan.FromMilliseconds(20) }),
      new BacklogAgeState(), _metrics(), NullLogger<BacklogAgeWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await peek.SecondPeek.WaitAsync(testToken);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("shutdown arrives while the loop is parked on its timer, and a duty that faulted "
             + "there would turn every ordinary deploy into a reported crash");
  }

  /// <summary>A peek that counts passes and reports when the duty has run more than one.</summary>
  private sealed class SignallingPeek(IReadOnlyList<BacklogSample> samples) : IBacklogPeek {
    private readonly TaskCompletionSource _secondPeek =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _peeks;

    public string TransportName => "test";

    /// <summary>Management operations issued so far.</summary>
    public int Peeks => Volatile.Read(ref _peeks);

    /// <summary>Completes once the duty has peeked a second time, proving the loop repeats.</summary>
    public Task SecondPeek => _secondPeek.Task;

    public Task<IReadOnlyList<BacklogSample>> PeekAsync(CancellationToken cancellationToken) {
      if (Interlocked.Increment(ref _peeks) >= 2) {
        _secondPeek.TrySetResult();
      }
      return Task.FromResult(samples);
    }
  }
}
