// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// An Azure Service Bus subscription reports a disconnect when its processor hits a failure it will not recover from on
/// its own (#1838): the topic or subscription is gone or disabled, the identity is not authorized, or the processor
/// was disposed underneath the subscription. Errors the SDK retries itself (transient ones), per-message settlement
/// errors and errors from the application's own handler are not disconnects. A disconnect is reported at most once per
/// subscription, and never once the application has paused or disposed the subscription or disposed the transport.
/// </summary>
/// <remarks>
/// The SDK's processor mocking surface raises each error inline through the transport's own error handler, so every
/// report is decided before the raise returns; nothing waits on a clock or another thread.
/// </remarks>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusTransport.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusSubscription.cs</code-under-test>
[Timeout(10_000)]
public class AzureServiceBusProcessorStopsReceivingTests {
  /// <summary>The four ways the transport builds a processor, each of which attaches its own error handler.</summary>
  public enum SubscribePath {
    Message,
    MessageSession,
    Batch,
    BatchSession,
  }

  private const string TOPIC = "stops-topic";
  private const string SUBSCRIPTION = "stops-sub";

  [Test]
  [Arguments(SubscribePath.Message, ServiceBusFailureReason.MessagingEntityNotFound)]
  [Arguments(SubscribePath.Message, ServiceBusFailureReason.MessagingEntityDisabled)]
  [Arguments(SubscribePath.MessageSession, ServiceBusFailureReason.MessagingEntityNotFound)]
  [Arguments(SubscribePath.MessageSession, ServiceBusFailureReason.MessagingEntityDisabled)]
  [Arguments(SubscribePath.Batch, ServiceBusFailureReason.MessagingEntityNotFound)]
  [Arguments(SubscribePath.Batch, ServiceBusFailureReason.MessagingEntityDisabled)]
  [Arguments(SubscribePath.BatchSession, ServiceBusFailureReason.MessagingEntityNotFound)]
  [Arguments(SubscribePath.BatchSession, ServiceBusFailureReason.MessagingEntityDisabled)]
  public async Task ProcessorError_EntityGoneOrDisabled_ReportsAnUnexpectedDisconnectAsync(
      SubscribePath path, ServiceBusFailureReason reason) {
    var harness = await Harness.SubscribeAsync(path);
    var error = new ServiceBusException("the entity cannot receive", reason);

    await harness.RaiseAsync(error, ServiceBusErrorSource.Receive);

    await Assert.That(harness.Reported.Count).IsEqualTo(1)
      .Because("a processor that will not recover on its own must report the disconnect, once");
    var reported = harness.Reported.Single();
    await Assert.That(reported.Exception).IsSameReferenceAs(error);
    await Assert.That(reported.IsApplicationInitiated).IsFalse()
      .Because("the broker took the entity away; the application asked for nothing");
    await Assert.That(reported.Reason).Contains(reason.ToString());
    await Assert.That(reported.Reason).Contains($"{TOPIC}/{SUBSCRIPTION}")
      .Because("the reason names the topic and subscription that stopped receiving");
    await Assert.That(harness.Subscription.Disconnection).IsSameReferenceAs(reported)
      .Because("the disconnect is recorded before it is raised, for a handler attached later (#1831)");
  }

  [Test]
  public async Task ProcessorError_Unauthorized_ReportsAnUnexpectedDisconnectAsync() {
    var harness = await Harness.SubscribeAsync(SubscribePath.Message);
    var error = new UnauthorizedAccessException("the identity lost its Listen right");

    await harness.RaiseAsync(error, ServiceBusErrorSource.Receive);

    await Assert.That(harness.Reported.Count).IsEqualTo(1)
      .Because("a processor that will not recover on its own must report the disconnect, once");
    await Assert.That(harness.Reported.Single().Exception).IsSameReferenceAs(error);
    await Assert.That(harness.Reported.Single().Reason).Contains("Unauthorized");
  }

