using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Unit tests for AzureServiceBusTransport's receive-pipeline error handling and lifecycle
/// paths: _handleMessageProcessingErrorAsync, _handleSessionMessageProcessingErrorAsync,
/// _handleProcessorErrorAsync, _invokeRecoveryHandlerAsync, the _safeAbandonAsync /
/// _safeDeadLetterAsync swallow policy, DisposeAsync, and the SendAsync / PublishAsync
/// error branches.
///
/// No broker is used: the Azure SDK's documented mocking surface is exercised instead —
/// mockable ServiceBusClient / ServiceBusProcessor subclasses raise OnProcessMessageAsync /
/// OnProcessErrorAsync directly, and settlement calls are captured by fake receivers.
/// Every raise is awaited inline, so assertions are deterministic without timing waits.
/// </summary>
[Timeout(10_000)]
public class AzureServiceBusErrorHandlingTests {
  private static readonly JsonSerializerOptions _combinedOptions = JsonContextRegistry.CreateCombinedOptions();

  // ========================================
  // NON-SESSION RECEIVE PIPELINE
  // ========================================

  /// <summary>Happy path: a valid envelope reaches the handler and is completed.</summary>
  [Test]
  public async Task ProcessMessage_ValidEnvelope_InvokesHandlerAndCompletesAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    var handled = new List<IMessageEnvelope>();
    await transport.SubscribeAsync(
      (envelope, _, _) => { handled.Add(envelope); return Task.CompletedTask; },
      _destination());
    var receiver = new FakeReceiver();
    var envelope = _createEnvelope();

    await client.LastProcessor!.RaiseMessageAsync(_messageArgs(_envelopeMessage(envelope), receiver));

