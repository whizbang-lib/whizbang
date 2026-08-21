#pragma warning disable CA1707 // Test method names can contain underscores

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Transport-side wiring of the adaptive session acceptors: session processors are created at
/// the floor (not the MaxConcurrentSessions ceiling), the governor's grow/decay decisions are
/// applied to the RUNNING processor via the SDK's dynamic UpdateConcurrency, evaluations run on
/// session initialize/close events plus a periodic tick, and disabling the feature reproduces
/// today's standing-concurrency behavior exactly. All time flows through FakeTimeProvider and
/// session demand is driven by raising the SDK's session lifecycle events — no broker, no sleeps.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusTransport.cs</code-under-test>
[Timeout(10_000)]
public class AsbAcceptorAdaptiveWiringTests {
  private static readonly TimeSpan _window = TimeSpan.FromSeconds(30);

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client, FakeTimeProvider Time, RecordingTransportLogger Logger)
      _createTransport(Action<AzureServiceBusOptions>? configure = null) {
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = true,
      AcceptorFloor = 4,
      MaxConcurrentSessions = 200,
      AcceptorEvaluationInterval = _window,
    };
    configure?.Invoke(options);
    var client = new RaisableServiceBusClient();
    var time = new FakeTimeProvider();
    var logger = new RecordingTransportLogger();
    var transport = new AzureServiceBusTransport(
      client, new JsonSerializerOptions(), options, logger, timeProvider: time);
    return (transport, client, time, logger);
  }

  private static Task<ISubscription> _subscribeBatchAsync(AzureServiceBusTransport transport, string topic = "inbox") =>
    transport.SubscribeBatchAsync(
      (batch, ct) => Task.CompletedTask,
      new TransportDestination(topic) { RoutingKey = "adaptive-sub" },
      new TransportBatchOptions());

  private static ProcessSessionEventArgs _sessionEventArgs() =>
    new(new RecordingTransportSessionReceiver(), CancellationToken.None);

  private static async Task _raiseSessionInitializingAsync(RaisableServiceBusClient client, int count) {
    for (var i = 0; i < count; i++) {
      await client.LastSessionProcessor!.RaiseSessionInitializingAsync(_sessionEventArgs());
    }
  }

  private static async Task _raiseSessionClosingAsync(RaisableServiceBusClient client, int count) {
    for (var i = 0; i < count; i++) {
      await client.LastSessionProcessor!.RaiseSessionClosingAsync(_sessionEventArgs());
    }
  }

  [Test]
  public async Task SubscribeBatch_AdaptiveDefault_CreatesTheSessionProcessorAtTheFloorAsync() {
    var (transport, client, _, _) = _createTransport();
    await transport.InitializeAsync();

    await _subscribeBatchAsync(transport);

    await Assert.That(client.LastSessionProcessorOptions!.MaxConcurrentSessions).IsEqualTo(4)
      .Because("adaptive mode starts the acceptor pool at the floor — the 200-slot ceiling is potential, not a standing army");
  }

  [Test]
  public async Task Subscribe_NonBatchSessionPath_AlsoStartsAtTheFloorAsync() {
    var (transport, client, _, _) = _createTransport();
    await transport.InitializeAsync();

    await transport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask,
      new TransportDestination("inbox") { RoutingKey = "adaptive-sub" });

    await Assert.That(client.LastSessionProcessorOptions!.MaxConcurrentSessions).IsEqualTo(4)
      .Because("both session subscribe paths must govern acceptors — a floor on only one path leaves the other a standing army");
  }

  [Test]
  public async Task SubscribeBatch_FloorAboveTheCeiling_UsesTheCeilingAsync() {
    var (transport, client, _, _) = _createTransport(o => o.MaxConcurrentSessions = 2);
    await transport.InitializeAsync();

    await _subscribeBatchAsync(transport);

    await Assert.That(client.LastSessionProcessorOptions!.MaxConcurrentSessions).IsEqualTo(2)
      .Because("MaxConcurrentSessions stays the hard ceiling even against the configured floor");
  }

  [Test]
  public async Task SubscribeBatch_AdaptiveDisabled_KeepsTodaysStandingConcurrencyAsync() {
    var (transport, client, time, _) = _createTransport(o => o.EnableAdaptiveAcceptors = false);
    await transport.InitializeAsync();

    await _subscribeBatchAsync(transport);
    var processor = client.LastSessionProcessor!;
    var initialConcurrency = processor.MaxConcurrentSessions;

    // Session churn + elapsed windows must not touch concurrency when the feature is off.
    await _raiseSessionInitializingAsync(client, 5);
    time.Advance(_window + _window);
    await _raiseSessionClosingAsync(client, 5);

    await Assert.That(client.LastSessionProcessorOptions!.MaxConcurrentSessions).IsEqualTo(200)
      .Because("disabled adaptive acceptors must reproduce today's behavior exactly: a fixed MaxConcurrentSessions standing pool");
    await Assert.That(processor.MaxConcurrentSessions).IsEqualTo(initialConcurrency)
      .Because("no governor may be attached when the feature is disabled");
  }

  [Test]
  public async Task SessionPressure_SustainedForOneWindow_GrowsTheRunningProcessorAsync() {
    var (transport, client, time, _) = _createTransport();
    await transport.InitializeAsync();
    await _subscribeBatchAsync(transport);

    // 4 active sessions on 4 slots = 100% occupancy — pressure stamped at t0.
    await _raiseSessionInitializingAsync(client, 4);
    time.Advance(_window);
    // The next session event triggers an evaluation with the pressure window elapsed.
    await _raiseSessionInitializingAsync(client, 1);

    await Assert.That(client.LastSessionProcessor!.MaxConcurrentSessions).IsEqualTo(8)
      .Because("sustained pressure doubles the RUNNING processor's concurrency via UpdateConcurrency — no stop/recreate");
  }

  [Test]
  public async Task QuietWindow_DecaysTheRunningProcessorBackTowardTheFloorAsync() {
    var (transport, client, time, _) = _createTransport();
    await transport.InitializeAsync();
    await _subscribeBatchAsync(transport);

    // Grow to 8 first.
    await _raiseSessionInitializingAsync(client, 4);
    time.Advance(_window);
    await _raiseSessionInitializingAsync(client, 1);

    // Then drain: 0 active on 8 slots — quiet stamped, window elapses, next event decays.
    await _raiseSessionClosingAsync(client, 5);
    time.Advance(_window);
    await client.LastSessionProcessor!.RaiseSessionInitializingAsync(_sessionEventArgs());

    await Assert.That(client.LastSessionProcessor!.MaxConcurrentSessions).IsEqualTo(4)
      .Because("a full quiet window hands surplus acceptors back — idle receive cost trends to the floor by construction");
  }

  [Test]
  public async Task PeriodicTick_EvaluatesWithoutAnySessionActivityAsync() {
    var (transport, client, time, logger) = _createTransport();
    await transport.InitializeAsync();
    await _subscribeBatchAsync(transport);

    var grown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    logger.MessageLogged += (_, message) => {
      if (message.Contains("Adaptive acceptors", StringComparison.Ordinal)) {
        grown.TrySetResult();
      }
    };

    // Pressure stamped by the 4th initialize; then NOTHING else happens — only the periodic
    // tick can observe the elapsed window (a stalled-at-capacity pool with no session churn).
    await _raiseSessionInitializingAsync(client, 4);
    time.Advance(_window);

    await grown.Task;
    await Assert.That(client.LastSessionProcessor!.MaxConcurrentSessions).IsEqualTo(8)
      .Because("the periodic tick is what lets a saturated-but-quiet pool grow — session events alone would never re-evaluate");

    await transport.DisposeAsync();
  }
}
