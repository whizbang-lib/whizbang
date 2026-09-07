using System.Diagnostics.CodeAnalysis;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Coverage for the one branch <see cref="AzureServiceBusDeadLetterDrainerTests"/> doesn't reach:
/// the custody import seam itself throwing <see cref="OperationCanceledException"/> (as opposed to
/// an ordinary import failure, which that suite already drives through the abandon path). Uses the
/// same mockable-SDK-fake seam as the existing suite — <c>ServiceBusClient</c> and
/// <c>ServiceBusReceiver</c> both expose a protected parameterless constructor for mocking, so no
/// broker is involved anywhere in this file.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusDeadLetterDrainer.cs</code-under-test>
public class AzureServiceBusDeadLetterDrainerCoverageTests {

  private const string MESSAGE_ID = "00000000-0000-0000-0000-000000000001";

  // A drain worker's per-tick time budget cancels the token it hands to the import seam; if that
  // cancellation races the custody write, the message must NOT be abandoned as though the import
  // failed for an ordinary reason. Falling into the ordinary-failure abandon path would settle a
  // message whose custody outcome is actually unknown -- the cancellation has to propagate as-is so
  // the caller's own cancellation handling decides what happens next, not the per-message retry logic.
  [Test]
  public async Task DrainDeadLetterQueueAsync_ImportThrowsOperationCanceled_PropagatesWithoutAbandoningOrCompletingAsync() {
    var client = new _fakeDrainClient();
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage(MESSAGE_ID) });
    static Task<bool> throwingImport(BrokerDeadLetterImport import, CancellationToken ct) =>
      Task.FromException<bool>(new OperationCanceledException("import canceled"));
    await using var drainer = new AzureServiceBusDeadLetterDrainer(
      client, "orders", "billing", throwingImport, NullLogger<AzureServiceBusDeadLetterDrainer>.Instance);

    await Assert.That(async () => await drainer.DrainDeadLetterQueueAsync(maxCount: 10))
      .Throws<OperationCanceledException>();

    await Assert.That(client.Receiver.Abandoned).IsEmpty()
      .Because("a canceled import must rethrow, not fall into the ordinary-failure abandon path");
    await Assert.That(client.Receiver.Completed).IsEmpty()
      .Because("a canceled import never reached the point where custody succeeded, so nothing settles");
  }

  private static ServiceBusReceivedMessage _dlqMessage(string messageId) =>
    ServiceBusModelFactory.ServiceBusReceivedMessage(
      body: BinaryData.FromString("dlq-body"),
      messageId: messageId,
      properties: new Dictionary<string, object> {
        ["EnvelopeType"] = "Whizbang.Test.Envelope",
        ["DeadLetterReason"] = "TestReason",
      });

  private sealed class _fakeDrainClient : ServiceBusClient {
    public _fakeDlqReceiver Receiver { get; } = new();

    public override ServiceBusReceiver CreateReceiver(
      string topicName, string subscriptionName, ServiceBusReceiverOptions options) => Receiver;
  }

  private sealed class _fakeDlqReceiver : ServiceBusReceiver {
    public Queue<IReadOnlyList<ServiceBusReceivedMessage>?> Batches { get; } = new();
    public List<string> Completed { get; } = [];
    public List<string> Abandoned { get; } = [];

    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(
      int maxMessages, TimeSpan? maxWaitTime = default, CancellationToken cancellationToken = default) {
      if (Batches.Count == 0) {
        return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([]);
      }
      var batch = Batches.Dequeue();
      return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(batch!);
    }

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

    [SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Base ServiceBusReceiver.DisposeAsync() calls CloseAsync which NREs on mocking-constructor instances; this fake only records the call")]
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}
