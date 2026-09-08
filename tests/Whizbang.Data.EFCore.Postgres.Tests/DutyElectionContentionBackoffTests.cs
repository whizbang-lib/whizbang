using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A duty elector that finds its duty held by another instance stops asking the database for a
/// while. Each contended attempt opens a suppression window (two seconds, doubling to the polling
/// fallback interval) during which attempts are answered from memory as Contended; a grant closes
/// it. On an idle fleet the non-holders' retry loops were a steady stream of advisory-lock probes
/// with nothing to learn. The clock is injected so the windows are proven without waiting.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDutyElector.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/DutyContentionBackoff.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class DutyElectionContentionBackoffTests : EFCoreTestBase {

  private sealed class _pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "duty-svc";
    public string HostName => "duty-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private PgDutyElector _electorFor(_pod pod, TimeProvider? clock = null, ProbeCadenceMetrics? metrics = null) => new(
    Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString, PollingFallbackInterval = TimeSpan.FromSeconds(30) }),
    new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
    pod,
    NullLogger<PgDutyElector>.Instance,
    timeProvider: clock,
    probeMetrics: metrics);

  private async Task _joinFleetAsync(_pod pod, CancellationToken ct) {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
  }

  [Test]
  [Timeout(120000)]
  public async Task SecondAttemptInsideTheWindow_IsAnsweredFromMemoryAsync(CancellationToken cancellationToken) {
    var holder = new _pod();
    var contender = new _pod();
    await _joinFleetAsync(holder, cancellationToken);
    await _joinFleetAsync(contender, cancellationToken);
    var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    using var factory = new RecordingMeterFactory();
    var metrics = new ProbeCadenceMetrics(new WhizbangMetrics(factory));
    var elector = _electorFor(contender, clock, metrics);

    await using var grant = (await _electorFor(holder).TryAcquireAsync("backoff-duty", cancellationToken)).Grant;
    await Assert.That(grant).IsNotNull().Because("precondition: the holder holds");

    var first = await elector.TryAcquireAsync("backoff-duty", cancellationToken);
    var second = await elector.TryAcquireAsync("backoff-duty", cancellationToken);

    await Assert.That(first.Refusal).IsEqualTo(DutyRefusal.Contended)
      .Because("the first attempt reaches the database and finds the holder");
    await Assert.That(first.Detail!).DoesNotContain("backing off");
    await Assert.That(second.Refusal).IsEqualTo(DutyRefusal.Contended)
      .Because("the answer is the same, so the caller's retry logic is unchanged");
    await Assert.That(second.Detail!).Contains("backing off")
      .Because("the detail says the attempt never left the process, so an operator reading a startup report knows why it was cheap");
    await Assert.That(elector.IsBackingOff("backoff-duty", out var remaining)).IsTrue();
    await Assert.That(remaining).IsEqualTo(TimeSpan.FromSeconds(2))
      .Because("the first window is the floor and no time has passed on the injected clock");
    await Assert.That(ProbeMeterTotals.Suppressed(factory)).IsEqualTo(1)
      .Because("one attempt was answered from memory and the meter says so");
  }

  [Test]
  [Timeout(120000)]
  public async Task WindowExpiry_ReachesTheDatabaseAgainAndDoublesOnContinuedContentionAsync(CancellationToken cancellationToken) {
    var holder = new _pod();
    var contender = new _pod();
    await _joinFleetAsync(holder, cancellationToken);
    await _joinFleetAsync(contender, cancellationToken);
    var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var elector = _electorFor(contender, clock);
    await using var grant = (await _electorFor(holder).TryAcquireAsync("backoff-duty-2", cancellationToken)).Grant;

    _ = await elector.TryAcquireAsync("backoff-duty-2", cancellationToken);   // real, contended: window 2 s
    clock.Advance(TimeSpan.FromSeconds(2));
    await Assert.That(elector.IsBackingOff("backoff-duty-2", out _)).IsFalse()
      .Because("the window has closed; the next attempt must be a real one");

    var real = await elector.TryAcquireAsync("backoff-duty-2", cancellationToken);   // real, still contended: window 4 s

    await Assert.That(real.Detail!).DoesNotContain("backing off").Because("this attempt went to the database");
    await Assert.That(elector.IsBackingOff("backoff-duty-2", out var remaining)).IsTrue();
    await Assert.That(remaining).IsEqualTo(TimeSpan.FromSeconds(4))
      .Because("a second consecutive contention doubles the window");
  }

  [Test]
  [Timeout(120000)]
  public async Task HolderReleases_TheNextRealAttemptWinsAndClosesTheWindowAsync(CancellationToken cancellationToken) {
    var holder = new _pod();
    var contender = new _pod();
    await _joinFleetAsync(holder, cancellationToken);
    await _joinFleetAsync(contender, cancellationToken);
    var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var elector = _electorFor(contender, clock);
    var grant = (await _electorFor(holder).TryAcquireAsync("backoff-duty-3", cancellationToken)).Grant;
    _ = await elector.TryAcquireAsync("backoff-duty-3", cancellationToken);   // window 2 s

    await grant!.DisposeAsync();
    var duringWindow = await elector.TryAcquireAsync("backoff-duty-3", cancellationToken);
    clock.Advance(TimeSpan.FromSeconds(2));
    var afterWindow = await elector.TryAcquireAsync("backoff-duty-3", cancellationToken);

    await Assert.That(duringWindow.Refusal).IsEqualTo(DutyRefusal.Contended)
      .Because("the trade: a release inside the window is seen at the window's end, at most one polling fallback interval later");
    await Assert.That(afterWindow.Grant).IsNotNull()
      .Because("the real attempt after the window takes the released duty");
    await Assert.That(elector.IsBackingOff("backoff-duty-3", out _)).IsFalse()
      .Because("a grant closes the window and forgets the streak");
    await afterWindow.Grant!.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task Windows_AreKeptPerDutyAsync(CancellationToken cancellationToken) {
    var holder = new _pod();
    var contender = new _pod();
    await _joinFleetAsync(holder, cancellationToken);
    await _joinFleetAsync(contender, cancellationToken);
    var elector = _electorFor(contender, new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    await using var grant = (await _electorFor(holder).TryAcquireAsync("backoff-duty-4a", cancellationToken)).Grant;

    _ = await elector.TryAcquireAsync("backoff-duty-4a", cancellationToken);
    var other = await elector.TryAcquireAsync("backoff-duty-4b", cancellationToken);

    await Assert.That(elector.IsBackingOff("backoff-duty-4a", out _)).IsTrue();
    await Assert.That(elector.IsBackingOff("backoff-duty-4b", out _)).IsFalse()
      .Because("contention on one duty says nothing about another");
    await Assert.That(other.Grant).IsNotNull();
    await other.Grant!.DisposeAsync();
  }

  [Test]
  public async Task IsBackingOff_UnknownDuty_IsFalseAsync() {
    var elector = _electorFor(new _pod());

    await Assert.That(elector.IsBackingOff("never-attempted", out var remaining)).IsFalse();
    await Assert.That(remaining).IsEqualTo(TimeSpan.Zero);
    await Assert.That(() => elector.IsBackingOff("", out _)).Throws<ArgumentException>();
  }

  [Test]
  public async Task ContentionBackoffCeiling_IsThePollingFallbackIntervalAsync() {
    var elector = _electorFor(new _pod());

    await Assert.That(elector.ContentionBackoffCeiling).IsEqualTo(TimeSpan.FromSeconds(30))
      .Because("the ceiling is an option that already exists; no new knob");
    await Assert.That(PgDutyElector.ContentionBackoffFloor).IsEqualTo(TimeSpan.FromSeconds(2));
  }

  /// <summary>An isolated meter factory so the listener can filter by meter identity.</summary>
  private sealed class RecordingMeterFactory : System.Diagnostics.Metrics.IMeterFactory {
    public List<System.Diagnostics.Metrics.Meter> CreatedMeters { get; } = [];
    public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options) {
      var meter = new System.Diagnostics.Metrics.Meter(options);
      CreatedMeters.Add(meter);
      return meter;
    }
    public void Dispose() {
      foreach (var meter in CreatedMeters) { meter.Dispose(); }
    }
  }

  /// <summary>Reads the suppressed-attempt total from an isolated meter factory.</summary>
  private static class ProbeMeterTotals {
    public static long Suppressed(RecordingMeterFactory factory) {
      long total = 0;
      var meter = factory.CreatedMeters[0];
      using var listener = new System.Diagnostics.Metrics.MeterListener {
        InstrumentPublished = (instrument, l) => {
          if (instrument.Meter == meter && instrument.Name == "whizbang.probes.suppressed_duty_attempts") {
            l.EnableMeasurementEvents(instrument);
          }
        }
      };
      listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref total, value));
      listener.Start();
      listener.RecordObservableInstruments();
      return Interlocked.Read(ref total);
    }
  }
}
