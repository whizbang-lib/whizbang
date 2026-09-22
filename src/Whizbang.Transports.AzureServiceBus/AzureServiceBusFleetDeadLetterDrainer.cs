using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// The one <see cref="ITransportDeadLetterDrainer"/> the ASB hosting registration contributes:
/// a fleet drainer that, on every drain pass, enumerates the transport's ACTIVE subscriptions
/// and drains each one's broker dead-letter queue through a cached per-subscription
/// <see cref="AzureServiceBusDeadLetterDrainer"/>. Subscriptions are established at runtime
/// (after the DI container is sealed), so per-subscription drainers cannot be individual DI
/// registrations — this aggregate is the bridge between the container-time registration the
/// <c>TransportDeadLetterDrainWorker</c> resolves and the runtime subscription set.
/// </summary>
/// <docs>operations/dead-letter-queue/transport-recovery</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AsbDeadLetterDrainerWiringTests.cs</tests>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Integration.Tests/AsbDeadLetterImportSeamIntegrationTests.cs</tests>
public sealed class AzureServiceBusFleetDeadLetterDrainer : ITransportDeadLetterDrainer, IAsyncDisposable {
  private readonly Func<IReadOnlyCollection<(string TopicName, string SubscriptionName)>> _activeSubscriptions;
  private readonly Func<(string TopicName, string SubscriptionName), ITransportDeadLetterDrainer> _drainerFactory;
  private readonly ConcurrentDictionary<(string, string), ITransportDeadLetterDrainer> _drainers = new();

  /// <summary>
  /// Production wiring: lazily resolves the shared <see cref="ServiceBusClient"/> so that merely
  /// CONSTRUCTING the fleet drainer (container validation, eager singletons) never dials the broker.
  /// </summary>
  /// <param name="clientFactory">Deferred access to the shared ServiceBusClient.</param>
  /// <param name="activeSubscriptions">Deferred snapshot of the transport's active
  ///   (topic, subscription) pairs; evaluated fresh on every drain pass.</param>
  /// <param name="importAsync">Custody seam handed to every per-subscription drainer — wraps
  ///   <c>IWorkCoordinator.ImportBrokerDeadLetterAsync</c> resolved per call.</param>
  /// <param name="loggerFactory">Logger factory for per-subscription drainers.</param>
  public AzureServiceBusFleetDeadLetterDrainer(
      Func<ServiceBusClient> clientFactory,
      Func<IReadOnlyCollection<(string TopicName, string SubscriptionName)>> activeSubscriptions,
      Func<Whizbang.Core.Transports.BrokerDeadLetterImport, System.Threading.CancellationToken, Task<bool>> importAsync,
      ILoggerFactory loggerFactory)
    : this(
        activeSubscriptions,
        key => new AzureServiceBusDeadLetterDrainer(
          (clientFactory ?? throw new ArgumentNullException(nameof(clientFactory)))(),
          key.TopicName,
          key.SubscriptionName,
          importAsync ?? throw new ArgumentNullException(nameof(importAsync)),
          (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger<AzureServiceBusDeadLetterDrainer>())) {
  }

  /// <summary>Test seam: inject the per-subscription drainer factory directly.</summary>
  internal AzureServiceBusFleetDeadLetterDrainer(
      Func<IReadOnlyCollection<(string TopicName, string SubscriptionName)>> activeSubscriptions,
      Func<(string TopicName, string SubscriptionName), ITransportDeadLetterDrainer> drainerFactory) {
    _activeSubscriptions = activeSubscriptions ?? throw new ArgumentNullException(nameof(activeSubscriptions));
    _drainerFactory = drainerFactory ?? throw new ArgumentNullException(nameof(drainerFactory));
  }

  /// <inheritdoc />
  public string TransportName => "asb";

  /// <inheritdoc />
  public async Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default) {
    if (maxCount <= 0) {
      return 0;
    }
    // maxCount is a TOTAL cap per pass, not per-subscription: the worker's MaxPerTick is a
    // broker ops-rate pacing contract, and a fleet that multiplied it by the subscription count
    // would reintroduce exactly the burst the pacing exists to prevent.
    var drained = 0;
    foreach (var key in _activeSubscriptions()) {
      ct.ThrowIfCancellationRequested();
      var remaining = maxCount - drained;
      if (remaining <= 0) {
        break;
      }
      var drainer = _drainers.GetOrAdd(key, _drainerFactory);
      drained += await drainer.DrainDeadLetterQueueAsync(remaining, ct).ConfigureAwait(false);
    }
    return drained;
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    foreach (var drainer in _drainers.Values) {
      if (drainer is IAsyncDisposable disposable) {
        await disposable.DisposeAsync().ConfigureAwait(false);
      }
    }
    _drainers.Clear();
  }
}
