// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Branch backfill for <see cref="SignalBusHostedService"/>: built by hand with no options it runs
/// on the defaults, and the self-test probe targets this instance's own channel when it has an
/// identity and falls back to broadcast when it does not.
/// </summary>
public class SignalBusHostedServiceBranchCoverageTests {

  [Test]
  [Timeout(30000)]
  public async Task Probe_NoOptionsSupplied_RunsOnTheDefaultsAndVerifiesTheRouteAsync(CancellationToken testToken) {
    var transport = new RecordingLoopbackTransport();
    var liveness = new SignalBusLivenessState();
    using var hosted = _hosted(transport, liveness, new FixedInstanceProvider(Guid.NewGuid()), options: null!);

    await hosted.StartAsync(CancellationToken.None);
    var verified = await liveness.FirstProbe.WaitAsync(TimeSpan.FromSeconds(20), testToken);
    await hosted.StopAsync(CancellationToken.None);

    await Assert.That(verified).IsTrue()
      .Because("with no options supplied the service falls back to the default options and still runs its self-test");
  }

  [Test]
  [Timeout(30000)]
  public async Task Probe_InstanceWithoutIdentity_TargetsBroadcastAsync(CancellationToken testToken) {
    var transport = new RecordingLoopbackTransport();
    var liveness = new SignalBusLivenessState();
    using var hosted = _hosted(transport, liveness, new FixedInstanceProvider(Guid.Empty), Options.Create(new SignalBusOptions()));

    await hosted.StartAsync(CancellationToken.None);
    _ = await liveness.FirstProbe.WaitAsync(TimeSpan.FromSeconds(20), testToken);
    await hosted.StopAsync(CancellationToken.None);

    await Assert.That(transport.Targets[0].Kind).IsEqualTo(SignalTargetKind.Broadcast)
      .Because("with no instance identity there is no own channel, so the self-loopback goes out as a broadcast");
  }

  [Test]
  [Timeout(30000)]
  public async Task Probe_InstanceWithIdentity_TargetsItsOwnChannelAsync(CancellationToken testToken) {
    var instanceId = Guid.NewGuid();
    var transport = new RecordingLoopbackTransport();
    var liveness = new SignalBusLivenessState();
    using var hosted = _hosted(transport, liveness, new FixedInstanceProvider(instanceId), Options.Create(new SignalBusOptions()));

    await hosted.StartAsync(CancellationToken.None);
    _ = await liveness.FirstProbe.WaitAsync(TimeSpan.FromSeconds(20), testToken);
    await hosted.StopAsync(CancellationToken.None);

    await Assert.That(transport.Targets[0].Kind).IsEqualTo(SignalTargetKind.Instance)
      .Because("in a fleet only this instance must ring, so the probe targets its own channel");
    await Assert.That(transport.Targets[0].InstanceId).IsEqualTo(instanceId);
  }

  /// <summary>
  /// A service the host stops before it starts never probes: the loop sees the stop already requested
  /// and ends before its first iteration, leaving the route unverified rather than racing the shutdown.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task Probe_StoppedBeforeItStarts_NeverProbesAsync(CancellationToken testToken) {
    var transport = new RecordingLoopbackTransport();
    var liveness = new SignalBusLivenessState();
    using var hosted = _hosted(transport, liveness, new FixedInstanceProvider(Guid.NewGuid()), Options.Create(new SignalBusOptions()));

    await hosted.StopAsync(CancellationToken.None);
    await hosted.StartAsync(CancellationToken.None);
    await hosted.StopAsync(testToken);   // waits for the loop, which has nothing to do

    await Assert.That(transport.Targets).IsEmpty()
      .Because("the stop was requested before the loop began, so no probe was published");
    await Assert.That(liveness.FirstProbe.IsCompleted).IsFalse()
      .Because("no probe result was ever recorded");
  }

  private static SignalBusHostedService _hosted(
      RecordingLoopbackTransport transport, SignalBusLivenessState liveness,
      IServiceInstanceProvider instanceProvider, IOptions<SignalBusOptions> options) =>
    new(
      new SignalBus([transport], []),
      liveness,
      [transport],
      NullLogger<SignalBusHostedService>.Instance,
      instanceProvider,
      options);

  /// <summary>Delivers every published signal straight back to the bus and records its target.</summary>
  private sealed class RecordingLoopbackTransport : ISignalTransport {
    private readonly List<SignalTarget> _targets = [];
    private ISignalSink? _sink;

    public IReadOnlyList<SignalTarget> Targets {
      get {
        lock (_targets) {
          return [.. _targets];
        }
      }
    }

    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) {
      _sink = sink;
      return Task.CompletedTask;
    }

    public async ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
        where TSignal : ISignal {
      lock (_targets) {
        _targets.Add(target);
      }
      await _sink!.ReceiveAsync(signal, cancellationToken);
    }
  }

  private sealed class FixedInstanceProvider(Guid instanceId) : IServiceInstanceProvider {
    public Guid InstanceId => instanceId;
    public string ServiceName => "TestService";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }
}