    await Assert.That(handled).Count().IsEqualTo(1);
    await Assert.That(handled[0].MessageId.Value).IsEqualTo(envelope.MessageId.Value);
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
    await Assert.That(receiver.Abandoned).IsEmpty();
    await Assert.That(receiver.DeadLettered).IsEmpty();
  }

  /// <summary>
  /// Handler failure below MaxDeliveryAttempts abandons the message for broker redelivery.
  /// </summary>
  [Test]
  public async Task ProcessMessage_HandlerThrowsBelowMaxAttempts_AbandonsMessageAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeAsync(
      (_, _, _) => throw new InvalidOperationException("boom"),
      _destination());
    var receiver = new FakeReceiver();

    await client.LastProcessor!.RaiseMessageAsync(
      _messageArgs(_envelopeMessage(_createEnvelope(), deliveryCount: 1), receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await Assert.That(receiver.DeadLettered).IsEmpty();
    await Assert.That(receiver.Completed).IsEmpty();
  }

  /// <summary>
  /// Handler failure at MaxDeliveryAttempts dead-letters with the standard reason and the
  /// handler's exception message as description.
  /// </summary>
  [Test]
  public async Task ProcessMessage_HandlerThrowsAtMaxAttempts_DeadLettersMessageAsync() {
    var (transport, client) = _createTransport(enableSessions: false, maxDeliveryAttempts: 5);
    await transport.SubscribeAsync(
      (_, _, _) => throw new InvalidOperationException("boom"),
      _destination());
    var receiver = new FakeReceiver();

    await client.LastProcessor!.RaiseMessageAsync(
      _messageArgs(_envelopeMessage(_createEnvelope(), deliveryCount: 5), receiver));

    await Assert.That(receiver.DeadLettered).Count().IsEqualTo(1);
    await Assert.That(receiver.DeadLettered[0].Reason).IsEqualTo("MaxDeliveryAttemptsExceeded");
    await Assert.That(receiver.DeadLettered[0].Description).IsEqualTo("boom");
    await Assert.That(receiver.Abandoned).IsEmpty();
  }

  /// <summary>
  /// _safeDeadLetterAsync swallows lock-lost failures during dead-lettering — the broker
  /// will redeliver and re-make the same decision.
  /// </summary>
  [Test]
  public async Task ProcessMessage_DeadLetterThrowsLockLost_SwallowsExceptionAsync() {
    var (transport, client) = _createTransport(enableSessions: false, maxDeliveryAttempts: 1);
    await transport.SubscribeAsync(
      (_, _, _) => throw new InvalidOperationException("boom"),
      _destination());
    var receiver = new FakeReceiver {
      DeadLetterException = new ServiceBusException("lock lost", ServiceBusFailureReason.MessageLockLost)
    };

    // Must not throw out of the processor callback.
    await client.LastProcessor!.RaiseMessageAsync(
      _messageArgs(_envelopeMessage(_createEnvelope(), deliveryCount: 3), receiver));

    await Assert.That(receiver.DeadLettered).Count().IsEqualTo(1)
      .Because("the dead-letter attempt is made, then the lock-lost failure is swallowed");
  }

  /// <summary>
  /// _safeAbandonAsync swallows ObjectDisposedException (processor shutting down mid-flight).
  /// </summary>
  [Test]
  public async Task ProcessMessage_AbandonThrowsObjectDisposed_SwallowsExceptionAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeAsync(
      (_, _, _) => throw new InvalidOperationException("boom"),
      _destination());
    var receiver = new FakeReceiver {
      AbandonException = new ObjectDisposedException("receiver")
    };

    await client.LastProcessor!.RaiseMessageAsync(
      _messageArgs(_envelopeMessage(_createEnvelope(), deliveryCount: 1), receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1)
      .Because("the abandon attempt is made, then the disposed failure is swallowed");
  }

  /// <summary>
  /// All lock-lost-class settlement failures are swallowed by the abandon safety wrapper.
  /// </summary>
  [Test]
  [Arguments(ServiceBusFailureReason.MessageLockLost)]
  [Arguments(ServiceBusFailureReason.SessionLockLost)]
  [Arguments(ServiceBusFailureReason.MessageNotFound)]
  public async Task ProcessMessage_AbandonThrowsSwallowableReason_SwallowsExceptionAsync(ServiceBusFailureReason reason) {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeAsync(
      (_, _, _) => throw new InvalidOperationException("boom"),
      _destination());
    var receiver = new FakeReceiver {
      AbandonException = new ServiceBusException("settlement failed", reason)
    };

    await client.LastProcessor!.RaiseMessageAsync(
      _messageArgs(_envelopeMessage(_createEnvelope(), deliveryCount: 1), receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
  }

  /// <summary>
  /// A message without the EnvelopeType application property has nothing to route on and is
  /// dead-lettered at the broker.
  /// </summary>
  [Test]
  public async Task ProcessMessage_MissingEnvelopeType_DeadLettersMessageAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    var handlerInvoked = false;
    await transport.SubscribeAsync(
      (_, _, _) => { handlerInvoked = true; return Task.CompletedTask; },
      _destination());
    var receiver = new FakeReceiver();

    await client.LastProcessor!.RaiseMessageAsync(
      _messageArgs(_rawMessage("{}", envelopeTypeName: null), receiver));

    await Assert.That(receiver.DeadLettered).Count().IsEqualTo(1);
    await Assert.That(receiver.DeadLettered[0].Reason).IsEqualTo(AsbReceiveReason.MISSING_ENVELOPE_TYPE);
    await Assert.That(handlerInvoked).IsFalse();
  }

  /// <summary>
  /// An envelope type with no local JsonTypeInfo is acked and dropped (no DLQ accumulation).
  /// </summary>
  [Test]
  public async Task ProcessMessage_UnknownEnvelopeType_AcksAndDropsMessageAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    var handlerInvoked = false;
    await transport.SubscribeAsync(
      (_, _, _) => { handlerInvoked = true; return Task.CompletedTask; },
      _destination());
    var receiver = new FakeReceiver();

    await client.LastProcessor!.RaiseMessageAsync(
      _messageArgs(_rawMessage("{}", "Unknown.Namespace.NoSuchEnvelope, NoSuchAssembly"), receiver));

    await Assert.That(receiver.Completed).Count().IsEqualTo(1)
      .Because("unresolvable types are acked so they exit the topic without DLQ accumulation");
    await Assert.That(receiver.DeadLettered).IsEmpty();
    await Assert.That(handlerInvoked).IsFalse();
  }

  /// <summary>A malformed body for a known envelope type is acked and dropped.</summary>
  [Test]
  public async Task ProcessMessage_MalformedBody_AcksAndDropsMessageAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    var handlerInvoked = false;
    await transport.SubscribeAsync(
      (_, _, _) => { handlerInvoked = true; return Task.CompletedTask; },
      _destination());
    var receiver = new FakeReceiver();

    await client.LastProcessor!.RaiseMessageAsync(
      _messageArgs(_rawMessage("{{{not-json", typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!), receiver));

    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
    await Assert.That(handlerInvoked).IsFalse();
  }

  /// <summary>
  /// A settlement failure during the ack+drop path (CompleteMessageAsync throws a
  /// non-swallowable exception) routes into _handleMessageProcessingErrorAsync, which
  /// abandons the message for redelivery.
  /// </summary>
  [Test]
  public async Task ProcessMessage_CompleteFailsDuringAckDrop_AbandonsMessageAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var receiver = new FakeReceiver {
      CompleteException = new InvalidOperationException("complete failed")
    };

    await client.LastProcessor!.RaiseMessageAsync(
      _messageArgs(_rawMessage("{}", "Unknown.Namespace.NoSuchEnvelope, NoSuchAssembly"), receiver));

    await Assert.That(receiver.Completed).Count().IsEqualTo(1)
      .Because("the ack was attempted before its failure was routed to the error handler");
    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1)
      .Because("a non-swallowable settlement failure falls back to abandon-for-redelivery");
  }

  /// <summary>A paused subscription abandons messages without invoking the handler.</summary>
  [Test]
  public async Task ProcessMessage_PausedSubscription_AbandonsWithoutInvokingHandlerAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    var handlerInvoked = false;
    var subscription = await transport.SubscribeAsync(
      (_, _, _) => { handlerInvoked = true; return Task.CompletedTask; },
      _destination());
    await subscription.PauseAsync();
    var receiver = new FakeReceiver();

    await client.LastProcessor!.RaiseMessageAsync(_messageArgs(_envelopeMessage(_createEnvelope()), receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await Assert.That(handlerInvoked).IsFalse();
  }

  /// <summary>Resuming a paused subscription restores normal processing.</summary>
  [Test]
  public async Task ProcessMessage_ResumedSubscription_ProcessesMessagesAgainAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    var handled = 0;
    var subscription = await transport.SubscribeAsync(
      (_, _, _) => { handled++; return Task.CompletedTask; },
      _destination());
    await subscription.PauseAsync();
    await subscription.ResumeAsync();
    var receiver = new FakeReceiver();

    await client.LastProcessor!.RaiseMessageAsync(_messageArgs(_envelopeMessage(_createEnvelope()), receiver));

    await Assert.That(handled).IsEqualTo(1);
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
    await Assert.That(receiver.Abandoned).IsEmpty();
  }

  // ========================================
  // SESSION RECEIVE PIPELINE
  // ========================================

  /// <summary>Session happy path: valid envelope is handled and completed.</summary>
  [Test]
  public async Task ProcessSessionMessage_ValidEnvelope_InvokesHandlerAndCompletesAsync() {
    var (transport, client) = _createTransport(enableSessions: true);
    var handled = new List<IMessageEnvelope>();
    await transport.SubscribeAsync(
      (envelope, _, _) => { handled.Add(envelope); return Task.CompletedTask; },
      _destination());
    var receiver = new FakeSessionReceiver();
    var envelope = _createEnvelope();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      _sessionArgs(_envelopeMessage(envelope), receiver));

    await Assert.That(handled).Count().IsEqualTo(1);
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
  }

  /// <summary>Session handler failure below the max attempts abandons the message.</summary>
  [Test]
  public async Task ProcessSessionMessage_HandlerThrowsBelowMaxAttempts_AbandonsMessageAsync() {
    var (transport, client) = _createTransport(enableSessions: true);
    await transport.SubscribeAsync(
      (_, _, _) => throw new InvalidOperationException("session boom"),
      _destination());
    var receiver = new FakeSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      _sessionArgs(_envelopeMessage(_createEnvelope(), deliveryCount: 1), receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await Assert.That(receiver.DeadLettered).IsEmpty();
  }

  /// <summary>Session handler failure at the max attempts dead-letters the message.</summary>
  [Test]
  public async Task ProcessSessionMessage_HandlerThrowsAtMaxAttempts_DeadLettersMessageAsync() {
    var (transport, client) = _createTransport(enableSessions: true, maxDeliveryAttempts: 3);
    await transport.SubscribeAsync(
      (_, _, _) => throw new InvalidOperationException("session boom"),
      _destination());
    var receiver = new FakeSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      _sessionArgs(_envelopeMessage(_createEnvelope(), deliveryCount: 3), receiver));

    await Assert.That(receiver.DeadLettered).Count().IsEqualTo(1);
    await Assert.That(receiver.DeadLettered[0].Reason).IsEqualTo("MaxDeliveryAttemptsExceeded");
    await Assert.That(receiver.DeadLettered[0].Description).IsEqualTo("session boom");
  }

  /// <summary>
  /// Session _safeDeadLetterAsync swallows SessionLockLost during dead-lettering.
  /// </summary>
  [Test]
  public async Task ProcessSessionMessage_DeadLetterThrowsSessionLockLost_SwallowsExceptionAsync() {
    var (transport, client) = _createTransport(enableSessions: true, maxDeliveryAttempts: 1);
    await transport.SubscribeAsync(
      (_, _, _) => throw new InvalidOperationException("session boom"),
      _destination());
    var receiver = new FakeSessionReceiver {
      DeadLetterException = new ServiceBusException("session lock lost", ServiceBusFailureReason.SessionLockLost)
    };

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      _sessionArgs(_envelopeMessage(_createEnvelope(), deliveryCount: 2), receiver));

    await Assert.That(receiver.DeadLettered).Count().IsEqualTo(1);
  }

  /// <summary>Session message without EnvelopeType metadata is dead-lettered.</summary>
  [Test]
  public async Task ProcessSessionMessage_MissingEnvelopeType_DeadLettersMessageAsync() {
    var (transport, client) = _createTransport(enableSessions: true);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var receiver = new FakeSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      _sessionArgs(_rawMessage("{}", envelopeTypeName: null), receiver));

    await Assert.That(receiver.DeadLettered).Count().IsEqualTo(1);
    await Assert.That(receiver.DeadLettered[0].Reason).IsEqualTo(AsbReceiveReason.MISSING_ENVELOPE_TYPE);
  }

  /// <summary>
  /// Session message with an unresolvable envelope type is acked and dropped (routes
  /// through EmitAckDropTelemetry's warning branch).
  /// </summary>
  [Test]
  public async Task ProcessSessionMessage_UnknownEnvelopeType_AcksAndDropsMessageAsync() {
    var (transport, client) = _createTransport(enableSessions: true);
    var handlerInvoked = false;
    await transport.SubscribeAsync(
      (_, _, _) => { handlerInvoked = true; return Task.CompletedTask; },
      _destination());
    var receiver = new FakeSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      _sessionArgs(_rawMessage("{}", "Unknown.Namespace.NoSuchEnvelope, NoSuchAssembly"), receiver));

    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
    await Assert.That(receiver.DeadLettered).IsEmpty();
    await Assert.That(handlerInvoked).IsFalse();
  }

  /// <summary>A paused session subscription abandons messages without handling.</summary>
  [Test]
  public async Task ProcessSessionMessage_PausedSubscription_AbandonsWithoutInvokingHandlerAsync() {
    var (transport, client) = _createTransport(enableSessions: true);
    var handlerInvoked = false;
    var subscription = await transport.SubscribeAsync(
      (_, _, _) => { handlerInvoked = true; return Task.CompletedTask; },
      _destination());
    await subscription.PauseAsync();
    var receiver = new FakeSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      _sessionArgs(_envelopeMessage(_createEnvelope()), receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await Assert.That(handlerInvoked).IsFalse();
  }

  // ========================================
  // PROCESSOR ERROR HANDLING / RECOVERY (ITransportWithRecovery)
  // ========================================

  /// <summary>
  /// Connection-level processor errors trigger the registered recovery handler.
  /// </summary>
  [Test]
  [Arguments(ServiceBusFailureReason.ServiceCommunicationProblem)]
  [Arguments(ServiceBusFailureReason.ServiceBusy)]
  [Arguments(ServiceBusFailureReason.ServiceTimeout)]
  public async Task ProcessorError_ConnectionError_InvokesRecoveryHandlerAsync(ServiceBusFailureReason reason) {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var recoveryInvocations = 0;
    transport.SetRecoveryHandler(_ => { recoveryInvocations++; return Task.CompletedTask; });

    await client.LastProcessor!.RaiseErrorAsync(
      _errorArgs(new ServiceBusException("connection error", reason)));

    await Assert.That(recoveryInvocations).IsEqualTo(1);
  }

  /// <summary>Non-connection Service Bus errors do not trigger recovery.</summary>
  [Test]
  public async Task ProcessorError_NonConnectionReason_DoesNotInvokeRecoveryHandlerAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var recoveryInvocations = 0;
    transport.SetRecoveryHandler(_ => { recoveryInvocations++; return Task.CompletedTask; });

    await client.LastProcessor!.RaiseErrorAsync(
      _errorArgs(new ServiceBusException("entity missing", ServiceBusFailureReason.MessagingEntityNotFound)));

    await Assert.That(recoveryInvocations).IsEqualTo(0);
  }

  /// <summary>Non-ServiceBus exceptions do not trigger recovery.</summary>
  [Test]
  public async Task ProcessorError_NonServiceBusException_DoesNotInvokeRecoveryHandlerAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var recoveryInvocations = 0;
    transport.SetRecoveryHandler(_ => { recoveryInvocations++; return Task.CompletedTask; });

    await client.LastProcessor!.RaiseErrorAsync(_errorArgs(new InvalidOperationException("not a connection issue")));

    await Assert.That(recoveryInvocations).IsEqualTo(0);
  }

  /// <summary>
  /// A recovery handler that itself throws is swallowed — the processor error callback must
  /// never propagate.
  /// </summary>
  [Test]
  public async Task ProcessorError_RecoveryHandlerThrows_ExceptionSwallowedAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var recoveryInvocations = 0;
    transport.SetRecoveryHandler(_ => {
      recoveryInvocations++;
      throw new InvalidOperationException("recovery failed");
    });

    // Must not throw.
    await client.LastProcessor!.RaiseErrorAsync(
      _errorArgs(new ServiceBusException("connection error", ServiceBusFailureReason.ServiceCommunicationProblem)));

    await Assert.That(recoveryInvocations).IsEqualTo(1)
      .Because("the handler is invoked, and its failure is logged rather than rethrown");
  }

  /// <summary>Connection errors without any registered recovery handler complete quietly.</summary>
  [Test]
  public async Task ProcessorError_NoRecoveryHandler_CompletesWithoutErrorAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());

    // Must not throw.
    await client.LastProcessor!.RaiseErrorAsync(
      _errorArgs(new ServiceBusException("connection error", ServiceBusFailureReason.ServiceBusy)));

    await Assert.That(transport.IsInitialized).IsFalse()
      .Because("sanity: transport state is untouched by the error path");
  }

  /// <summary>SetRecoveryHandler(null) clears a previously registered handler.</summary>
  [Test]
  public async Task SetRecoveryHandler_ClearedWithNull_HandlerNotInvokedAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var recoveryInvocations = 0;
    transport.SetRecoveryHandler(_ => { recoveryInvocations++; return Task.CompletedTask; });
    transport.SetRecoveryHandler(null);

    await client.LastProcessor!.RaiseErrorAsync(
      _errorArgs(new ServiceBusException("connection error", ServiceBusFailureReason.ServiceTimeout)));

    await Assert.That(recoveryInvocations).IsEqualTo(0);
  }

  /// <summary>Session processors route errors through the same recovery path.</summary>
  [Test]
  public async Task ProcessorError_SessionProcessorConnectionError_InvokesRecoveryHandlerAsync() {
    var (transport, client) = _createTransport(enableSessions: true);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var recoveryInvocations = 0;
    transport.SetRecoveryHandler(_ => { recoveryInvocations++; return Task.CompletedTask; });

    await client.LastSessionProcessor!.RaiseErrorAsync(
      _errorArgs(new ServiceBusException("connection error", ServiceBusFailureReason.ServiceCommunicationProblem)));

    await Assert.That(recoveryInvocations).IsEqualTo(1);
  }

  // ========================================
  // SendAsync (REQUEST/RESPONSE NOT SUPPORTED)
  // ========================================

  /// <summary>ASB has no native request/response — SendAsync always throws NotSupported.</summary>
  [Test]
  public async Task SendAsync_Always_ThrowsNotSupportedExceptionAsync() {
    var (transport, _) = _createTransport(enableSessions: false);

    await Assert.ThrowsAsync<NotSupportedException>(async () =>
      await transport.SendAsync<TestMessage, TestMessage>(_createEnvelope(), _destination()));
  }

  /// <summary>Disposal is checked before the not-supported guard.</summary>
  [Test]
  public async Task SendAsync_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    var (transport, _) = _createTransport(enableSessions: false);
    await transport.DisposeAsync();

    await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
      await transport.SendAsync<TestMessage, TestMessage>(_createEnvelope(), _destination()));
  }

  // ========================================
  // PublishAsync BRANCHES (FAKE SENDER — NO BROKER)
  // ========================================

  /// <summary>
  /// Publish success path: envelope metadata is projected onto the ServiceBusMessage
  /// (MessageId, EnvelopeType, SessionId from StreamId, custom metadata, subject).
  /// </summary>
  [Test]
  public async Task PublishAsync_WithMetadata_SendsMessageWithSessionAndPropertiesAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    var envelope = _createEnvelope();
    var streamId = Guid.CreateVersion7();
    var metadata = new Dictionary<string, JsonElement> {
      ["StreamId"] = _json($"\"{streamId}\""),
      ["tenant"] = _json("\"acme\"")
    };
    var destination = new TransportDestination("pub-topic", "orders.created", metadata);

    await transport.PublishAsync(envelope, destination);

    var sender = client.LastSender!;
    await Assert.That(sender.Sent).Count().IsEqualTo(1);
    var message = sender.Sent[0];
    await Assert.That(message.MessageId).IsEqualTo(envelope.MessageId.Value.ToString());
    await Assert.That(message.Subject).IsEqualTo("orders.created");
    await Assert.That(message.SessionId).IsEqualTo(streamId.ToString());
    await Assert.That(message.ContentType).IsEqualTo("application/json");
    await Assert.That(message.ApplicationProperties["EnvelopeType"])
      .IsEqualTo(typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName);
    await Assert.That(message.ApplicationProperties["tenant"]).IsEqualTo("acme");
  }

  /// <summary>Send failures are logged and rethrown to the caller.</summary>
  [Test]
  public async Task PublishAsync_SenderFails_PropagatesExceptionAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    client.SendException = new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy);

    await Assert.ThrowsAsync<ServiceBusException>(async () =>
      await transport.PublishAsync(_createEnvelope(), _destination("pub-topic")));
  }

  /// <summary>Publishing after disposal is rejected.</summary>
  [Test]
  public async Task PublishAsync_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    var (transport, _) = _createTransport(enableSessions: false);
    await transport.DisposeAsync();

    await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
      await transport.PublishAsync(_createEnvelope(), _destination("pub-topic")));
  }

  // ========================================
  // DisposeAsync PATHS
  // ========================================

  /// <summary>DisposeAsync disposes every cached sender exactly once.</summary>
  [Test]
  public async Task DisposeAsync_WithCachedSender_DisposesSenderAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.PublishAsync(_createEnvelope(), _destination("pub-topic"));

    await transport.DisposeAsync();

    await Assert.That(client.LastSender!.DisposeCount).IsEqualTo(1);
  }

  /// <summary>Double-dispose is an idempotent no-op.</summary>
  [Test]
  public async Task DisposeAsync_CalledTwice_DisposesSendersOnlyOnceAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.PublishAsync(_createEnvelope(), _destination("pub-topic"));

    await transport.DisposeAsync();
    await transport.DisposeAsync();

    await Assert.That(client.LastSender!.DisposeCount).IsEqualTo(1)
      .Because("the second DisposeAsync must return early without re-disposing senders");
  }

  /// <summary>
  /// DisposeAsync clears the recovery handler — connection errors raised by still-wired
  /// processors after disposal no longer invoke it.
  /// </summary>
  [Test]
  public async Task DisposeAsync_ClearsRecoveryHandler_ErrorAfterDisposeDoesNotInvokeAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var recoveryInvocations = 0;
    transport.SetRecoveryHandler(_ => { recoveryInvocations++; return Task.CompletedTask; });

    await transport.DisposeAsync();
    await client.LastProcessor!.RaiseErrorAsync(
      _errorArgs(new ServiceBusException("connection error", ServiceBusFailureReason.ServiceCommunicationProblem)));

    await Assert.That(recoveryInvocations).IsEqualTo(0)
      .Because("DisposeAsync nulls the recovery handler to prevent leaks and late callbacks");
  }

  // ========================================
  // HELPERS
  // ========================================

  private static (AzureServiceBusTransport Transport, FakeServiceBusClient Client) _createTransport(
    bool enableSessions,
    int maxDeliveryAttempts = 10) {
    var client = new FakeServiceBusClient();
    var options = new AzureServiceBusOptions {
      // No admin client is wired, so provisioning short-circuits regardless.
      AutoProvisionInfrastructure = false,
      EnableSessions = enableSessions,
      MaxDeliveryAttempts = maxDeliveryAttempts
    };
    var transport = new AzureServiceBusTransport(
      client,
      _combinedOptions,
      options,
      NullLogger<AzureServiceBusTransport>.Instance);
    return (transport, client);
  }

  private static TransportDestination _destination(string topic = "err-topic") => new(topic, "err-sub");

  private static MessageEnvelope<TestMessage> _createEnvelope() => new() {
    MessageId = MessageId.New(),
    Payload = new TestMessage("error-handling-test"),
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        Topic = "err-topic",
        ServiceInstance = ServiceInstanceInfo.Unknown
      }
    ]
  };

  /// <summary>Builds a broker message carrying a real serialized envelope.</summary>
  private static ServiceBusReceivedMessage _envelopeMessage(MessageEnvelope<TestMessage> envelope, int deliveryCount = 1) {
    var typeInfo = _combinedOptions.GetTypeInfo(typeof(MessageEnvelope<TestMessage>));
    var body = JsonSerializer.Serialize(envelope, typeInfo);
    return _rawMessage(body, typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!, deliveryCount);
  }

  /// <summary>Builds a broker message with an arbitrary body and optional EnvelopeType property.</summary>
  private static ServiceBusReceivedMessage _rawMessage(string body, string? envelopeTypeName, int deliveryCount = 1) {
    var properties = new Dictionary<string, object>();
    if (envelopeTypeName is not null) {
      properties["EnvelopeType"] = envelopeTypeName;
    }
    return ServiceBusModelFactory.ServiceBusReceivedMessage(
      body: BinaryData.FromString(body),
      messageId: Guid.CreateVersion7().ToString(),
      deliveryCount: deliveryCount,
      properties: properties);
  }

  private static ProcessMessageEventArgs _messageArgs(ServiceBusReceivedMessage message, FakeReceiver receiver) =>
    new(message, receiver, CancellationToken.None);

  private static ProcessSessionMessageEventArgs _sessionArgs(ServiceBusReceivedMessage message, FakeSessionReceiver receiver) =>
    new(message, receiver, CancellationToken.None);

  private static ProcessErrorEventArgs _errorArgs(Exception exception) =>
    new(exception, ServiceBusErrorSource.Receive, "unit-test.servicebus.windows.net", "err-topic", CancellationToken.None);

  private static JsonElement _json(string raw) {
    using var doc = JsonDocument.Parse(raw);
    return doc.RootElement.Clone();
  }

  // ========================================
  // TEST DOUBLES
  // ========================================

  /// <summary>
  /// Mockable ServiceBusClient that hands out raisable fake processors and recording senders
  /// without opening any connection.
  /// </summary>
  private sealed class FakeServiceBusClient : ServiceBusClient {
    public FakeProcessor? LastProcessor { get; private set; }
    public FakeSessionProcessor? LastSessionProcessor { get; private set; }
    public FakeSender? LastSender { get; private set; }
    public Exception? SendException { get; set; }

    public override string FullyQualifiedNamespace => "unit-test.servicebus.windows.net";
    public override bool IsClosed => false;

    public override ServiceBusProcessor CreateProcessor(
      string topicName, string subscriptionName, ServiceBusProcessorOptions options) {
      LastProcessor = new FakeProcessor();
      return LastProcessor;
    }

    public override ServiceBusSessionProcessor CreateSessionProcessor(
      string topicName, string subscriptionName, ServiceBusSessionProcessorOptions options) {
      LastSessionProcessor = new FakeSessionProcessor();
      return LastSessionProcessor;
    }

    public override ServiceBusSender CreateSender(string queueOrTopicName) {
      LastSender = new FakeSender { SendException = SendException };
      return LastSender;
    }
  }

  /// <summary>
  /// ServiceBusProcessor whose event pipeline can be pumped synchronously via the SDK's
  /// OnProcessMessageAsync / OnProcessErrorAsync mocking hooks.
  /// </summary>
  private sealed class FakeProcessor : ServiceBusProcessor {
    private int _startCount;
    private int _stopCount;

    /// <summary>Accept-loop starts, including the throttle governor's resume.</summary>
    public int StartCount => Volatile.Read(ref _startCount);

    /// <summary>Accept-loop stops, including the throttle governor's pause.</summary>
    public int StopCount => Volatile.Read(ref _stopCount);

    public override Task StartProcessingAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _startCount);
      return Task.CompletedTask;
    }

    public override Task StopProcessingAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _stopCount);
      return Task.CompletedTask;
    }

    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RaiseMessageAsync(ProcessMessageEventArgs args) => OnProcessMessageAsync(args);

    public Task RaiseErrorAsync(ProcessErrorEventArgs args) => OnProcessErrorAsync(args);
  }

  /// <summary>
  /// ServiceBusSessionProcessor with a real inner processor (the SDK's session processor
  /// delegates event storage to the virtual InnerProcessor property), pumpable via the
  /// OnProcessSessionMessageAsync / OnProcessErrorAsync mocking hooks.
  /// </summary>
  private sealed class FakeSessionProcessor : ServiceBusSessionProcessor {
    private readonly InnerFakeProcessor _inner = new();

    private int _startCount;
    private int _stopCount;

    protected override ServiceBusProcessor InnerProcessor => _inner;

    /// <summary>Accept-loop starts, including the throttle governor's resume.</summary>
    public int StartCount => Volatile.Read(ref _startCount);

    /// <summary>Accept-loop stops, including the throttle governor's pause.</summary>
    public int StopCount => Volatile.Read(ref _stopCount);

    public override Task StartProcessingAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _startCount);
      return Task.CompletedTask;
    }

    public override Task StopProcessingAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _stopCount);
      return Task.CompletedTask;
    }

    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RaiseSessionMessageAsync(ProcessSessionMessageEventArgs args) => OnProcessSessionMessageAsync(args);

    public Task RaiseErrorAsync(ProcessErrorEventArgs args) => OnProcessErrorAsync(args);

    private sealed class InnerFakeProcessor : ServiceBusProcessor {
    }
  }

  /// <summary>
  /// Recording ServiceBusReceiver — captures Complete/Abandon/DeadLetter settlement calls
  /// and can inject settlement failures. Attempts are recorded before the injected
  /// exception is thrown so swallow behavior stays observable.
  /// </summary>
  private sealed class FakeReceiver : ServiceBusReceiver {
    public List<string> Completed { get; } = [];
    public List<string> Abandoned { get; } = [];
    public List<(string MessageId, string Reason, string? Description)> DeadLettered { get; } = [];
    public Exception? CompleteException { get; init; }
    public Exception? AbandonException { get; init; }
    public Exception? DeadLetterException { get; init; }

    public override Task CompleteMessageAsync(
      ServiceBusReceivedMessage message, CancellationToken cancellationToken = default) {
      Completed.Add(message.MessageId);
      return CompleteException is null ? Task.CompletedTask : Task.FromException(CompleteException);
    }

    public override Task AbandonMessageAsync(
      ServiceBusReceivedMessage message,
      IDictionary<string, object>? propertiesToModify = null,
      CancellationToken cancellationToken = default) {
      Abandoned.Add(message.MessageId);
      return AbandonException is null ? Task.CompletedTask : Task.FromException(AbandonException);
    }

    public override Task DeadLetterMessageAsync(
      ServiceBusReceivedMessage message,
      string deadLetterReason,
      string? deadLetterErrorDescription = null,
      CancellationToken cancellationToken = default) {
      DeadLettered.Add((message.MessageId, deadLetterReason, deadLetterErrorDescription));
      return DeadLetterException is null ? Task.CompletedTask : Task.FromException(DeadLetterException);
    }
  }

  /// <summary>Session-receiver counterpart of <see cref="FakeReceiver"/>.</summary>
  private sealed class FakeSessionReceiver : ServiceBusSessionReceiver {
    public List<string> Completed { get; } = [];
    public List<string> Abandoned { get; } = [];
    public List<(string MessageId, string Reason, string? Description)> DeadLettered { get; } = [];
    public Exception? DeadLetterException { get; init; }

    public override string SessionId => "unit-test-session";

    public override Task CompleteMessageAsync(
      ServiceBusReceivedMessage message, CancellationToken cancellationToken = default) {
      Completed.Add(message.MessageId);
      return Task.CompletedTask;
    }

    public override Task AbandonMessageAsync(
      ServiceBusReceivedMessage message,
      IDictionary<string, object>? propertiesToModify = null,
      CancellationToken cancellationToken = default) {
      Abandoned.Add(message.MessageId);
      return Task.CompletedTask;
    }

    public override Task DeadLetterMessageAsync(
      ServiceBusReceivedMessage message,
      string deadLetterReason,
      string? deadLetterErrorDescription = null,
      CancellationToken cancellationToken = default) {
      DeadLettered.Add((message.MessageId, deadLetterReason, deadLetterErrorDescription));
      return DeadLetterException is null ? Task.CompletedTask : Task.FromException(DeadLetterException);
    }
  }

  /// <summary>Recording ServiceBusSender with failure injection and dispose tracking.</summary>
  private sealed class FakeSender : ServiceBusSender {
    public List<ServiceBusMessage> Sent { get; } = [];
    public Exception? SendException { get; init; }
    public int DisposeCount { get; private set; }

    public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default) {
      if (SendException is not null) {
        return Task.FromException(SendException);
      }
      Sent.Add(message);
      return Task.CompletedTask;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Base ServiceBusSender.DisposeAsync() calls CloseAsync which NREs on mocking-constructor instances; this fake only records the call")]
    public override ValueTask DisposeAsync() {
      DisposeCount++;
      return ValueTask.CompletedTask;
    }
  }

  // ============================================================
  // Namespace throttle governor (ServiceBusy)
  // ============================================================
  //
  // ServiceBusy is a NAMESPACE condition, and the SDK answers it by retrying the accept
  // immediately across every concurrent session slot — which amplifies the throttle instead of
  // relieving it. Observed live as a fleet that could not accept a single session while thousands
  // of messages sat broker-side. The governor's job is to convert that storm into one backed-off
  // pause per streak: stop accepting, wait, resume.
  //
  // The governor is wired on the BATCH subscription paths, which are the ones that run with
  // concurrent accept slots; the single-message path takes the error handler without the
  // stop/start delegates and so never pauses.

  private static TransportBatchOptions _batchOptions() => new();

  /// <summary>
  /// A throttle error pauses the accept loop and then resumes it. Both halves matter: a pause
  /// that never resumes is a consumer that has quietly stopped receiving, which is strictly worse
  /// than the throttle it was shedding.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task BatchProcessorError_ServiceBusy_PausesThenResumesTheAcceptLoopAsync(
      CancellationToken cancellationToken) {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, _destination(), _batchOptions(), cancellationToken);
    var processor = client.LastProcessor!;
    var startsAfterSubscribe = processor.StartCount;

    await processor.RaiseErrorAsync(
      _errorArgs(new ServiceBusException("throttled", ServiceBusFailureReason.ServiceBusy)));

    // The pause runs detached so the error callback can return — StopProcessingAsync awaits
    // in-flight handlers, and this callback is one of them.
    while (processor.StopCount == 0 && !cancellationToken.IsCancellationRequested) {
      await Task.Delay(25, cancellationToken);
    }
    await Assert.That(processor.StopCount).IsGreaterThanOrEqualTo(1)
      .Because("shedding accept pressure is the whole response to a namespace throttle");

    while (processor.StartCount <= startsAfterSubscribe && !cancellationToken.IsCancellationRequested) {
      await Task.Delay(25, cancellationToken);
    }
    await Assert.That(processor.StartCount).IsGreaterThan(startsAfterSubscribe)
      .Because("a pause with no resume is an outage, not a backoff");
  }

  /// <summary>
  /// A burst of throttle errors produces ONE pause, not one per error.
  /// </summary>
  /// <remarks>
  /// This is the single-flight guard. Every concurrent accept slot sees the same namespace
  /// throttle at once, so without it a sixteen-slot consumer would stop and start the processor
  /// sixteen times over — churn that adds management-plane load to a namespace already refusing
  /// work.
  /// </remarks>
  [Test]
  [Timeout(60000)]
  public async Task BatchProcessorError_ServiceBusyBurst_PausesOnlyOnceAsync(
      CancellationToken cancellationToken) {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, _destination(), _batchOptions(), cancellationToken);
    var processor = client.LastProcessor!;

    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
      processor.RaiseErrorAsync(
        _errorArgs(new ServiceBusException("throttled", ServiceBusFailureReason.ServiceBusy)))));

    while (processor.StopCount == 0 && !cancellationToken.IsCancellationRequested) {
      await Task.Delay(25, cancellationToken);
    }
    // Give any duplicate pause a chance to land before counting.
    await Task.Delay(300, cancellationToken);

    await Assert.That(processor.StopCount).IsEqualTo(1)
      .Because("one namespace throttle is one pause however many slots reported it");
  }

  /// <summary>
  /// Errors that are not ServiceBusy never touch the accept loop.
  /// </summary>
  [Test]
  [Timeout(60000)]
  [Arguments(ServiceBusFailureReason.MessagingEntityNotFound)]
  [Arguments(ServiceBusFailureReason.MessageLockLost)]
  [Arguments(ServiceBusFailureReason.QuotaExceeded)]
  public async Task BatchProcessorError_NonThrottleReason_LeavesTheAcceptLoopRunningAsync(
      ServiceBusFailureReason reason, CancellationToken cancellationToken) {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, _destination(), _batchOptions(), cancellationToken);
    var processor = client.LastProcessor!;

    await processor.RaiseErrorAsync(_errorArgs(new ServiceBusException("not a throttle", reason)));
    await Task.Delay(300, cancellationToken);

    await Assert.That(processor.StopCount).IsEqualTo(0)
      .Because("pausing accepts for an unrelated error sheds throughput for no reason");
  }

  /// <summary>
  /// A non-Service-Bus exception never pauses either.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task BatchProcessorError_NonServiceBusException_LeavesTheAcceptLoopRunningAsync(
      CancellationToken cancellationToken) {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, _destination(), _batchOptions(), cancellationToken);
    var processor = client.LastProcessor!;

    await processor.RaiseErrorAsync(_errorArgs(new InvalidOperationException("unrelated")));
    await Task.Delay(300, cancellationToken);

    await Assert.That(processor.StopCount).IsEqualTo(0);
  }

  /// <summary>
  /// The session batch processor gets the same governor — sessions are where the amplification
  /// was actually observed, since each concurrent session slot retries its accept independently.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task SessionBatchProcessorError_ServiceBusy_PausesThenResumesTheAcceptLoopAsync(
      CancellationToken cancellationToken) {
    var (transport, client) = _createTransport(enableSessions: true);
    await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, _destination(), _batchOptions(), cancellationToken);
    var processor = client.LastSessionProcessor!;
    var startsAfterSubscribe = processor.StartCount;

    await processor.RaiseErrorAsync(
      _errorArgs(new ServiceBusException("throttled", ServiceBusFailureReason.ServiceBusy)));

    while (processor.StopCount == 0 && !cancellationToken.IsCancellationRequested) {
      await Task.Delay(25, cancellationToken);
    }
    await Assert.That(processor.StopCount).IsGreaterThanOrEqualTo(1);

    while (processor.StartCount <= startsAfterSubscribe && !cancellationToken.IsCancellationRequested) {
      await Task.Delay(25, cancellationToken);
    }
    await Assert.That(processor.StartCount).IsGreaterThan(startsAfterSubscribe);
  }

  /// <summary>
  /// ServiceBusy drives the connection-recovery handler as well as the pause — the two responses
  /// are independent, and both fire.
  /// </summary>
  /// <remarks>
  /// Worth pinning explicitly because it is not the obvious design: ServiceBusy is back-pressure
  /// rather than a broken connection, so classifying it as a connection error is a deliberate
  /// choice. Dropping it from that list would silently remove the recovery half while every
  /// throttle test here still passed on the pause half alone.
  /// </remarks>
  [Test]
  [Timeout(60000)]
  public async Task BatchProcessorError_ServiceBusy_AlsoInvokesTheRecoveryHandlerAsync(
      CancellationToken cancellationToken) {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, _destination(), _batchOptions(), cancellationToken);
    var recoveryInvocations = 0;
    transport.SetRecoveryHandler(_ => { recoveryInvocations++; return Task.CompletedTask; });

    await client.LastProcessor!.RaiseErrorAsync(
      _errorArgs(new ServiceBusException("throttled", ServiceBusFailureReason.ServiceBusy)));

    await Assert.That(recoveryInvocations).IsEqualTo(1)
      .Because("ServiceBusy is classified as a connection error, so recovery runs alongside the pause");
  }

  /// <summary>
  /// The single-message subscription path takes the error handler without stop/start delegates,
  /// so a throttle there is logged and recovered from but never pauses the accept loop.
  /// </summary>
  /// <remarks>
  /// Pinned so the asymmetry is visible rather than assumed. The governor exists for concurrent
  /// accept slots, which is the batch path; if the single path ever grows concurrency it needs
  /// the delegates too, and this test is where that shows up.
  /// </remarks>
  [Test]
  [Timeout(60000)]
  public async Task SingleMessageProcessorError_ServiceBusy_DoesNotPauseTheAcceptLoopAsync(
      CancellationToken cancellationToken) {
    var (transport, client) = _createTransport(enableSessions: false);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination(), cancellationToken);
    var processor = client.LastProcessor!;

    await processor.RaiseErrorAsync(
      _errorArgs(new ServiceBusException("throttled", ServiceBusFailureReason.ServiceBusy)));
    await Task.Delay(300, cancellationToken);

    await Assert.That(processor.StopCount).IsEqualTo(0)
      .Because("the single-message path has no concurrent accept slots to shed, so it is wired "
             + "without the pause delegates");
  }
}
