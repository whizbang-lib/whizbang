using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Drains one RabbitMQ dead-letter queue by IMPORTING each message into Whizbang's durable
/// dead-letter custody (<c>wh_dead_letters</c>, via
/// <c>IWorkCoordinator.ImportBrokerDeadLetterAsync</c>) and then acking it at the broker. One
/// hop, no re-publish, and no broker carousel: once imported, retry belongs to the dead-letter
/// recovery flow (per-reason policies, operator disposition, generation-tagged auto-replay), and
/// a message the current build still cannot process re-parks VISIBLY instead of orbiting the
/// broker DLQ.
/// </summary>
/// <remarks>
/// The import is raw custody: the wire body travels verbatim and nothing here deserializes it.
/// Only messages whose <c>MessageId</c> parses as a GUID are imported (Whizbang publishes
/// envelope ids as the AMQP MessageId); a foreign message is nacked back with requeue and the
/// pass ENDS — RabbitMQ's single-message poll would otherwise re-fetch the same requeued head
/// forever within one pass.
/// </remarks>
/// <docs>operations/dead-letter-queue/transport-recovery</docs>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMqDeadLetterDrainerTests.cs</tests>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Integration.Tests/RabbitMqHostRegistrationIntegrationTests.cs</tests>
public sealed class RabbitMqDeadLetterDrainer : ITransportDeadLetterDrainer {
  private readonly IConnection _connection;
  private readonly string _dlqName;
  private readonly Func<BrokerDeadLetterImport, CancellationToken, Task<bool>> _importAsync;
  private readonly ILogger<RabbitMqDeadLetterDrainer> _logger;

  /// <summary>Creates a drainer bound to a single dead-letter queue.</summary>
  /// <param name="connection">Shared broker connection. Lifetime owned by DI; not disposed here.</param>
  /// <param name="dlqName">The dead-letter queue to drain (convention: <c>{queue}.dlq</c>).</param>
  /// <param name="importAsync">Custody seam — wraps <c>IWorkCoordinator.ImportBrokerDeadLetterAsync</c>.
  ///   Returns <c>true</c> when a custody row was created, <c>false</c> for a duplicate (still safe
  ///   to ack), and THROWS on failure so the message is requeued for the next pass.</param>
  /// <param name="logger">Logger.</param>
  public RabbitMqDeadLetterDrainer(
    IConnection connection,
    string dlqName,
    Func<BrokerDeadLetterImport, CancellationToken, Task<bool>> importAsync,
    ILogger<RabbitMqDeadLetterDrainer> logger) {
    _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    _dlqName = !string.IsNullOrWhiteSpace(dlqName) ? dlqName
      : throw new ArgumentException("DLQ name required", nameof(dlqName));
    _importAsync = importAsync ?? throw new ArgumentNullException(nameof(importAsync));
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RabbitMqDeadLetterDrainer>.Instance;
  }

  /// <inheritdoc />
  public string TransportName => $"rmq:{_dlqName}";

