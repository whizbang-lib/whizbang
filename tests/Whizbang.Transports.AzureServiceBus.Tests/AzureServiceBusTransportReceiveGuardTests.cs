using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The two guards the receive and provisioning paths keep for states their own callers rule out:
/// a receive decision naming an action the transport's switch does not know, and a correlation
/// filter asked for without an administration client.
/// </summary>
/// <remarks>
/// Neither is reachable through the shipped call chain — the decision policy only ever names the
/// four defined actions, and the one caller of the filter helper checks for a client first — so
/// each is driven through the narrowest seam the class already offers: the internal decision-maker
/// property, and the internal filter helper itself.
/// </remarks>
[Timeout(10_000)]
public class AzureServiceBusTransportReceiveGuardTests {

  /// <summary>An action value no version of this transport has a case for.</summary>
  private const int UNRECOGNIZED_ACTION = 9999;

  /// <summary>
  /// A policy that names an action outside <see cref="AsbReceiveAction"/> — what a future action
  /// added to the enum but not to the transport's switch would look like on the wire.
  /// </summary>
  private sealed class UnrecognizedActionDecisionMaker : AsbReceiveDecisionMaker {
    public override AsbReceiveDecision Decide(
        IReadOnlyDictionary<string, object> applicationProperties,
        string bodyJson,
        Func<string, JsonSerializerOptions, JsonTypeInfo?> getTypeInfoByName,
        JsonSerializerOptions jsonOptions,
        Func<Type, bool>? isHandledLocally = null,
        IRawReceptorRegistry? rawReceptorRegistry = null,
        IMessageTypeBinder? typeBinder = null,
        IReadOnlySet<string>? absorbedNamespaces = null,
        Whizbang.Core.Routing.IPoisonMessageDetector? poisonDetector = null,
        Whizbang.Core.Routing.PoisonEvaluationContext poisonContext = default) =>
      new() {
        Action = (AsbReceiveAction)UNRECOGNIZED_ACTION,
        Reason = "UnrecognizedProbeAction",
        Description = "A policy naming an action this transport has no case for.",
        EnvelopeTypeName = "Probe.Unrecognized, Probe"
      };
  }

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client) _createTransport(
      bool enableSessions,
      RecordingTransportLogger logger,
      AsbReceiveDecisionMaker? decisionMaker = null) {
    var client = new RaisableServiceBusClient();
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = enableSessions
    };
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      options,
      logger,
      adminClient: null) {
      DecisionMaker = decisionMaker ?? new AsbReceiveDecisionMaker()
    };
    return (transport, client);
  }

  private static TransportDestination _destination() => new("guard-topic", "guard-sub");

  // An action the switch has no case for is a policy the transport does not understand. Settling
  // the message either way would be a guess: acking discards a message nobody decided to discard,
  // dead-lettering condemns one nobody decided to condemn. Failing leaves it unsettled, so the
  // broker redelivers and an operator sees the error — the only safe answer to "I don't know".
  [Test]
  public async Task ProcessMessage_WhenThePolicyNamesAnUnrecognizedAction_LeavesTheMessageUnsettledAsync() {
    var logger = new RecordingTransportLogger();
    var (transport, client) = _createTransport(
      enableSessions: false, logger, new UnrecognizedActionDecisionMaker());
    var handlerInvoked = false;
    await transport.SubscribeAsync((_, _, _) => { handlerInvoked = true; return Task.CompletedTask; }, _destination());
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(AsbTransportTestData.MessageArgs(
      AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));

    await Assert.That(handlerInvoked).IsFalse()
      .Because("an undecidable message must never reach the local handler");
    await Assert.That(receiver.Completed).IsEmpty()
      .Because("acking would discard a message no policy asked to discard");
    await Assert.That(receiver.DeadLettered).IsEmpty()
      .Because("dead-lettering would condemn a message no policy asked to condemn");
    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1)
      .Because("abandoning is what returns it to the broker for redelivery");
    await Assert.That(logger.Contains(LogLevel.Error, "Error processing message")).IsTrue()
      .Because("the failure has to be visible: it means this build's switch and its policy disagree");
  }

  // Same contract on the session pipeline, which has its own copy of the switch: a future action
  // added to one and not the other is exactly the drift these two guards exist to catch.
  [Test]
  public async Task ProcessSessionMessage_WhenThePolicyNamesAnUnrecognizedAction_LeavesTheMessageUnsettledAsync() {
    var logger = new RecordingTransportLogger();
    var (transport, client) = _createTransport(
      enableSessions: true, logger, new UnrecognizedActionDecisionMaker());
    var handlerInvoked = false;
    await transport.SubscribeAsync((_, _, _) => { handlerInvoked = true; return Task.CompletedTask; }, _destination());
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(AsbTransportTestData.SessionArgs(
      AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));

    await Assert.That(handlerInvoked).IsFalse();
    await Assert.That(receiver.Completed).IsEmpty();
    await Assert.That(receiver.DeadLettered).IsEmpty();
    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await Assert.That(logger.Contains(LogLevel.Error, "Error processing session message")).IsTrue();
  }

  // Replacing a subscription's default rule is a management-plane write. Without an administration
  // client there is no connection to make it on, and the caller must hear that rather than be told
  // the filter is in place while every message keeps arriving unfiltered.
  [Test]
  public async Task ApplyCorrelationFilterAsync_WithNoAdministrationClient_RefusesAsync(
      CancellationToken cancellationToken) {
    var logger = new RecordingTransportLogger();
    var (transport, _) = _createTransport(enableSessions: false, logger);

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await transport.ApplyCorrelationFilterAsync(
        "guard-topic", "guard-sub", "some-destination", cancellationToken));

    await Assert.That(ex!.Message).IsEqualTo("Administration client is not available");
  }
}
