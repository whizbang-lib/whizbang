// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// The union of every contributed <see cref="IEventTypeProvider"/>: the provider a host with several perspective
/// assemblies serves (#2146).
/// </summary>
/// <remarks>
/// The list is built once, at construction: generated providers are views over compile-time arrays. The registration
/// hands the contributions over latest registered first (the rule every per-assembly registration follows), and a
/// type two assemblies both know is listed once, at its first position in that order. It is not a collision: both
/// assemblies name the same type, so neither registration is hidden. The union is available when one of its contributions
/// is, so a host whose contributions are all unavailable keeps the "no event types known" path its consumers take.
/// </remarks>
/// <docs>fundamentals/events/events</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventTypeProviderContributionTests.cs</tests>
public sealed class CompositeEventTypeProvider : IEventTypeProvider {
  private readonly IReadOnlyList<Type> _eventTypes;

  /// <summary>Unions the given providers' event types.</summary>
  /// <param name="providers">The contributions.</param>
  public CompositeEventTypeProvider(IEnumerable<IEventTypeProvider> providers) {
    ArgumentNullException.ThrowIfNull(providers);
    var seen = new HashSet<Type>();
    var eventTypes = new List<Type>();
    var available = false;
    foreach (var provider in providers) {
      available |= provider.IsAvailable;
      eventTypes.AddRange(provider.GetEventTypes().Where(seen.Add));
    }
    _eventTypes = eventTypes;
    IsAvailable = available;
  }

  /// <inheritdoc />
  public bool IsAvailable { get; }

  /// <inheritdoc />
  public IReadOnlyList<Type> GetEventTypes() => _eventTypes;
}
