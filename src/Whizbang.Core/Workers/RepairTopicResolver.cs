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
/// <see cref="StreamIntegrityOptions.RepairTopic"/> names it when set. Otherwise it is this service's own command inbox
/// (#2078): the consumer destination the namespace inbox strategy marks as an owned command inbox, which only this
/// service subscribes to, so a directed message reaches it and no other service. A service that consumes no such
/// inbox keeps the first destination it consumes, a topic it is guaranteed to receive on but which other services
/// read too, and says so once. Every integrity worker resolves it the same way, so the rule lives here once.
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Workers/RepairTopicResolverTests.cs</tests>
public static partial class RepairTopicResolver {
  // Once per consumer configuration (one per host), so the warning is written once per service, not per call.
  private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TransportConsumerOptions, object> _warnedShared = [];

  /// <summary>
  /// The configured repair topic, or the first destination this service consumes, or null when
  /// neither exists.
  /// </summary>
  /// <param name="configured">The configured <see cref="StreamIntegrityOptions.RepairTopic"/>, if any.</param>
  /// <param name="services">The scope's services, read for <see cref="TransportConsumerOptions"/>.</param>
  /// <returns>The topic, or null when the service names none and consumes none.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RepairTopicResolverTests.cs:Configured_WinsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RepairTopicResolverTests.cs:Unset_FirstDestinationAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RepairTopicResolverTests.cs:Unset_NoDestinations_NullAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RepairTopicResolverTests.cs:Unset_NoConsumer_NullAsync</tests>
  public static string? Resolve(string? configured, IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    if (configured is not null) {
      return configured;
    }
    var consumer = services.GetService<TransportConsumerOptions>();
    if (consumer is not { Destinations.Count: > 0 }) {
      return null;
    }
    if (consumer.Destinations.FirstOrDefault(_isOwnCommandInbox) is { } own) {
      return own.Address;
    }
    var shared = consumer.Destinations[0].Address;
    if (_warnedShared.TryAdd(consumer, shared)
        && services.GetService<ILoggerFactory>()?.CreateLogger(TypeNameFormatter.DisplayName(typeof(RepairTopicResolver))) is { } logger) {
      LogSharedRequestTopic(logger, shared);
    }
    return shared;
  }

  /// <summary>
  /// The shared topic a broadcast integrity request goes to when no addressee is known (#2078): the topic every service
  /// reads, never this service's own command inbox.
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
    return (destinations?.FirstOrDefault(d => !_isOwnCommandInbox(d)) ?? destinations?.FirstOrDefault())?.Address;
  }

  /// <summary>True for a destination the namespace inbox strategy marked as this service's own command inbox.</summary>
  private static bool _isOwnCommandInbox(TransportDestination destination) =>
    destination.Metadata is not null
    && destination.Metadata.TryGetValue(NamespaceInboxStrategy.OwnedCommandInboxMetadataKey, out var marker)
    && marker.ValueKind == JsonValueKind.True;

  [LoggerMessage(EventId = 99, Level = LogLevel.Warning,
    Message = "This service consumes no command inbox of its own, so its directed integrity traffic (repair and manifest "
            + "requests and their answers) uses the shared topic '{Topic}', which every service reads. Set "
            + "Whizbang:StreamIntegrity:RepairTopic or handle a command of an owned namespace to give it its own inbox.")]
  static partial void LogSharedRequestTopic(ILogger logger, string topic);

}