  [Test]
  [Arguments(SubscribePath.Message, ServiceBusErrorSource.Receive)]
  [Arguments(SubscribePath.MessageSession, ServiceBusErrorSource.AcceptSession)]
  public async Task ProcessorError_ProcessorDisposedUnderneathTheSubscription_ReportsAnUnexpectedDisconnectAsync(
      SubscribePath path, ServiceBusErrorSource source) {
    var harness = await Harness.SubscribeAsync(path);
    var error = new ObjectDisposedException("ServiceBusReceiver");

    await harness.RaiseAsync(error, source);

    await Assert.That(harness.Reported.Count).IsEqualTo(1)
      .Because("a processor that will not recover on its own must report the disconnect, once");
    await Assert.That(harness.Reported.Single().Exception).IsSameReferenceAs(error);
    await Assert.That(harness.Reported.Single().Reason).Contains("disposed");
  }

  [Test]
  [Arguments(ServiceBusFailureReason.ServiceCommunicationProblem)]
  [Arguments(ServiceBusFailureReason.ServiceBusy)]
  [Arguments(ServiceBusFailureReason.ServiceTimeout)]
  [Arguments(ServiceBusFailureReason.MessageLockLost)]
  [Arguments(ServiceBusFailureReason.SessionLockLost)]
  [Arguments(ServiceBusFailureReason.MessageNotFound)]
  [Arguments(ServiceBusFailureReason.SessionCannotBeLocked)]
  [Arguments(ServiceBusFailureReason.QuotaExceeded)]
  [Arguments(ServiceBusFailureReason.GeneralError)]
  public async Task ProcessorError_AFailureTheProcessorRecoversFrom_IsNotADisconnectAsync(ServiceBusFailureReason reason) {
    var harness = await Harness.SubscribeAsync(SubscribePath.Message);

    await harness.RaiseAsync(new ServiceBusException("the processor keeps going", reason), ServiceBusErrorSource.Receive);

    await Assert.That(harness.Reported).IsEmpty()
      .Because("the SDK retries this itself; a reconnect would only churn a processor that is still receiving");
    await Assert.That(harness.Subscription.Disconnection).IsNull();
  }

  [Test]
  public async Task ProcessorError_EntityNotFoundTheServiceMarksTransient_IsNotADisconnectAsync() {
    var harness = await Harness.SubscribeAsync(SubscribePath.Message);
    var error = new ServiceBusException(
      isTransient: true, "briefly missing", TOPIC, ServiceBusFailureReason.MessagingEntityNotFound, innerException: null);

    await harness.RaiseAsync(error, ServiceBusErrorSource.Receive);

    await Assert.That(harness.Reported).IsEmpty()
      .Because("an error the SDK marks transient is one it retries");
  }

  [Test]
  [Arguments(ServiceBusErrorSource.ProcessMessageCallback)]
  [Arguments(ServiceBusErrorSource.Complete)]
  [Arguments(ServiceBusErrorSource.Abandon)]
  [Arguments(ServiceBusErrorSource.RenewLock)]
  [Arguments(ServiceBusErrorSource.CloseSession)]
  public async Task ProcessorError_FromAnOperationOtherThanReceiving_IsNotADisconnectAsync(ServiceBusErrorSource source) {
    var harness = await Harness.SubscribeAsync(SubscribePath.Message);

    await harness.RaiseAsync(new ObjectDisposedException("a scoped service"), source);
    await harness.RaiseAsync(
      new ServiceBusException("settled elsewhere", ServiceBusFailureReason.MessagingEntityNotFound), source);

    await Assert.That(harness.Reported).IsEmpty()
      .Because("a failed handler, settlement or lock renewal concerns one message; the processor still receives");
  }

  [Test]
  public async Task ProcessorError_AnyOtherException_IsNotADisconnectAsync() {
    var harness = await Harness.SubscribeAsync(SubscribePath.Message);

    await harness.RaiseAsync(new InvalidOperationException("unclassified"), ServiceBusErrorSource.Receive);

    await Assert.That(harness.Reported).IsEmpty();
  }

