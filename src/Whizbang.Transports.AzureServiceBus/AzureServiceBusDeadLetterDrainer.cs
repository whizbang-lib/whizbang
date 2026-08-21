using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Drains one ASB subscription's dead-letter queue by IMPORTING each message into Whizbang's
/// durable dead-letter custody (<c>wh_dead_letters</c>, via
/// <c>IWorkCoordinator.ImportBrokerDeadLetterAsync</c>) and then completing it at the broker.
/// One hop, no topic re-broadcast, and no broker carousel: once imported, retry belongs to the
/// dead-letter recovery flow (per-reason policies, operator disposition, generation-tagged
/// auto-replay), and a message the current build still cannot process re-parks VISIBLY instead
/// of orbiting the broker's opaque DLQ.
/// </summary>
/// <remarks>
/// The import is raw-JSON custody: the wire body travels verbatim and nothing here deserializes
/// it — a message that cannot be deserialized is precisely the one that needs custody. Only
/// messages whose broker <c>MessageId</c> parses as a GUID are imported (Whizbang publishes
/// envelope ids as the broker MessageId); foreign messages are abandoned and stay on the broker
/// DLQ for their owner's tooling.
/// </remarks>
/// <docs>operations/dead-letter-queue/transport-recovery</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusDeadLetterDrainerTests.cs</tests>
public sealed class AzureServiceBusDeadLetterDrainer : ITransportDeadLetterDrainer, IAsyncDisposable {
  private readonly ServiceBusClient _client;
  private readonly string _topicName;
  private readonly string _subscriptionName;
  private readonly Func<BrokerDeadLetterImport, CancellationToken, Task<bool>> _importAsync;
  private readonly ILogger<AzureServiceBusDeadLetterDrainer> _logger;
  private ServiceBusReceiver? _receiver;
  private readonly SemaphoreSlim _lock = new(1, 1);
  private bool _disposed;

