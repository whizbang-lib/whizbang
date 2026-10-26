// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Messaging.ServiceBus;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Unit tests for AzureServiceBusTransport's receive-side decision paths that need wired
/// registries or an all-levels logger: the slice-2 _buildIsHandledLocally predicate
/// (receptor hit / perspective hit / no-local-consumer drop), the slice-5 raw-receptor
/// dispatch on both session and non-session pipelines, the discard-policy telemetry routing,
/// the session abandon swallow policy, and the IsEnabled-gated receive diagnostics.
/// </summary>
[Timeout(10_000)]
public class AzureServiceBusTransportReceiveDecisionPathTests {
  private const string RAW_INNER_TYPE_NAME = "Contracts.External.RawOnlyMessage, Contracts.External";
  private const string RAW_ENVELOPE_TYPE_NAME =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Contracts.External.RawOnlyMessage, Contracts.External]], Whizbang.Core";

  // ========================================
  // SLICE-2 RECEPTOR/PERSPECTIVE RECEIVE FILTER
  // ========================================

  /// <summary>
  /// A payload type with a local receptor (at any lifecycle stage) passes the receive filter
  /// and reaches the handler.
  /// </summary>
  [Test]
  public async Task ProcessMessage_ReceptorRegistryHandlesPayload_ProcessesMessageAsync() {
    var (transport, client) = _createTransport(
      enableSessions: false,
      receptorRegistry: new StubReceptorRegistry(typeof(TestMessage)));
    var handled = new List<IMessageEnvelope>();
    await transport.SubscribeAsync(
      (envelope, _, _) => { handled.Add(envelope); return Task.CompletedTask; },
      _destination());
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(
      AsbTransportTestData.MessageArgs(AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));

    await Assert.That(handled).Count().IsEqualTo(1);
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
    await Assert.That(receiver.Abandoned).IsEmpty();
  }

  /// <summary>
  /// A payload type with no receptor but with a perspective Apply also passes the filter —
  /// the receptor loop misses and the perspective event-type loop hits.
  /// </summary>
  [Test]
  public async Task ProcessMessage_PerspectiveAppliesPayload_ProcessesMessageAsync() {
    var (transport, client) = _createTransport(
      enableSessions: false,
      receptorRegistry: new StubReceptorRegistry(null),
      perspectiveRegistry: new StubPerspectiveRegistry(typeof(TestMessage)));
    var handled = new List<IMessageEnvelope>();
    await transport.SubscribeAsync(
      (envelope, _, _) => { handled.Add(envelope); return Task.CompletedTask; },
      _destination());
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(
      AsbTransportTestData.MessageArgs(AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));

    await Assert.That(handled).Count().IsEqualTo(1);
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Registries wired but neither consumes the payload type: the message still reaches the handler, which stores it,
  /// before the broker is told anything; the consumer decides the skip after storage (#1720).
  /// </summary>
  [Test]
  public async Task ProcessMessage_NoLocalConsumer_IsHandedToTheHandlerBeforeItIsCompletedAsync() {
    var logger = new RecordingTransportLogger();
    var (transport, client) = _createTransport(
      enableSessions: false,
      logger: logger,
      receptorRegistry: new StubReceptorRegistry(null),
      perspectiveRegistry: new StubPerspectiveRegistry());
    var receiver = new RecordingTransportReceiver();
    var completedWhenHandled = -1;
    await transport.SubscribeAsync(
      (_, _, _) => { completedWhenHandled = receiver.Completed.Count; return Task.CompletedTask; },
      _destination());

    await client.LastProcessor!.RaiseMessageAsync(
      AsbTransportTestData.MessageArgs(AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));

    await Assert.That(completedWhenHandled).IsEqualTo(0)
      .Because("the handler stores the message before the broker is told anything");
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
    await Assert.That(receiver.DeadLettered).IsEmpty();
  }


  /// <summary>
  /// Session pipeline: a payload with no local consumer reaches the handler too, and the transport records no discard,
  /// since it no longer decides one (#1720).
  /// </summary>
  [Test]
  public async Task ProcessSessionMessage_NoLocalConsumer_IsHandedToTheHandler_AndRecordsNoDiscardAsync() {
    var discardPolicy = new RecordingDiscardPolicy();
    var (transport, client) = _createTransport(
      enableSessions: true,
      receptorRegistry: new StubReceptorRegistry(null),
      perspectiveRegistry: new StubPerspectiveRegistry(),
      discardPolicy: discardPolicy);
    var handled = 0;
    await transport.SubscribeAsync(
      (_, _, _) => { handled++; return Task.CompletedTask; },
      _destination());
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));

    await Assert.That(handled).IsEqualTo(1);
    await Assert.That(discardPolicy.Recorded).IsEmpty();
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
  }


  // ========================================
  // SLICE-5 RAW RECEPTOR DISPATCH
  // ========================================

  /// <summary>
  /// Non-session pipeline: when the typed binder misses but a raw receptor is registered for the envelope's inner type,
  /// the handler stores the message as received first, then the raw receptor gets the "p" payload, then the message is
  /// completed (#1720).
  /// </summary>
  [Test]
  public async Task ProcessMessage_RawReceptorMatch_StoresThenInvokesRawReceptorThenCompletesAsync() {
    var logger = new RecordingTransportLogger();
    var rawReceptor = new RecordingRawReceptor(RAW_INNER_TYPE_NAME);
    var (transport, client) = _createTransport(
      enableSessions: false,
      logger: logger,
      rawReceptorRegistry: new SingleRawReceptorRegistry(rawReceptor),
      typeBinder: new MissTypeBinder());
    var receptorRunsWhenHandled = -1;
    await transport.SubscribeAsync(
      (_, _, _) => { receptorRunsWhenHandled = rawReceptor.Handled.Count; return Task.CompletedTask; },
      _destination());
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(AsbTransportTestData.MessageArgs(
      AsbTransportTestData.RawMessage(_rawEnvelopeBody("raw-payload"), RAW_ENVELOPE_TYPE_NAME),
      receiver));

    await Assert.That(receptorRunsWhenHandled).IsEqualTo(0).Because("the message is stored before the raw receptor runs");
    await Assert.That(rawReceptor.Handled).Count().IsEqualTo(1);
    await Assert.That(rawReceptor.Handled[0].GetProperty("content").GetString()).IsEqualTo("raw-payload");
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
    await Assert.That(receiver.DeadLettered).IsEmpty();
    await Assert.That(logger.Contains(LogLevel.Information, "stored as received")).IsTrue();
  }


  /// <summary>Session pipeline counterpart of the raw-receptor dispatch.</summary>
  [Test]
  public async Task ProcessSessionMessage_RawReceptorMatch_StoresThenInvokesRawReceptorThenCompletesAsync() {
    var rawReceptor = new RecordingRawReceptor(RAW_INNER_TYPE_NAME);
    var (transport, client) = _createTransport(
      enableSessions: true,
      rawReceptorRegistry: new SingleRawReceptorRegistry(rawReceptor),
      typeBinder: new MissTypeBinder());
    var receptorRunsWhenHandled = -1;
    await transport.SubscribeAsync(
      (_, _, _) => { receptorRunsWhenHandled = rawReceptor.Handled.Count; return Task.CompletedTask; },
      _destination());
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(AsbTransportTestData.SessionArgs(
      AsbTransportTestData.RawMessage(_rawEnvelopeBody("raw-session-payload"), RAW_ENVELOPE_TYPE_NAME),
      receiver));

    await Assert.That(receptorRunsWhenHandled).IsEqualTo(0);
    await Assert.That(rawReceptor.Handled).Count().IsEqualTo(1);
    await Assert.That(rawReceptor.Handled[0].GetProperty("content").GetString()).IsEqualTo("raw-session-payload");
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
  }


  // ========================================
  // SESSION ABANDON SWALLOW POLICY
  // ========================================

  /// <summary>
  /// The session _safeAbandonAsync wrapper swallows lock-lost-class settlement failures —
  /// the processor callback must not propagate when abandoning after a handler error.
  /// </summary>
  [Test]
  [Arguments(ServiceBusFailureReason.SessionLockLost)]
  [Arguments(ServiceBusFailureReason.MessageLockLost)]
  [Arguments(ServiceBusFailureReason.MessageNotFound)]
  public async Task ProcessSessionMessage_AbandonThrowsSwallowableReason_SwallowsExceptionAsync(ServiceBusFailureReason reason) {
    var (transport, client) = _createTransport(enableSessions: true);
    await transport.SubscribeAsync(
      (_, _, _) => throw new InvalidOperationException("session boom"),
      _destination());
    var receiver = new RecordingTransportSessionReceiver {
      AbandonException = new ServiceBusException("settlement failed", reason)
    };

    // Must not throw out of the session processor callback.
    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1)
      .Because("the abandon attempt is made, then the lock-lost failure is swallowed");
    await Assert.That(receiver.DeadLettered).IsEmpty();
  }

  // ========================================
  // DEBUG-GATED RECEIVE DIAGNOSTICS
  // ========================================

  /// <summary>
  /// With every log level enabled, the non-session receive pipeline emits the subscription
  /// info log plus the body-preview / deserialized / processed diagnostics. A body longer
  /// than 500 characters takes the truncated-preview branch.
  /// </summary>
  [Test]
  public async Task ProcessMessage_ValidEnvelopeDebugLogging_EmitsReceiveDiagnosticsAsync() {
    var logger = new RecordingTransportLogger();
    var (transport, client) = _createTransport(enableSessions: false, logger: logger);
    var handled = new List<IMessageEnvelope>();
    await transport.SubscribeAsync(
      (envelope, _, _) => { handled.Add(envelope); return Task.CompletedTask; },
      _destination());
    var receiver = new RecordingTransportReceiver();
    var largeContent = new string('y', 600);

    await client.LastProcessor!.RaiseMessageAsync(AsbTransportTestData.MessageArgs(
      AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(content: largeContent)), receiver));

    await Assert.That(handled).Count().IsEqualTo(1);
    await Assert.That(logger.Contains(LogLevel.Information, "Started subscription")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Debug, "DIAGNOSTIC [Subscribe]: Received message")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Debug, "DIAGNOSTIC [Subscribe]: Deserialized envelope")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Debug, "Processed message")).IsTrue();
  }

  /// <summary>
  /// Session receive pipeline with all levels enabled emits the per-session processed
  /// diagnostic (message id + session id).
  /// </summary>
  [Test]
  public async Task ProcessSessionMessage_ValidEnvelopeDebugLogging_EmitsSessionDiagnosticsAsync() {
    var logger = new RecordingTransportLogger();
    var (transport, client) = _createTransport(enableSessions: true, logger: logger);
    var handled = new List<IMessageEnvelope>();
    await transport.SubscribeAsync(
      (envelope, _, _) => { handled.Add(envelope); return Task.CompletedTask; },
      _destination());
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));

    await Assert.That(handled).Count().IsEqualTo(1);
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
    await Assert.That(logger.Contains(LogLevel.Information, "Started subscription")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Debug, "Processed session message")).IsTrue();
  }

  // ========================================
  // HELPERS
  // ========================================

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client) _createTransport(
    bool enableSessions,
    RecordingTransportLogger? logger = null,
    IReceptorRegistry? receptorRegistry = null,
    IPerspectiveRunnerRegistry? perspectiveRegistry = null,
    IRawReceptorRegistry? rawReceptorRegistry = null,
    IMessageTypeBinder? typeBinder = null,
    IMessageDiscardPolicy? discardPolicy = null) {
    var client = new RaisableServiceBusClient();
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = enableSessions
    };
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      options,
      (ILogger<AzureServiceBusTransport>?)logger ?? NullLogger<AzureServiceBusTransport>.Instance,
      adminClient: null,
      receptorRegistry: receptorRegistry,
      perspectiveRegistry: perspectiveRegistry,
      rawReceptorRegistry: rawReceptorRegistry,
      typeBinder: typeBinder,
      discardPolicy: discardPolicy);
    return (transport, client);
  }

  private static TransportDestination _destination() => new("decision-topic", "decision-sub");

  /// <summary>A full envelope whose payload this service has no type for, as a producer of it writes it.</summary>
  private static string _rawEnvelopeBody(string content) {
    var envelope = AsbTransportTestData.CreateEnvelope();
    var asReceived = new MessageEnvelope<JsonElement> {
      MessageId = envelope.MessageId,
      Payload = JsonElement.Parse("{\"content\":\"" + content + "\"}"),
      Hops = envelope.Hops,
      DispatchContext = envelope.DispatchContext,
    };
    return JsonSerializer.Serialize(asReceived,
      AsbTransportTestData.CombinedOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
  }
}
