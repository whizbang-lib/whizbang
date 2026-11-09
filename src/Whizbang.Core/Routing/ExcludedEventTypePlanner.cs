// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Routing;

/// <summary>
/// Turns the opt-out list (<see cref="RoutingOptions.ExcludedEventTypes"/>, #2175) into the subjects each event
/// subscription excludes, and runs the startup checks on it.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#excluded-event-types</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/ExcludedEventTypePlannerTests.cs</tests>
internal static class ExcludedEventTypePlanner {
  /// <summary>
  /// The excluded subjects per subscribed event topic. Every topic in <paramref name="eventTopics"/> is in the answer,
  /// with an empty list when nothing published to it is excluded.
  /// </summary>
  /// <param name="options">The routing options holding the opt-out list.</param>
  /// <param name="eventTopics">The event topics this service subscribes to.</param>
  /// <param name="consumers">What this service consumes.</param>
  /// <param name="runtimeEventTypes">Events consumed through receptors registered at startup.</param>
  /// <param name="messageCatalog">The application's message types, or null when none is known.</param>
  /// <param name="logger">Receives the warnings and the effective exclusions.</param>
  /// <returns>Topic to ordered, distinct excluded subjects.</returns>
  /// <exception cref="InvalidOperationException">Thrown when an excluded type is one this service consumes.</exception>
  internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Plan(
      RoutingOptions options,
      IReadOnlySet<string> eventTopics,
      IConsumerRegistry consumers,
      IEnumerable<Type> runtimeEventTypes,
      IMessageTypeCatalog? messageCatalog,
      ILogger logger) {
    throw new NotImplementedException();
  }
}
