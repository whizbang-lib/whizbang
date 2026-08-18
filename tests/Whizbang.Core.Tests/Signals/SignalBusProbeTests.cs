using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Locks the wire-route self-test contract (issue #505 layer 2): the hosted bus probes every
/// registered transport with a loopback <see cref="SignalBusProbeSignal"/> and marks the
/// <see cref="SignalBusLivenessState"/> verdict — a transport that cannot deliver its own probe
/// degrades the signal-bus component instead of silently falling back to polling. A socket-level
/// self-test cannot catch this class: the connection can be healthy while the routing layer drops
/// every doorbell, which is exactly how the production gap stayed invisible.
/// </summary>
public class SignalBusProbeTests {
  /// <summary>The #505 failure mode in miniature: a transport that never delivers anything.</summary>
  private sealed class DeadTransport : ISignalTransport {
    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;
  }

  private static async Task _startAllHostedServicesAsync(IServiceProvider provider) {
    foreach (var hosted in provider.GetServices<IHostedService>()) {
      await hosted.StartAsync(CancellationToken.None);
    }
  }

  [Test]
  [Timeout(30_000)]
  public async Task HostedStart_ProbeVerifiesWireRoute_ViaInMemoryAsync(CancellationToken cancellationToken) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangSignalBus();

    await using var provider = services.BuildServiceProvider();
    await _startAllHostedServicesAsync(provider);
    var state = provider.GetRequiredService<SignalBusLivenessState>();

    var verified = await state.FirstProbe.WaitAsync(cancellationToken);

    await Assert.That(verified).IsTrue();
    await Assert.That(state.WireRouteVerified).IsEqualTo(true);
    await Assert.That(state.Report().State).IsEqualTo(ComponentState.Operational);
  }

  [Test]
  [Timeout(30_000)]
  public async Task HostedStart_DeadTransport_ProbeMarksWireRouteFailedAsync(CancellationToken cancellationToken) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangSignalBus();
    services.AddSingleton<ISignalTransport>(new DeadTransport());
    services.Configure<SignalBusOptions>(o => o.ProbeTimeoutMilliseconds = 50);

    await using var provider = services.BuildServiceProvider();
    await _startAllHostedServicesAsync(provider);
    var state = provider.GetRequiredService<SignalBusLivenessState>();

    var verified = await state.FirstProbe.WaitAsync(cancellationToken);

    await Assert.That(verified).IsFalse();
    await Assert.That(state.WireRouteVerified).IsEqualTo(false);
    var report = state.Report();
    await Assert.That(report.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(report.Detail!).Contains("DeadTransport");
  }

  /// <summary>Delivers its first probe (loops back like the in-memory transport), then goes dead —
  /// the mid-run listener death the periodic re-probe exists to catch.</summary>
  private sealed class DiesAfterFirstProbeTransport : ISignalTransport {
    private ISignalSink? _sink;
    private int _published;

    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) {
      _sink = sink;
      return Task.CompletedTask;
    }

    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      return Interlocked.Increment(ref _published) == 1 && _sink is not null
        ? _sink.ReceiveAsync(signal, cancellationToken)
        : ValueTask.CompletedTask;
    }
  }

  private sealed class ThrowingTransport : ISignalTransport {
    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
      where TSignal : ISignal => throw new InvalidOperationException("wire exploded");
  }

  [Test]
  [Timeout(30_000)]
  public async Task HostedStart_ThrowingTransport_ProbeMarksFailed_LoopSurvivesAsync(CancellationToken cancellationToken) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangSignalBus();
    services.AddSingleton<ISignalTransport>(new ThrowingTransport());

    await using var provider = services.BuildServiceProvider();
    await _startAllHostedServicesAsync(provider);
    var state = provider.GetRequiredService<SignalBusLivenessState>();

    var verified = await state.FirstProbe.WaitAsync(cancellationToken);

    await Assert.That(verified).IsFalse()
      .Because("a probe that throws is an unverified route, never a silent pass");
    await Assert.That(state.Report().State).IsEqualTo(ComponentState.Degraded);
  }

  [Test]
  [Timeout(30_000)]
  public async Task ReProbe_TransportDiesAfterFirstPass_LaterProbeDegradesAsync(CancellationToken cancellationToken) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangSignalBus();
    services.AddSingleton<ISignalTransport>(new DiesAfterFirstProbeTransport());
    services.Configure<SignalBusOptions>(o => {
      o.ProbeTimeoutMilliseconds = 50;
      o.ReProbeIntervalMilliseconds = 30;
    });

    await using var provider = services.BuildServiceProvider();
    var state = provider.GetRequiredService<SignalBusLivenessState>();
    var degraded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    state.ProbeCompleted += verdict => {
      if (!verdict) {
        degraded.TrySetResult();
      }
    };

    await _startAllHostedServicesAsync(provider);

    var firstVerdict = await state.FirstProbe.WaitAsync(cancellationToken);
    await Assert.That(firstVerdict).IsTrue()
      .Because("the transport delivers its first probe — the route starts healthy");

    await degraded.Task.WaitAsync(cancellationToken);
    await Assert.That(state.WireRouteVerified).IsEqualTo(false)
      .Because("the periodic re-probe must catch a listener that died after startup");
    await Assert.That(state.Report().State).IsEqualTo(ComponentState.Degraded);
  }

  [Test]
  public async Task Report_ConsecutiveMissedDoorbells_DegradesAtThreshold_DoorbellWakeResetsAsync() {
    var state = new SignalBusLivenessState { MissedDoorbellThreshold = 3 };
    state.MarkProbeResult(success: true, at: DateTimeOffset.UnixEpoch);

    state.RecordMissedDoorbell();
    state.RecordMissedDoorbell();
    await Assert.That(state.Report().State).IsEqualTo(ComponentState.Operational);

    state.RecordMissedDoorbell();
    await Assert.That(state.ConsecutiveMissedDoorbells).IsEqualTo(3);
    await Assert.That(state.Report().State).IsEqualTo(ComponentState.Degraded);

    state.RecordDoorbellWake();
    await Assert.That(state.ConsecutiveMissedDoorbells).IsEqualTo(0);
    await Assert.That(state.Report().State).IsEqualTo(ComponentState.Operational);
  }
}