  /// <inheritdoc />
  public async Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default) {
    if (maxCount <= 0) {
      return 0;
    }

    await using var channel = await _connection.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);

    int drained = 0;
    while (drained < maxCount && !ct.IsCancellationRequested) {
      // BasicGetAsync with autoAck=false — single-message poll keeps the implementation
      // simple. DLQ recovery is intentionally low-cadence; throughput isn't the limiting factor.
      var result = await channel.BasicGetAsync(_dlqName, autoAck: false, cancellationToken: ct).ConfigureAwait(false);
      if (result is null) {
        // DLQ is empty.
        break;
      }

      ct.ThrowIfCancellationRequested();

      if (!TryBuildImport(result, _dlqName, out var import)) {
#pragma warning disable CA1848, CA1873
        _logger.LogWarning(
          "RMQ DLQ drain skipped a message from {Dlq}: MessageId is not a Whizbang wire id (GUID) — requeueing and ending this pass",
          _dlqName);
#pragma warning restore CA1848, CA1873
        await _nackGuardedAsync(channel, result.DeliveryTag, ct).ConfigureAwait(false);
        break;   // the requeued head would be re-fetched immediately — end the pass instead
      }

      try {
        // FALSE = duplicate custody (already imported) — still ack so the broker copy leaves
        // the DLQ. Failures THROW and route to the requeue arm.
        var imported = await _importAsync(import, ct).ConfigureAwait(false);
        await channel.BasicAckAsync(result.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
        drained++;
#pragma warning disable CA1848, CA1873
        _logger.LogInformation(
          "RMQ DLQ imported message {MessageId} from {Dlq} into wh_dead_letters (duplicate={Duplicate})",
          import.MessageId, _dlqName, !imported);
#pragma warning restore CA1848, CA1873
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
#pragma warning disable CA1848, CA1873
        _logger.LogWarning(ex,
          "RMQ DLQ import failed for message {MessageId} from {Dlq} — requeueing and ending this pass",
          import.MessageId, _dlqName);
#pragma warning restore CA1848, CA1873
        await _nackGuardedAsync(channel, result.DeliveryTag, ct).ConfigureAwait(false);
        break;   // same head-blocking guard as above
      }
    }

    return drained;
  }

  /// <summary>
  /// Maps a broker message to the import record — pure metadata + raw body, no deserialization.
  /// Returns false when the AMQP MessageId is not a GUID (not a Whizbang wire message).
  /// Internal for direct regression testing of the mapping.
  /// </summary>
  internal static bool TryBuildImport(BasicGetResult result, string dlqName, out BrokerDeadLetterImport import) {
    var props = result.BasicProperties;
    if (!Guid.TryParse(props.MessageId, out var messageId)) {
      import = null!;
      return false;
    }
    string? messageType = null;
    if (props.Headers is { } headers && headers.TryGetValue("EnvelopeType", out var et)) {
      messageType = et switch {
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        string str => str,
        _ => null,
      };
    }
    var (reason, count) = ParseXDeath(props.Headers);
    import = new BrokerDeadLetterImport(
      MessageId: messageId,
      StreamId: null,
      MessageType: messageType,
      Destination: dlqName,
      EnvelopeJson: System.Text.Encoding.UTF8.GetString(result.Body.Span),
      BrokerReason: reason,
      BrokerDescription: null,
      EnqueuedAt: null,
      DeliveryCount: count is null ? null : (int)Math.Min(count.Value, int.MaxValue));
    return true;
  }

  /// <summary>
  /// Reads the dead-letter reason and death count from the <c>x-death</c> header RabbitMQ adds
  /// when a message enters a DLQ. Pure function so it can be unit-tested without a broker.
  /// </summary>
  internal static (string? Reason, long? Count) ParseXDeath(IDictionary<string, object?>? headers) {
    if (headers is null
        || !headers.TryGetValue("x-death", out var xDeathRaw)
        || xDeathRaw is not IList<object?> xDeathList
        || xDeathList.Count == 0
        || xDeathList[0] is not IDictionary<string, object?> first) {
      return (null, null);
    }
    string? reason = null;
    long? count = null;
    if (first.TryGetValue("reason", out var r) && r is byte[] reasonBytes) {
      reason = System.Text.Encoding.UTF8.GetString(reasonBytes);
    }
    if (first.TryGetValue("count", out var c) && c is long countValue) {
      count = countValue;
    }
    return (reason, count);
  }

  private async Task _nackGuardedAsync(IChannel channel, ulong deliveryTag, CancellationToken ct) {
    try {
      await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, ct).ConfigureAwait(false);
    } catch (Exception nackEx) when (
        nackEx is global::RabbitMQ.Client.Exceptions.AlreadyClosedException or ObjectDisposedException) {
#pragma warning disable CA1848, CA1873
      _logger.LogWarning(nackEx,
        "RMQ DLQ nack failed on a closed channel for {Dlq} — broker re-delivers naturally", _dlqName);
#pragma warning restore CA1848, CA1873
    }
  }
}
