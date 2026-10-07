// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// The one <see cref="ITransportDeadLetterDrainer"/> the Service Bus hosting registration
/// contributes: a drain pass covers every Service Bus namespace the host's transport reaches,
/// through one <see cref="AzureServiceBusFleetDeadLetterDrainer"/> per namespace.
/// </summary>
/// <remarks>
/// <para>
/// A host with several namespaces resolves its transport as a <see cref="NamespaceRoutingTransport"/>
/// wrapping one Service Bus transport per namespace. Each of those owns its own client and its own
/// subscriptions, so each namespace is drained with its own client: the default namespace's client
/// cannot read another namespace's dead-letter queues, and two namespaces can carry the same topic and
/// subscription names.
/// </para>
/// <para>
/// The per-pass budget is one total across namespaces, as it is across subscriptions inside a fleet:
/// it paces broker operations, and multiplying it by the namespace count would undo the pacing.
/// </para>
/// </remarks>
/// <docs>operations/dead-letter-queue/transport-recovery#multiple-namespaces</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AsbNamespacesDeadLetterDrainerTests.cs</tests>
internal sealed class AzureServiceBusNamespacesDeadLetterDrainer : ITransportDeadLetterDrainer, IAsyncDisposable {
  private readonly Func<IReadOnlyList<AzureServiceBusTransport>> _transports;
  private readonly Func<AzureServiceBusTransport, AzureServiceBusFleetDeadLetterDrainer> _fleetFor;
  private readonly ConcurrentDictionary<AzureServiceBusTransport, AzureServiceBusFleetDeadLetterDrainer> _fleets = new();

  /// <summary>Creates the drainer.</summary>
  /// <param name="transports">The Service Bus transports to drain, evaluated fresh on every pass.</param>
  /// <param name="fleetFor">Builds the fleet drainer for one namespace's transport, once per transport.</param>
  internal AzureServiceBusNamespacesDeadLetterDrainer(
      Func<IReadOnlyList<AzureServiceBusTransport>> transports,
      Func<AzureServiceBusTransport, AzureServiceBusFleetDeadLetterDrainer> fleetFor) {
    ArgumentNullException.ThrowIfNull(transports);
    ArgumentNullException.ThrowIfNull(fleetFor);
    _transports = transports;
    _fleetFor = fleetFor;
  }

  /// <inheritdoc />
  public string TransportName => "asb";

  /// <summary>
  /// The Service Bus transports behind <paramref name="transport"/>: itself when it is one, and
  /// every one a namespace router wraps.
  /// </summary>
  /// <param name="transport">The host's resolved transport.</param>
  /// <returns>The Service Bus transports to drain; empty for any other transport.</returns>
  internal static IReadOnlyList<AzureServiceBusTransport> ServiceBusTransportsOf(ITransport transport) =>
    transport switch {
      AzureServiceBusTransport serviceBus => [serviceBus],
      NamespaceRoutingTransport router => [.. router.Transports.OfType<AzureServiceBusTransport>()],
      _ => [],
    };

  /// <inheritdoc />
  public async Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default) {
    var drained = 0;
    foreach (var transport in _transports()) {
      var remaining = maxCount - drained;
      if (remaining <= 0) {
        break;
      }

      var fleet = _fleets.GetOrAdd(transport, _fleetFor);
      drained += await fleet.DrainDeadLetterQueueAsync(remaining, ct).ConfigureAwait(false);
    }

    return drained;
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    foreach (var fleet in _fleets.Values) {
      await fleet.DisposeAsync().ConfigureAwait(false);
    }

    _fleets.Clear();
  }
}
