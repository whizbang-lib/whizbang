using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// v0.502 slice C.9 — drains a RabbitMQ dead-letter queue (default topology:
/// <c>&lt;queue&gt;.dlq</c> bound to <c>&lt;exchange&gt;.dlx</c>) by re-publishing each
/// message back onto its original exchange. Symmetric with
/// <see cref="Whizbang.Transports.AzureServiceBus.AzureServiceBusDeadLetterDrainer"/>.
/// </summary>
/// <remarks>
/// <para>
/// One instance per DLQ. Registered in DI alongside <see cref="RabbitMQTransport"/>; the
/// <see cref="Whizbang.Core.Workers.TransportDeadLetterDrainWorker"/> resolves all registered
/// drainers on every backstop tick (default 10 min) and invokes
/// <see cref="DrainDeadLetterQueueAsync"/> on each.
/// </para>
/// <para>
/// Re-publish semantics: the drainer reads the original exchange + routing key from the
/// <c>x-death</c> header that RMQ adds when a message enters DLQ. Body and headers are
/// preserved. The new message starts fresh — RMQ's per-message delivery count resets. If the
/// underlying defect is permanent the message lands back in DLQ; the worker's re-attempt rate
/// is capped by <see cref="Whizbang.Core.Workers.TransportDeadLetterDrainWorkerOptions.IntervalMinutes"/>.
/// </para>
/// </remarks>
/// <docs>operations/dead-letter-queue/transport-recovery</docs>
public sealed class RabbitMqDeadLetterDrainer : ITransportDeadLetterDrainer {
  private readonly IConnection _connection;
  private readonly string _dlqName;
  private readonly string _fallbackExchange;
  private readonly ILogger<RabbitMqDeadLetterDrainer> _logger;

  /// <summary>
  /// Creates a drainer bound to a single RMQ DLQ.
  /// </summary>
  /// <param name="connection">Shared <see cref="IConnection"/>. Lifetime owned by DI; not closed here.</param>
  /// <param name="dlqName">Name of the DLQ to drain (e.g. <c>orders.dlq</c>).</param>
  /// <param name="fallbackExchange">Exchange to publish to when the message has no
  /// <c>x-death</c> header (typically the original main exchange that fed the DLX).
  /// Used as a safety net when the broker stripped headers.</param>
  /// <param name="logger">Logger.</param>
  public RabbitMqDeadLetterDrainer(
    IConnection connection,
    string dlqName,
    string fallbackExchange,
    ILogger<RabbitMqDeadLetterDrainer> logger) {
    _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    _dlqName = !string.IsNullOrWhiteSpace(dlqName) ? dlqName
      : throw new ArgumentException("DLQ name required", nameof(dlqName));
    _fallbackExchange = fallbackExchange ?? "";
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
      // simple. For high-volume DLQs we could switch to a BasicConsume loop, but DLQ
      // recovery is intentionally low-cadence; throughput isn't the limiting factor.
      var result = await channel.BasicGetAsync(_dlqName, autoAck: false, cancellationToken: ct).ConfigureAwait(false);
      if (result is null) {
        // DLQ is empty.
        break;
      }

      try {
        var (exchange, routingKey) = _resolveOriginalDestination(result);
        var props = new BasicProperties(result.BasicProperties);
        await channel.BasicPublishAsync(
          exchange: exchange,
          routingKey: routingKey,
          mandatory: false,
          basicProperties: props,
          body: result.Body,
          cancellationToken: ct).ConfigureAwait(false);
        await channel.BasicAckAsync(result.DeliveryTag, multiple: false, ct).ConfigureAwait(false);
        drained++;
#pragma warning disable CA1848, CA1873
        _logger.LogInformation(
          "RMQ DLQ drained message from {Dlq} → exchange={Exchange}/routingKey={RoutingKey}",
          _dlqName, exchange, routingKey);
#pragma warning restore CA1848, CA1873
      } catch (Exception ex) {
#pragma warning disable CA1848, CA1873
        _logger.LogWarning(ex,
          "RMQ DLQ drain failed for a message from {Dlq} — nacking with requeue",
          _dlqName);
#pragma warning restore CA1848, CA1873
        try {
          // Requeue=true keeps it in DLQ for the next sweep instead of dropping it.
          await channel.BasicNackAsync(result.DeliveryTag, multiple: false, requeue: true, ct).ConfigureAwait(false);
        } catch {
          // Best-effort cleanup; ignore.
        }
      }
    }

    return drained;
  }

  /// <summary>
  /// Reads the original (exchange, routing-key) from the <c>x-death</c> header RabbitMQ adds
  /// when a message enters DLQ. Falls back to <see cref="_fallbackExchange"/> + the message's
  /// current routing key when the header is missing or malformed.
  /// </summary>
  private (string Exchange, string RoutingKey) _resolveOriginalDestination(BasicGetResult result) {
    var fallbackExchange = _fallbackExchange;
    var fallbackRoutingKey = result.RoutingKey ?? "";

    if (result.BasicProperties.Headers is null
        || !result.BasicProperties.Headers.TryGetValue("x-death", out var xDeathRaw)
        || xDeathRaw is not IList<object?> xDeathList
        || xDeathList.Count == 0
        || xDeathList[0] is not IDictionary<string, object?> first) {
      return (fallbackExchange, fallbackRoutingKey);
    }

    string? exchange = null;
    string? routingKey = null;

    if (first.TryGetValue("exchange", out var ex) && ex is byte[] exBytes) {
      exchange = System.Text.Encoding.UTF8.GetString(exBytes);
    }
    if (first.TryGetValue("routing-keys", out var rkRaw)
        && rkRaw is IList<object?> rkList
        && rkList.Count > 0
        && rkList[0] is byte[] rkBytes) {
      routingKey = System.Text.Encoding.UTF8.GetString(rkBytes);
    }

    return (exchange ?? fallbackExchange, routingKey ?? fallbackRoutingKey);
  }
}
