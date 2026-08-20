using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// The one <see cref="ITransportDeadLetterDrainer"/> the RabbitMQ hosting registration
/// contributes: a fleet drainer that, on every drain pass, enumerates the transport's declared
/// dead-letter queues and drains each through a cached per-queue
/// <see cref="RabbitMqDeadLetterDrainer"/>. Queues are declared at runtime (after the DI
/// container is sealed), so per-queue drainers cannot be individual DI registrations — this
/// aggregate bridges the container-time registration the <c>TransportDeadLetterDrainWorker</c>
/// resolves and the runtime queue set. <c>maxCount</c> is a TOTAL cap per pass, not
/// per-queue — the worker's MaxPerTick is a broker pacing contract.
/// </summary>
/// <docs>operations/dead-letter-queue/transport-recovery</docs>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMqFleetDeadLetterDrainerTests.cs</tests>
public sealed class RabbitMqFleetDeadLetterDrainer : ITransportDeadLetterDrainer {
  private readonly Func<IReadOnlyCollection<string>> _activeDeadLetterQueues;
  private readonly Func<string, ITransportDeadLetterDrainer> _drainerFactory;
  private readonly ConcurrentDictionary<string, ITransportDeadLetterDrainer> _drainers = new();

  /// <summary>
  /// Production wiring: lazily resolves the shared connection so that merely CONSTRUCTING the
  /// fleet drainer never dials the broker.
  /// </summary>
  /// <param name="connectionFactory">Deferred access to the shared broker connection.</param>
  /// <param name="activeDeadLetterQueues">Deferred snapshot of declared DLQ names; evaluated
  ///   fresh on every drain pass.</param>
  /// <param name="importAsync">Custody seam handed to every per-queue drainer.</param>
  /// <param name="loggerFactory">Logger factory for per-queue drainers.</param>
  public RabbitMqFleetDeadLetterDrainer(
      Func<IConnection> connectionFactory,
      Func<IReadOnlyCollection<string>> activeDeadLetterQueues,
      Func<BrokerDeadLetterImport, CancellationToken, Task<bool>> importAsync,
      ILoggerFactory loggerFactory)
    : this(
        activeDeadLetterQueues,
        dlqName => new RabbitMqDeadLetterDrainer(
          (connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory)))(),
          dlqName,
          importAsync ?? throw new ArgumentNullException(nameof(importAsync)),
          (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger<RabbitMqDeadLetterDrainer>())) {
  }

  /// <summary>Test seam: inject the per-queue drainer factory directly.</summary>
  internal RabbitMqFleetDeadLetterDrainer(
      Func<IReadOnlyCollection<string>> activeDeadLetterQueues,
      Func<string, ITransportDeadLetterDrainer> drainerFactory) {
    _activeDeadLetterQueues = activeDeadLetterQueues ?? throw new ArgumentNullException(nameof(activeDeadLetterQueues));
    _drainerFactory = drainerFactory ?? throw new ArgumentNullException(nameof(drainerFactory));
  }

  /// <inheritdoc />
  public string TransportName => "rmq";

  /// <inheritdoc />
  public async Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default) {
    if (maxCount <= 0) {
      return 0;
    }
    var drained = 0;
    foreach (var dlqName in _activeDeadLetterQueues()) {
      ct.ThrowIfCancellationRequested();
      var remaining = maxCount - drained;
      if (remaining <= 0) {
        break;
      }
      var drainer = _drainers.GetOrAdd(dlqName, _drainerFactory);
      drained += await drainer.DrainDeadLetterQueueAsync(remaining, ct).ConfigureAwait(false);
    }
    return drained;
  }
}
