// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// The wire topic stream-integrity traffic addressed to this service travels on: repair and
/// redelivery requests, the bundles that answer them, and the request topic a checkpoint advertises.
/// </summary>
/// <remarks>
/// <see cref="StreamIntegrityOptions.RepairTopic"/> names it when set; otherwise it is the first
/// destination this service consumes, because that is a topic the service is guaranteed to receive
/// on. Every integrity worker resolves it the same way, so the rule lives here once.
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Workers/RepairTopicResolverTests.cs</tests>
internal static class RepairTopicResolver {
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
  internal static string? Resolve(string? configured, IServiceProvider services) =>
    configured ?? services.GetService<TransportConsumerOptions>()?.Destinations.FirstOrDefault()?.Address;
}
