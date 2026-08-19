#pragma warning disable CA1707 // Test method names can contain underscores

using System.Text.Json;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Transport-side wiring of the idle ops-rate self-check: every session-enabled subscription
/// start re-projects the cumulative idle ops rate and logs a structured warning when it crosses
/// the threshold — the moment the configuration is knowable, not after a namespace is already
/// pinned. Uses the raisable client (no broker) so subscription start succeeds locally.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusTransport.cs</code-under-test>
public class AsbOpsRateSelfCheckWiringTests {

  private const string WARNING_FRAGMENT = "idle ops-rate";

  private static (AzureServiceBusTransport Transport, RecordingTransportLogger Logger) _createTransport(
      AzureServiceBusOptions options) {
    var logger = new RecordingTransportLogger();
    var transport = new AzureServiceBusTransport(
      new RaisableServiceBusClient(),
      new JsonSerializerOptions(),
      options,
      logger);
    return (transport, logger);
  }

  private static Task<ISubscription> _subscribeAsync(AzureServiceBusTransport transport, string topic) =>
    transport.SubscribeBatchAsync(
      (batch, ct) => Task.CompletedTask,
      new TransportDestination(topic) { RoutingKey = "self-check-sub" },
      new TransportBatchOptions());

  [Test]
  public async Task Subscribe_ChurnHeavySessionConfiguration_LogsOpsRateWarningAsync() {
    // 200 acceptors / 1s idle timeout = 200 projected idle ops/sec — over the 100/sec default.
    var (transport, logger) = _createTransport(new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = true,
      MaxConcurrentSessions = 200,
      SessionIdleTimeout = TimeSpan.FromSeconds(1),
    });
    await transport.InitializeAsync();

    await _subscribeAsync(transport, "inbox");

    await Assert.That(logger.Contains(LogLevel.Warning, WARNING_FRAGMENT)).IsTrue()
      .Because("a configuration whose idle accept churn can pin the namespace must be called out at subscribe time");
  }

  [Test]
  public async Task Subscribe_TurnkeyDefaults_DoesNotWarnAsync() {
    var (transport, logger) = _createTransport(new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
    });
    await transport.InitializeAsync();

    await _subscribeAsync(transport, "inbox");

    await Assert.That(logger.Contains(LogLevel.Warning, WARNING_FRAGMENT)).IsFalse()
      .Because("the turnkey defaults project ≈3.3 idle ops/sec per subscription — far under threshold");
  }

  [Test]
  public async Task Subscribe_SelfCheckDisabled_DoesNotWarnAsync() {
    var (transport, logger) = _createTransport(new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = true,
      MaxConcurrentSessions = 200,
      SessionIdleTimeout = TimeSpan.FromSeconds(1),
      EnableOpsRateSelfCheck = false,
    });
    await transport.InitializeAsync();

    await _subscribeAsync(transport, "inbox");

    await Assert.That(logger.Contains(LogLevel.Warning, WARNING_FRAGMENT)).IsFalse()
      .Because("the killswitch must silence the self-check for deliberately churn-heavy Premium-tier setups");
  }

  [Test]
  public async Task Subscribe_ProjectionAccumulatesAcrossSubscriptionsAsync() {
    // 120 sessions / 2s = 60 ops/sec per subscription: one subscription sits under the 100/sec
    // threshold, the second pushes the cumulative projection to 120 and must warn.
    var (transport, logger) = _createTransport(new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = true,
      MaxConcurrentSessions = 120,
      SessionIdleTimeout = TimeSpan.FromSeconds(2),
    });
    await transport.InitializeAsync();

    await _subscribeAsync(transport, "inbox");
    await Assert.That(logger.Contains(LogLevel.Warning, WARNING_FRAGMENT)).IsFalse()
      .Because("60 ops/sec projected for a single subscription is under the threshold");

    await _subscribeAsync(transport, "domain-events");
    await Assert.That(logger.Contains(LogLevel.Warning, WARNING_FRAGMENT)).IsTrue()
      .Because("the projection is per-instance across ALL session subscriptions: 2 × 60 = 120 ops/sec exceeds 100");
  }

  [Test]
  public async Task Subscribe_NonSessionMode_DoesNotWarnAsync() {
    var (transport, logger) = _createTransport(new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = false,
      MaxConcurrentCalls = 500,
    });
    await transport.InitializeAsync();

    await _subscribeAsync(transport, "inbox");

    await Assert.That(logger.Contains(LogLevel.Warning, WARNING_FRAGMENT)).IsFalse()
      .Because("non-session processors long-poll and have no idle accept churn to project");
  }
}
