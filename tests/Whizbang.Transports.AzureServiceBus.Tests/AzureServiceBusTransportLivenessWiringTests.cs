using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Wiring tests for the transport's <see cref="ReceiveLivenessWatchdog"/> integration:
/// construction gating (option + admin client), Track-on-subscribe, RecordActivity on every
/// receive path (batch session, batch non-session, legacy session, legacy non-session),
/// recovery-handler invocation on a detected stall, and disposal.
///
/// Uses the shared raisable client/processor doubles — no broker, no real time.
/// </summary>
[Timeout(10_000)]
public class AzureServiceBusTransportLivenessWiringTests {
  private static readonly JsonSerializerOptions _combinedOptions = JsonContextRegistry.CreateCombinedOptions();
  private static readonly TimeSpan _threshold = TimeSpan.FromMinutes(5);
  private static readonly TimeSpan _pastThreshold = TimeSpan.FromMinutes(6);

  // ===== Construction gating =====

  [Test]
  public async Task Constructor_AdminClientPresentAndWatchdogEnabled_CreatesWatchdogAsync() {
    var (transport, _, _, _) = _createTransport(enableSessions: true);

    await Assert.That(transport.LivenessWatchdog).IsNotNull()
      .Because("with an admin client available the transport can distinguish idle from stalled, so the watchdog is active by default");
  }

  [Test]
  public async Task Constructor_NoAdminClient_DoesNotCreateWatchdogAsync() {
    var client = new RaisableServiceBusClient();
    var transport = new AzureServiceBusTransport(
      client,
      _combinedOptions,
      new AzureServiceBusOptions { AutoProvisionInfrastructure = false },
      NullLogger<AzureServiceBusTransport>.Instance);

    await Assert.That(transport.LivenessWatchdog).IsNull()
      .Because("without an admin client a silent subscription cannot be distinguished from an idle one — silence-only recovery would restart healthy idle services");
  }

  [Test]
  public async Task Constructor_WatchdogDisabled_DoesNotCreateWatchdogAsync() {
    var (transport, _, _, _) = _createTransport(enableSessions: true, enableWatchdog: false);

    await Assert.That(transport.LivenessWatchdog).IsNull();
  }

  // ===== Receive paths record activity; silence past threshold recovers =====

  [Test]
  public async Task BatchSessionReceive_RecordsActivityAndSilenceTriggersRecoveryAsync() {
    var (transport, client, time, recoveryCalls) = _createTransport(enableSessions: true);
    await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, _destination(), new TransportBatchOptions());

    // Silence accrues, then a message arrives: the receive must reset the window.
    time.Advance(_pastThreshold);
    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(
        AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()),
        new RecordingTransportSessionReceiver()));
    await transport.LivenessWatchdog!.ProbeAsync();
    await Assert.That(recoveryCalls.Count).IsEqualTo(0)
      .Because("a message pumped through the session batch path must record receive activity");

    // No further receives: the next sweep past the threshold detects the stall.
    time.Advance(_pastThreshold);
    await transport.LivenessWatchdog.ProbeAsync();
    await Assert.That(recoveryCalls.Count).IsEqualTo(1)
      .Because("silent past the threshold with backlog present must invoke the transport's recovery handler");
  }

  [Test]
  public async Task BatchNonSessionReceive_RecordsActivityAsync() {
    var (transport, client, time, recoveryCalls) = _createTransport(enableSessions: false);
    await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, _destination(), new TransportBatchOptions());

    time.Advance(_pastThreshold);
    await client.LastProcessor!.RaiseMessageAsync(
      AsbTransportTestData.MessageArgs(
        AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()),
        new RecordingTransportReceiver()));
    await transport.LivenessWatchdog!.ProbeAsync();

    await Assert.That(recoveryCalls.Count).IsEqualTo(0)
      .Because("a message enqueued through the non-session batch path must record receive activity");
  }

  [Test]
  public async Task LegacySessionReceive_RecordsActivityAsync() {
    var (transport, client, time, recoveryCalls) = _createTransport(enableSessions: true);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());

    time.Advance(_pastThreshold);
    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(
        AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()),
        new RecordingTransportSessionReceiver()));
    await transport.LivenessWatchdog!.ProbeAsync();

    await Assert.That(recoveryCalls.Count).IsEqualTo(0);
  }

  [Test]
  public async Task LegacyNonSessionReceive_RecordsActivityAsync() {
    var (transport, client, time, recoveryCalls) = _createTransport(enableSessions: false);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());

    time.Advance(_pastThreshold);
    await client.LastProcessor!.RaiseMessageAsync(
      AsbTransportTestData.MessageArgs(
        AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()),
        new RecordingTransportReceiver()));
    await transport.LivenessWatchdog!.ProbeAsync();

    await Assert.That(recoveryCalls.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Subscribe_WithoutReceives_SilencePastThresholdInvokesRecoveryHandlerAsync() {
    var (transport, _, time, recoveryCalls) = _createTransport(enableSessions: true);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());

    time.Advance(_pastThreshold);
    await transport.LivenessWatchdog!.ProbeAsync();

    await Assert.That(recoveryCalls.Count).IsEqualTo(1)
      .Because("Track-on-subscribe is the baseline: a subscription that never receives while backlog exists is stalled from birth");
  }

  // ===== Disposal =====

  [Test]
  public async Task DisposeAsync_WithActiveWatchdog_CompletesAsync() {
    var (transport, _, _, _) = _createTransport(enableSessions: true);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());

    await transport.DisposeAsync();

    // Completing without a hang is the core assertion (class Timeout enforces it):
    // transport disposal must stop the started watchdog loop.
    await Assert.That(transport.LivenessWatchdog).IsNotNull();
  }

  private static TransportDestination _destination(string topic = "liveness-topic") => new(topic, "liveness-sub");

  /// <summary>
  /// Transport wired with a raisable client, a backlog-bearing admin client (5 messages
  /// waiting on every subscription), a FakeTimeProvider, and a counting recovery handler.
  /// Auto-provisioning stays enabled so the admin fake also exercises Track-on-subscribe
  /// through the real provisioning path.
  /// </summary>
  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client, FakeTimeProvider Time, List<int> RecoveryCalls) _createTransport(
    bool enableSessions,
    bool enableWatchdog = true) {
    var client = new RaisableServiceBusClient();
    var time = new FakeTimeProvider();
    var adminClient = new RecordingProvisioningAdminClient {
      ActiveMessageCountResult = 5
    };
    var options = new AzureServiceBusOptions {
      EnableSessions = enableSessions,
      EnableReceiveLivenessWatchdog = enableWatchdog,
      ReceiveLivenessSilenceThreshold = _threshold,
      // Park the periodic loop far beyond any Advance() in these tests: sweeps must
      // happen only through the explicit ProbeAsync calls so assertions are exact.
      ReceiveLivenessProbeInterval = TimeSpan.FromHours(24)
    };
    var transport = new AzureServiceBusTransport(
      client,
      _combinedOptions,
      options,
      NullLogger<AzureServiceBusTransport>.Instance,
      adminClient,
      timeProvider: time);
    var recoveryCalls = new List<int>();
    transport.SetRecoveryHandler(_ => { recoveryCalls.Add(1); return Task.CompletedTask; });
    return (transport, client, time, recoveryCalls);
  }
}
