// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The consumer registry: the one answer to "what does this service consume". A message type is consumed when
/// something registered here accepts it at the receive boundary: an inbox or lifecycle receptor, a perspective, a
/// notification-tag consumer, the composite fan-out or the collective sink.
/// </summary>
/// <remarks>
/// <para>
/// The receive boundary asks it as a predicate (<see cref="HasAnyConsumer"/>) to keep or discard an inbound message.
/// Stream integrity (the audit request, the checkpoint and manifest comparisons, the repair ledger), the
/// subscription-expansion backfill and the orphan-inbox janitor ask it as a list
/// (<see cref="GetConsumedMessages"/>, <see cref="GetConsumedEventTypes"/>). Both forms come from the same generated
/// data, so they cannot disagree. Read it through <see cref="ConsumedMessageTypes"/> from a service provider.
/// </para>
/// <para>
/// Other type sources answer other questions: <see cref="IEventTypeProvider"/> lists the events this service's
/// perspectives fold (empty for a service with no perspectives), and <see cref="IMessageTypeCatalog"/> lists every
/// message type the application declares. Neither is the consumed set.
/// </para>
/// <para>
/// The default implementation is the source-generated registry (<see cref="WhizbangReceptorRegistryQueryAdapter"/>,
/// also the <see cref="IReceptorRegistryQuery"/>), so the receive boundary's gate and every list reader see the same
/// registry. Runtime-registered receptors (control-plane receptors, test waits) are answered by the predicate but not
/// enumerated: they register after startup and carry no compile-time metadata.
/// </para>
/// </remarks>
/// <docs>internals/receptor-registry-query#what-a-service-consumes</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ConsumedMessageTypesTests.cs</tests>
public interface IConsumerRegistry {
  /// <summary>True when ANY consumer (handler / lifecycle receptor / perspective /
  /// tag-attribute) is registered for the message type.</summary>
  bool HasAnyConsumer(string messageType);

  /// <summary>
  /// Enumerates every message type this service consumes, known at compile time: the types
  /// <see cref="HasAnyConsumer"/> answers true for, in the assembly-qualified wire form, each marked event or not.
  /// Default: empty, for implementations that predate the enumeration.
  /// </summary>
  /// <returns>The consumed message types, deduplicated and in ordinal type-name order.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WhizbangReceptorRegistryQueryAdapterTests.cs:CustomPredicatesOnlyImplementation_GetConsumedMessages_DefaultsToEmptyAsync</tests>
  IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() => [];

  /// <summary>
  /// The event types this service consumes, in the assembly-qualified wire form (the form the event store's
  /// <c>event_type</c> holds), deduplicated and in ordinal order: the list a peer or an audit can be given.
  /// </summary>
  /// <returns>The consumed event type names.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ConsumedMessageTypesTests.cs:EventTypeNames_AreTheConsumedEvents_DeduplicatedInOrdinalOrder_WithoutCommandsAsync</tests>
  IReadOnlyList<string> GetConsumedEventTypes() =>
    [.. GetConsumedMessages().Where(c => c.IsEvent).Select(c => c.TypeName).Distinct(System.StringComparer.Ordinal)
      .Order(System.StringComparer.Ordinal)];
}
