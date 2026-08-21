using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// RabbitMQ's admin-plane backlog peek (topology arc phase 10): queue DEPTH from a passive
/// declare, which returns the broker's own message count without creating or modifying anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capability honesty.</b> This transport supplies DEPTH but not oldest-enqueue AGE, and says
/// so rather than going silently inert — the same contract phase 8.5 established for age-based
/// poison detection. AMQP has no way to read the head message's timestamp without consuming it,
/// so an "age" here could only be obtained by a get-and-requeue, which mutates delivery state
/// (it marks the message redelivered) on every duty tick. Corrupting the very counters the poison
/// detector reads, once a minute, forever, to obtain a gauge is not a trade worth making; the duty
/// reports the gap, and the health signal for this transport stays depth-only.
/// </para>
/// <para>
/// Each passive declare uses its own rented channel: a failed passive declare CLOSES the channel,
/// so sharing one would take out an unrelated declare in the same pass (the existing
/// ownership-drift probe learned this the same way).
/// </para>
/// </remarks>
/// <docs>operations/observability/managed-resource-health#backlog-age</docs>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQBacklogPeekTests.cs</tests>
public sealed class RabbitMQBacklogPeek : IBacklogPeek {
  private readonly RabbitMQChannelPool _channelPool;
  private readonly Func<IReadOnlyList<string>> _queueNames;

  /// <summary>Creates the peek.</summary>
  /// <param name="channelPool">The channel pool passive declares are rented from.</param>
  /// <param name="queueNames">The queues this instance consumes from.</param>
  /// <exception cref="ArgumentNullException">Thrown when a required dependency is null.</exception>
  public RabbitMQBacklogPeek(RabbitMQChannelPool channelPool, Func<IReadOnlyList<string>> queueNames) {
    _channelPool = channelPool ?? throw new ArgumentNullException(nameof(channelPool));
    _queueNames = queueNames ?? throw new ArgumentNullException(nameof(queueNames));
  }

  /// <inheritdoc />
  public string TransportName => "rabbitmq";

  /// <inheritdoc />
  public async Task<IReadOnlyList<BacklogSample>> PeekAsync(CancellationToken cancellationToken) {
    var samples = new List<BacklogSample>();

    foreach (var queueName in _queueNames().Distinct(StringComparer.Ordinal)) {
      uint depth;
      try {
        // A dedicated channel per probe: a failed passive declare closes the channel it ran on.
        using var probe = await _channelPool.RentAsync(cancellationToken).ConfigureAwait(false);
        var declared = await probe.Channel.QueueDeclarePassiveAsync(queueName, cancellationToken)
          .ConfigureAwait(false);
        depth = declared.MessageCount;
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        continue;  // queue not declared yet, or the broker refused; the next tick retries.
      }

      samples.Add(new BacklogSample(queueName, depth, OldestAge: null) {
        Transport = TransportName,
        TransportNamespace = TransportNamespaces.DefaultKey,
        TrafficClass = TrafficClasses.DOMAIN,
      });
    }

    return samples;
  }
}