  [Test]
  public async Task ProcessorError_RepeatedTerminalErrors_ReportOneDisconnectAsync() {
    // The processor keeps looping on a missing entity and reports each failed receive; the subscription is replaced
    // once, so it reports once.
    var harness = await Harness.SubscribeAsync(SubscribePath.Message);
    var first = new ServiceBusException("gone", ServiceBusFailureReason.MessagingEntityNotFound);

    await harness.RaiseAsync(first, ServiceBusErrorSource.Receive);
    await harness.RaiseAsync(new ServiceBusException("still gone", ServiceBusFailureReason.MessagingEntityNotFound), ServiceBusErrorSource.Receive);
    await harness.RaiseAsync(new UnauthorizedAccessException("and now forbidden"), ServiceBusErrorSource.Receive);

    await Assert.That(harness.Reported.Count).IsEqualTo(1)
      .Because("a second report would start a second reconnect for one lost subscription");
    await Assert.That(harness.Reported[0].Exception).IsSameReferenceAs(first);
  }

  [Test]
  public async Task ProcessorError_AfterTheApplicationPausedTheSubscription_IsNotADisconnectAsync() {
    // A reconnect would hand back an active subscription and silently undo the pause.
    var harness = await Harness.SubscribeAsync(SubscribePath.Message);
    await harness.Subscription.PauseAsync();

    await harness.RaiseAsync(new ServiceBusException("gone", ServiceBusFailureReason.MessagingEntityNotFound), ServiceBusErrorSource.Receive);

    await Assert.That(harness.Reported).IsEmpty();
  }

  [Test]
  public async Task ProcessorError_AfterTheApplicationDisposedTheSubscription_IsNotADisconnectAsync() {
    var harness = await Harness.SubscribeAsync(SubscribePath.Message);
    harness.Subscription.Dispose();

    await harness.RaiseAsync(new ObjectDisposedException("ServiceBusReceiver"), ServiceBusErrorSource.Receive);

    await Assert.That(harness.Reported).IsEmpty()
      .Because("the application stopped this subscription itself");
  }

  [Test]
  public async Task ProcessorError_AfterTheTransportIsDisposed_IsNotADisconnectAsync() {
    var harness = await Harness.SubscribeAsync(SubscribePath.Message);
    await harness.Transport.DisposeAsync();

    await harness.RaiseAsync(new ObjectDisposedException("ServiceBusClient"), ServiceBusErrorSource.Receive);

    await Assert.That(harness.Reported).IsEmpty()
      .Because("the processor fails because the application is shutting the transport down");
  }

  // ============================================================
  // Harness
  // ============================================================

  private sealed class Harness {
    private readonly RaisableServiceBusClient _client;
    private readonly SubscribePath _path;

    private Harness(RaisableServiceBusClient client, AzureServiceBusTransport transport, SubscribePath path) {
      _client = client;
      Transport = transport;
      _path = path;
    }

    public AzureServiceBusTransport Transport { get; }
    public ISubscription Subscription { get; private set; } = null!;
    public List<SubscriptionDisconnectedEventArgs> Reported { get; } = [];

    public static async Task<Harness> SubscribeAsync(SubscribePath path) {
      var client = new RaisableServiceBusClient();
      var sessions = path is SubscribePath.MessageSession or SubscribePath.BatchSession;
      var transport = new AzureServiceBusTransport(
        client,
        AsbTransportTestData.CombinedOptions,
        new AzureServiceBusOptions { AutoProvisionInfrastructure = false, EnableSessions = sessions },
        NullLogger<AzureServiceBusTransport>.Instance,
        adminClient: null);
      var harness = new Harness(client, transport, path);
      var destination = new TransportDestination(TOPIC, SUBSCRIPTION);
      harness.Subscription = path is SubscribePath.Message or SubscribePath.MessageSession
        ? await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, destination)
        : await transport.SubscribeBatchAsync((_, _) => Task.CompletedTask, destination, new TransportBatchOptions());
      harness.Subscription.OnDisconnected += (_, args) => harness.Reported.Add(args);
      return harness;
    }

    public Task RaiseAsync(Exception error, ServiceBusErrorSource source) {
      var args = new ProcessErrorEventArgs(
        error, source, "unit-test.servicebus.windows.net", $"{TOPIC}/Subscriptions/{SUBSCRIPTION}", CancellationToken.None);
      return _path is SubscribePath.MessageSession or SubscribePath.BatchSession
        ? _client.LastSessionProcessor!.RaiseErrorAsync(args)
        : _client.LastProcessor!.RaiseErrorAsync(args);
    }
  }
}
