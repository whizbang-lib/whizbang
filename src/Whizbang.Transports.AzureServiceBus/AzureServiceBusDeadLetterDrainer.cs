using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// v0.502 slice C.8 — drains the Azure Service Bus broker-side
/// <c>$DeadLetterQueue</c> sub-queue for a single (topic, subscription) pair by re-sending
/// each DLQ message back onto the topic. Symmetric with
/// <see cref="Whizbang.Transports.RabbitMQ.RabbitMqDeadLetterDrainer"/>.
/// </summary>
/// <remarks>
/// <para>
/// One instance per subscription. Registered in DI alongside
/// <see cref="AzureServiceBusTransport"/>. The
/// <see cref="Whizbang.Core.Workers.TransportDeadLetterDrainWorker"/> resolves all registered
/// drainers on every backstop tick (default 10 min) and invokes
/// <see cref="DrainDeadLetterQueueAsync"/> on each.
/// </para>
/// <para>
/// Re-send semantics: each DLQ message becomes a fresh outbound
/// <see cref="ServiceBusMessage"/> with body, application properties, and routing fields
/// (subject/correlation/messageId) copied. The DeliveryCount resets on the new message — the
/// next attempt at the receiver starts fresh. If the underlying defect is permanent (bad
/// envelope type, missing receptor) the new message lands in DLQ again; the worker's
/// re-attempt rate is capped by <see cref="Whizbang.Core.Workers.TransportDeadLetterDrainWorkerOptions.IntervalMinutes"/>.
/// </para>
/// </remarks>
/// <docs>operations/dead-letter-queue/transport-recovery</docs>
public sealed class AzureServiceBusDeadLetterDrainer : ITransportDeadLetterDrainer, IAsyncDisposable {
  private readonly ServiceBusClient _client;
  private readonly string _topicName;
  private readonly string _subscriptionName;
  private readonly ILogger<AzureServiceBusDeadLetterDrainer> _logger;
  private ServiceBusReceiver? _receiver;
  private ServiceBusSender? _sender;
  private readonly SemaphoreSlim _lock = new(1, 1);
  private bool _disposed;

  /// <summary>
  /// Creates a drainer bound to a single ASB subscription.
  /// </summary>
  /// <param name="client">Shared ServiceBusClient. Lifetime owned by DI; not disposed here.</param>
  /// <param name="topicName">Topic that owns the subscription.</param>
  /// <param name="subscriptionName">Subscription whose DLQ to drain.</param>
  /// <param name="logger">Logger.</param>
  public AzureServiceBusDeadLetterDrainer(
    ServiceBusClient client,
    string topicName,
    string subscriptionName,
    ILogger<AzureServiceBusDeadLetterDrainer> logger) {
    _client = client ?? throw new ArgumentNullException(nameof(client));
    _topicName = !string.IsNullOrWhiteSpace(topicName) ? topicName
      : throw new ArgumentException("Topic name required", nameof(topicName));
    _subscriptionName = !string.IsNullOrWhiteSpace(subscriptionName) ? subscriptionName
      : throw new ArgumentException("Subscription name required", nameof(subscriptionName));
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureServiceBusDeadLetterDrainer>.Instance;
  }

  /// <inheritdoc />
  public string TransportName => $"asb:{_topicName}/{_subscriptionName}";

  /// <inheritdoc />
  public async Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (maxCount <= 0) {
      return 0;
    }

    var (receiver, sender) = await _getOrCreateReceiverAndSenderAsync(ct).ConfigureAwait(false);

    int drained = 0;
    while (drained < maxCount && !ct.IsCancellationRequested) {
      // ReceiveMessagesAsync with a short wait — when the DLQ is empty the call returns
      // immediately with an empty list. Pull in small batches so cancellation responds
      // quickly even if a DLQ has thousands of messages.
      var batchSize = Math.Min(maxCount - drained, 100);
      var batch = await receiver.ReceiveMessagesAsync(
        maxMessages: batchSize,
        maxWaitTime: TimeSpan.FromSeconds(2),
        cancellationToken: ct).ConfigureAwait(false);
      if (batch is null || batch.Count == 0) {
        break;
      }

      foreach (var msg in batch) {
        ct.ThrowIfCancellationRequested();
        try {
          var resend = CloneForResend(msg);
          await sender.SendMessageAsync(resend, ct).ConfigureAwait(false);
          await receiver.CompleteMessageAsync(msg, ct).ConfigureAwait(false);
          drained++;
#pragma warning disable CA1848, CA1873
          _logger.LogInformation(
            "ASB DLQ drained message {MessageId} from topic={Topic}/sub={Sub} (deadLetterReason={Reason})",
            msg.MessageId, _topicName, _subscriptionName, msg.DeadLetterReason);
#pragma warning restore CA1848, CA1873
        } catch (Exception ex) {
#pragma warning disable CA1848, CA1873
          _logger.LogWarning(ex,
            "ASB DLQ drain failed for message {MessageId} from topic={Topic}/sub={Sub} — abandoning",
            msg.MessageId, _topicName, _subscriptionName);
#pragma warning restore CA1848, CA1873
          try {
            await receiver.AbandonMessageAsync(msg, cancellationToken: ct).ConfigureAwait(false);
          } catch (Exception abandonEx) when (abandonEx is ServiceBusException or ObjectDisposedException) {
            // Abandon failures on a lost lock are expected — broker will re-deliver naturally.
          }
        }
      }
    }

    return drained;
  }

  /// <summary>
  /// Builds the outbound message that re-enters the topic. Internal for direct
  /// regression testing — ServiceBusReceivedMessage is opaque enough that asserting
  /// the clone shape on the public DrainDeadLetterQueueAsync flow would need a real broker.
  /// </summary>
  internal static ServiceBusMessage CloneForResend(ServiceBusReceivedMessage msg) {
    var resend = new ServiceBusMessage(msg.Body) {
      MessageId = msg.MessageId,
      ContentType = msg.ContentType,
      CorrelationId = msg.CorrelationId,
      Subject = msg.Subject,
      To = msg.To,
      ReplyTo = msg.ReplyTo,
      ReplyToSessionId = msg.ReplyToSessionId,
      SessionId = msg.SessionId,
      PartitionKey = msg.PartitionKey,
    };
    foreach (var kv in msg.ApplicationProperties) {
      resend.ApplicationProperties[kv.Key] = kv.Value;
    }
    return resend;
  }

  private async Task<(ServiceBusReceiver Receiver, ServiceBusSender Sender)> _getOrCreateReceiverAndSenderAsync(CancellationToken ct) {
    if (_receiver is not null && _sender is not null) {
      return (_receiver, _sender);
    }
    await _lock.WaitAsync(ct).ConfigureAwait(false);
    try {
      _receiver ??= _client.CreateReceiver(_topicName, _subscriptionName, new ServiceBusReceiverOptions {
        SubQueue = SubQueue.DeadLetter,
        ReceiveMode = ServiceBusReceiveMode.PeekLock,
      });
      _sender ??= _client.CreateSender(_topicName);
    } finally {
      _lock.Release();
    }
    return (_receiver, _sender);
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    if (_receiver is not null) {
      await _receiver.DisposeAsync().ConfigureAwait(false);
    }
    if (_sender is not null) {
      await _sender.DisposeAsync().ConfigureAwait(false);
    }
    _lock.Dispose();
  }
}
