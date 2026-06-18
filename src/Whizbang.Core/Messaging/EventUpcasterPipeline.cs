using System;
using System.Collections.Generic;
using System.Linq;
using Whizbang.Core;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Applies the registered <see cref="IEventUpcaster"/>s to a deserialized event in a single
/// forward pass, in registration order. The output of each matching upcaster feeds the next
/// upcaster's <see cref="IEventUpcaster.CanUpcast"/>, so chained version bumps (V1→V2→V3)
/// compose when registered oldest-shape-first.
/// </summary>
/// <remarks>
/// <para>
/// A single forward pass is intentional: it is bounded (at most one visit per registered
/// upcaster) and cannot loop, even if an upcaster's output would re-match an earlier upcaster.
/// Register upcasters oldest-shape → newest-shape so a stale event walks the whole chain.
/// </para>
/// <para>
/// AOT-safe: the pipeline only invokes the upcasters' own type checks and constructors — no
/// reflection, no dynamic dispatch beyond the interface call. The no-upcaster case
/// (<see cref="HasAny"/> is <c>false</c>) is a near-free passthrough.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventUpcasterPipelineTests.cs</tests>
public sealed class EventUpcasterPipeline {
  private readonly IEventUpcaster[] _upcasters;

  /// <summary>
  /// Creates a pipeline from the registered upcasters, preserving their order.
  /// </summary>
  /// <param name="upcasters">The upcasters in registration order (oldest-shape first).</param>
  public EventUpcasterPipeline(IEnumerable<IEventUpcaster> upcasters) {
    ArgumentNullException.ThrowIfNull(upcasters);
    _upcasters = upcasters.ToArray();
  }

  /// <summary>
  /// <c>true</c> when at least one upcaster is registered. Callers can short-circuit the read
  /// path entirely when this is <c>false</c>.
  /// </summary>
  public bool HasAny => _upcasters.Length > 0;

  /// <summary>
  /// Runs the event through every registered upcaster once, in order, applying each whose
  /// <see cref="IEventUpcaster.CanUpcast"/> returns <c>true</c> to the current (possibly already
  /// transformed) event. Returns the event unchanged when no upcaster matches.
  /// </summary>
  /// <param name="event">The deserialized event.</param>
  /// <returns>The upcasted event (or the same instance when nothing matched).</returns>
  public IEvent Apply(IEvent @event) {
    ArgumentNullException.ThrowIfNull(@event);

    var current = @event;
    foreach (var upcaster in _upcasters) {
      if (upcaster.CanUpcast(current)) {
        current = upcaster.Upcast(current);
        ArgumentNullException.ThrowIfNull(current, nameof(current));
      }
    }
    return current;
  }
}
