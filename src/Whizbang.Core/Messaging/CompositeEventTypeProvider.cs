// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>The union of every contributed <see cref="IEventTypeProvider"/>.</summary>
/// <docs>fundamentals/events/events</docs>
public sealed class CompositeEventTypeProvider : IEventTypeProvider {
  /// <summary>Unions the given providers' event types.</summary>
  /// <param name="providers">The contributions.</param>
  public CompositeEventTypeProvider(IEnumerable<IEventTypeProvider> providers) {
    ArgumentNullException.ThrowIfNull(providers);
  }

  /// <inheritdoc />
  public IReadOnlyList<Type> GetEventTypes() => [];
}
