#pragma warning disable CA1707 // Test method names can contain underscores

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The managed-health surface of the idle ops-rate self-check: the transport component reports
/// DEGRADED (never faulted — the transport still serves) while the projected idle broker-op
/// rate exceeds the warning threshold, and recovers the moment a re-projection lands back under
/// it — which adaptive acceptor decay can do without any configuration change. This closes the
/// Phase-1 delta where the self-check only logged.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AsbOpsRateHealthSource.cs</code-under-test>
[Timeout(10_000)]
public class AsbOpsRateHealthSourceTests {
  private static readonly TimeSpan _window = TimeSpan.FromSeconds(30);

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client, FakeTimeProvider Time)
      _createTransport(AzureServiceBusOptions options) {
    var client = new RaisableServiceBusClient();
    var time = new FakeTimeProvider();
    var transport = new AzureServiceBusTransport(
      client, new JsonSerializerOptions(), options, new RecordingTransportLogger(), timeProvider: time);
    return (transport, client, time);
  }

  private static Task<ISubscription> _subscribeAsync(AzureServiceBusTransport transport) =>
    transport.SubscribeBatchAsync(
      (batch, ct) => Task.CompletedTask,
      new TransportDestination("inbox") { RoutingKey = "health-sub" },
      new TransportBatchOptions());

  private static ProcessSessionEventArgs _sessionEventArgs() =>
    new(new RecordingTransportSessionReceiver(), CancellationToken.None);

  [Test]
  public async Task Component_IsTheTransportComponentAsync() {
    var (transport, _, _) = _createTransport(new AzureServiceBusOptions());
    var source = new AsbOpsRateHealthSource(transport);

    await Assert.That(source.Component).IsEqualTo("transport")
      .Because("the spec degrades the transport's managed-health component — the idle churn IS transport spend");
  }

  [Test]
  public async Task Report_NonAsbTransport_ReportsOperationalAsync() {
    var source = new AsbOpsRateHealthSource(new NonAsbTransportStub());

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Operational)
      .Because("the self-check only understands ASB session economics — any other transport is out of scope, not degraded");
  }

  [Test]
  public async Task Report_BeforeAnySubscription_ReportsOperationalAsync() {
    var (transport, _, _) = _createTransport(new AzureServiceBusOptions());
    var source = new AsbOpsRateHealthSource(transport);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Operational)
      .Because("no session subscription means no idle accept churn to project");
  }

  [Test]
  public async Task Report_ProjectionExceedsThreshold_ReportsDegradedWithDetailAsync() {
    // Legacy standing army: 200 acceptors / 1s = 200 idle ops/sec over the 100/sec threshold.
    var (transport, _, _) = _createTransport(new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = true,
      EnableAdaptiveAcceptors = false,
      MaxConcurrentSessions = 200,
      SessionIdleTimeout = TimeSpan.FromSeconds(1),
    });
    await transport.InitializeAsync();
    await _subscribeAsync(transport);
    var source = new AsbOpsRateHealthSource(transport);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded)
      .Because("idle churn approaching a Standard pool's shared quota must surface on the health plane, not just in a log line");
    await Assert.That(health.Detail ?? string.Empty).Contains("200")
      .Because("the detail must carry the projected rate so 'why degraded' is answerable from the probe");
  }

  [Test]
  public async Task Report_SelfCheckDisabled_ReportsOperationalAsync() {
    var (transport, _, _) = _createTransport(new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = true,
      EnableAdaptiveAcceptors = false,
      MaxConcurrentSessions = 200,
      SessionIdleTimeout = TimeSpan.FromSeconds(1),
      EnableOpsRateSelfCheck = false,
    });
    await transport.InitializeAsync();
    await _subscribeAsync(transport);
    var source = new AsbOpsRateHealthSource(transport);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Operational)
      .Because("the killswitch silences the whole self-check — health degradation included");
  }

  [Test]
  public async Task Report_AdaptiveGrowthThenDecay_DegradesAndRecoversAsync() {
    // Floor 4 / ceiling 16 / 1s idle timeout / threshold 5: the floor projects 4 (healthy),
    // one growth step projects 8 (degraded), and one decay step recovers without any
    // configuration change — the projection is recomputed as the pool resizes.
    var (transport, client, time) = _createTransport(new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = true,
      AcceptorFloor = 4,
      MaxConcurrentSessions = 16,
      AcceptorEvaluationInterval = _window,
      SessionIdleTimeout = TimeSpan.FromSeconds(1),
      OpsRateWarningThresholdPerSecond = 5,
    });
    await transport.InitializeAsync();
    await _subscribeAsync(transport);
    var source = new AsbOpsRateHealthSource(transport);
    var processor = client.LastSessionProcessor!;

    var atFloor = await source.ReportAsync(CancellationToken.None);
    await Assert.That(atFloor.State).IsEqualTo(ComponentState.Operational)
      .Because("the 4-slot floor projects 4 idle ops/sec — under the 5/sec threshold");

    // Grow: 4 active on 4 slots for one window, then one more session event applies the step.
    for (var i = 0; i < 4; i++) {
      await processor.RaiseSessionInitializingAsync(_sessionEventArgs());
    }
    time.Advance(_window);
    await processor.RaiseSessionInitializingAsync(_sessionEventArgs());

    var afterGrowth = await source.ReportAsync(CancellationToken.None);
    await Assert.That(afterGrowth.State).IsEqualTo(ComponentState.Degraded)
      .Because("the grown 8-slot pool projects 8 idle ops/sec — the health surface must track the pool actually held");

    // Decay: drain to 0 active, hold quiet for a window, one more event applies the step.
    for (var i = 0; i < 5; i++) {
      await processor.RaiseSessionClosingAsync(_sessionEventArgs());
    }
    time.Advance(_window);
    await processor.RaiseSessionInitializingAsync(_sessionEventArgs());

    var afterDecay = await source.ReportAsync(CancellationToken.None);
    await Assert.That(afterDecay.State).IsEqualTo(ComponentState.Operational)
      .Because("decay shrank the projection back under the threshold — recovery must not require a redeploy");
  }

  /// <summary>Minimal non-ASB ITransport so the source's type guard is exercised.</summary>
  private sealed class NonAsbTransportStub : ITransport {
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public bool IsInitialized => true;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      ReadOnlyMemory<byte>? preSerializedBytes = null,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
      Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
      TransportDestination destination,
      TransportBatchOptions batchOptions,
      CancellationToken cancellationToken = default) => Task.FromResult<ISubscription>(null!);

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull => Task.FromResult<IMessageEnvelope>(null!);
  }
}