  /// <summary>
  /// Creates a drainer bound to a single ASB subscription.
  /// </summary>
  /// <param name="client">Shared ServiceBusClient. Lifetime owned by DI; not disposed here.</param>
  /// <param name="topicName">Topic that owns the subscription.</param>
  /// <param name="subscriptionName">Subscription whose DLQ to drain.</param>
  /// <param name="importAsync">Custody seam — typically wraps
  ///   <c>IWorkCoordinator.ImportBrokerDeadLetterAsync</c>. Returns <c>true</c> when a custody row
  ///   was created, <c>false</c> for a duplicate (already imported — still safe to settle), and
  ///   THROWS on failure so the message is abandoned and re-offered next pass.</param>
  /// <param name="logger">Logger.</param>
  public AzureServiceBusDeadLetterDrainer(
    ServiceBusClient client,
    string topicName,
    string subscriptionName,
    Func<BrokerDeadLetterImport, CancellationToken, Task<bool>> importAsync,
    ILogger<AzureServiceBusDeadLetterDrainer> logger) {
    _client = client ?? throw new ArgumentNullException(nameof(client));
    _topicName = !string.IsNullOrWhiteSpace(topicName) ? topicName
      : throw new ArgumentException("Topic name required", nameof(topicName));
    _subscriptionName = !string.IsNullOrWhiteSpace(subscriptionName) ? subscriptionName
      : throw new ArgumentException("Subscription name required", nameof(subscriptionName));
    _importAsync = importAsync ?? throw new ArgumentNullException(nameof(importAsync));
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

    var receiver = await _getOrCreateReceiverAsync(ct).ConfigureAwait(false);

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

        if (!TryBuildImport(msg, _topicName, _subscriptionName, out var import)) {
#pragma warning disable CA1848, CA1873
          _logger.LogWarning(
            "ASB DLQ drain skipped message {MessageId} from topic={Topic}/sub={Sub}: MessageId is not a Whizbang wire id (GUID) — abandoning, message stays on the broker DLQ",
            msg.MessageId, _topicName, _subscriptionName);
#pragma warning restore CA1848, CA1873
          await _abandonGuardedAsync(receiver, msg, ct).ConfigureAwait(false);
          continue;
        }

        try {
          // FALSE = duplicate custody (already imported) — still settle so the broker copy
          // stops occupying the DLQ. Failures THROW and route to the abandon arm.
          var imported = await _importAsync(import, ct).ConfigureAwait(false);
          await receiver.CompleteMessageAsync(msg, ct).ConfigureAwait(false);
          drained++;
#pragma warning disable CA1848, CA1873
          _logger.LogInformation(
            "ASB DLQ imported message {MessageId} from topic={Topic}/sub={Sub} into wh_dead_letters (duplicate={Duplicate}, brokerReason={Reason})",
            msg.MessageId, _topicName, _subscriptionName, !imported, msg.DeadLetterReason);
#pragma warning restore CA1848, CA1873
        } catch (OperationCanceledException) {
          throw;
        } catch (Exception ex) {
#pragma warning disable CA1848, CA1873
          _logger.LogWarning(ex,
            "ASB DLQ import failed for message {MessageId} from topic={Topic}/sub={Sub} — abandoning, re-offered next pass",
            msg.MessageId, _topicName, _subscriptionName);
#pragma warning restore CA1848, CA1873
          await _abandonGuardedAsync(receiver, msg, ct).ConfigureAwait(false);
        }
      }
    }

    return drained;
  }

  /// <summary>
  /// Maps a broker message to the import record — pure metadata + raw body, no deserialization.
  /// Returns false when the broker MessageId is not a GUID (not a Whizbang wire message).
  /// Internal for direct regression testing of the mapping.
  /// </summary>
  internal static bool TryBuildImport(
      ServiceBusReceivedMessage msg, string topicName, string subscriptionName,
      out BrokerDeadLetterImport import) {
    if (!Guid.TryParse(msg.MessageId, out var messageId)) {
      import = null!;
      return false;
    }
    Guid? streamId = Guid.TryParse(msg.SessionId, out var sid) ? sid : null;
    string? messageType =
      msg.ApplicationProperties.TryGetValue("EnvelopeType", out var et) ? et as string : null;
    import = new BrokerDeadLetterImport(
      MessageId: messageId,
      StreamId: streamId,
      MessageType: messageType,
      Destination: $"{topicName}/{subscriptionName}",
      EnvelopeJson: msg.Body.ToString(),
      BrokerReason: msg.DeadLetterReason,
      BrokerDescription: msg.DeadLetterErrorDescription,
      EnqueuedAt: msg.EnqueuedTime == default ? null : msg.EnqueuedTime,
      DeliveryCount: msg.DeliveryCount);
    return true;
  }

  private static async Task _abandonGuardedAsync(
      ServiceBusReceiver receiver, ServiceBusReceivedMessage msg, CancellationToken ct) {
    try {
      await receiver.AbandonMessageAsync(msg, cancellationToken: ct).ConfigureAwait(false);
    } catch (Exception abandonEx) when (abandonEx is ServiceBusException or ObjectDisposedException) {
      // Abandon failures on a lost lock are expected — broker will re-deliver naturally.
    }
  }

  private async Task<ServiceBusReceiver> _getOrCreateReceiverAsync(CancellationToken ct) {
    if (_receiver is not null) {
      return _receiver;
    }
    await _lock.WaitAsync(ct).ConfigureAwait(false);
    try {
      _receiver ??= _client.CreateReceiver(_topicName, _subscriptionName, new ServiceBusReceiverOptions {
        SubQueue = SubQueue.DeadLetter,
        ReceiveMode = ServiceBusReceiveMode.PeekLock,
      });
      return _receiver;
    } finally {
      _lock.Release();
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    if (_receiver is not null) {
      await _receiver.DisposeAsync().ConfigureAwait(false);
      _receiver = null;
    }
    _lock.Dispose();
  }
}
