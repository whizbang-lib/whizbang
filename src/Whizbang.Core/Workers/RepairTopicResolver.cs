// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// The wire topic stream-integrity traffic addressed to this service travels on: repair and
/// redelivery requests, the bundles that answer them, and the request topic a checkpoint advertises.
/// </summary>
/// <remarks>
/// <see cref="StreamIntegrityOptions.RepairTopic"/> names it when set. Otherwise it is this service's own control inbox
/// (#2090, <see cref="CommandInboxNaming.ServiceControlTopicFor"/>), the consumer destination the namespace inbox strategy
/// marks as such, which only this service subscribes to, so a directed message reaches it and no other service. A
/// per-namespace command inbox is never chosen: any service handling one of its commands reads it (#2089), and its
/// subscriptions filter to command types, so control traffic sent there would be dropped. A service with no control
/// inbox (a strategy that does not provision one) keeps the first shared destination it consumes, a topic it is
/// guaranteed to receive on but which other services read too, and says so once. Every integrity worker resolves it
/// the same way, so the rule lives here once; senders always answer on the address the receiver advertised, so an
/// older service that still advertises a command inbox or the shared topic keeps receiving there.
/// </remarks>
/// <docs>resilience/stream-integrity#directed-integrity-traffic</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/RepairTopicResolverTests.cs</tests>
public static partial class RepairTopicResolver {
  // Once per consumer configuration (one per host), so the warning is written once per service, not per call.
  private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TransportConsumerOptions, object> _warnedShared = [];

  /// <summary>
  /// The configured repair topic, else this service's own control inbox (#2090), else the first shared destination it
  /// consumes, or null when it consumes none.
  /// </summary>
  /// <param name="configured">The configured <see cref="StreamIntegrityOptions.RepairTopic"/>, if any.</param>
  /// <param name="services">The scope's services, read for <see cref="TransportConsumerOptions"/>.</param>
  /// <returns>The topic, or null when the service names none and consumes none.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RepairTopicResolverTests.cs:Configured_WinsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RepairTopicResolverTests.cs:Unset_FirstDestinationAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RepairTopicResolverTests.cs:Unset_NoDestinations_NullAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RepairTopicResolverTests.cs:Unset_NoConsumer_NullAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RepairTopicResolverTests.cs:Unset_ServiceControlInbox_IsPreferredOverCommandInboxesAndSharedTopicsAsync</tests>
  public static string? Resolve(string? configured, IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    if (configured is not null) {
      return configured;
    }
    var consumer = services.GetService<TransportConsumerOptions>();
    if (consumer is not { Destinations.Count: > 0 }) {
      return null;
    }
    if (consumer.Destinations.FirstOrDefault(_isServiceControlInbox) is { } own) {
      return own.Address;
    }
    var shared = _firstShared(consumer.Destinations).Address;
    if (_warnedShared.TryAdd(consumer, shared)
        && services.GetService<ILoggerFactory>()?.CreateLogger(TypeNameFormatter.DisplayName(typeof(RepairTopicResolver))) is { } logger) {
      LogSharedRequestTopic(logger, shared);
    }
    return shared;
  }

  /// <summary>
  /// The shared topic a broadcast integrity request goes to when no addressee is known (#2078): the topic every service
  /// reads, never a command inbox or this service's own control inbox.
  /// </summary>
  /// <param name="configured">The configured <see cref="StreamIntegrityOptions.RepairTopic"/>, if any.</param>
  /// <param name="services">The scope's services, read for <see cref="TransportConsumerOptions"/>.</param>
  /// <returns>The shared topic, or null when the service consumes none.</returns>
  public static string? ResolveBroadcast(string? configured, IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    if (configured is not null) {
      return configured;
    }
    var destinations = services.GetService<TransportConsumerOptions>()?.Destinations;
    return destinations is { Count: > 0 } ? _firstShared(destinations).Address : null;
  }

  /// <summary>The first destination every service reads: neither a command inbox nor a service's own control inbox,
  /// or the first destination when there is no other.</summary>
  private static TransportDestination _firstShared(List<TransportDestination> destinations) =>
    destinations.FirstOrDefault(d => !_isOwnCommandInbox(d) && !_isServiceControlInbox(d)) ?? destinations[0];

  /// <summary>True for a destination the namespace inbox strategy marked as a per-namespace command inbox.</summary>
  private static bool _isOwnCommandInbox(TransportDestination destination) =>
    _hasTrueMarker(destination, NamespaceInboxStrategy.OwnedCommandInboxMetadataKey);

  /// <summary>True for a destination the namespace inbox strategy marked as this service's own control inbox.</summary>
  private static bool _isServiceControlInbox(TransportDestination destination) =>
    _hasTrueMarker(destination, NamespaceInboxStrategy.ServiceControlInboxMetadataKey);

  private static bool _hasTrueMarker(TransportDestination destination, string key) =>
    destination.Metadata is not null
    && destination.Metadata.TryGetValue(key, out var marker)
    && marker.ValueKind == JsonValueKind.True;

  [LoggerMessage(EventId = 99, Level = LogLevel.Warning,
    Message = "This service consumes no control inbox of its own, so its directed integrity traffic (repair and manifest "
            + "requests and their answers) uses the shared topic '{Topic}', which every service reads. Set "
            + "Whizbang:StreamIntegrity:RepairTopic or use the namespace inbox topology to give it its own inbox.")]
  static partial void LogSharedRequestTopic(ILogger logger, string topic);

}
