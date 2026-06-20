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

  // One entry per upcaster that declares a type-change (non-empty SourceTypes + TargetTypes). Holds
  // the source/target types in both Type and TypeNameFormatter.Format() string form so the read seam
  // (Type-based) and the rebuild stream-enumeration (EventType-column-name-based) can both scope.
  private readonly TypeChangeContribution[] _typeChanges;

  private sealed record TypeChangeContribution(
      Type[] SourceTypes, string[] SourceTypeNames, Type[] TargetTypes, HashSet<string> TargetTypeNames);

  /// <summary>
  /// Creates a pipeline from the registered upcasters, preserving their order.
  /// </summary>
  /// <param name="upcasters">The upcasters in registration order (oldest-shape first).</param>
  public EventUpcasterPipeline(IEnumerable<IEventUpcaster> upcasters) {
    ArgumentNullException.ThrowIfNull(upcasters);
    _upcasters = upcasters.ToArray();
    _typeChanges = _upcasters
      .Where(u => u.SourceTypes.Count > 0 && u.TargetTypes.Count > 0)
      .Select(u => new TypeChangeContribution(
        SourceTypes: u.SourceTypes.ToArray(),
        SourceTypeNames: u.SourceTypes.Select(TypeNameFormatter.Format).ToArray(),
        TargetTypes: u.TargetTypes.ToArray(),
        TargetTypeNames: new HashSet<string>(u.TargetTypes.Select(TypeNameFormatter.Format), StringComparer.Ordinal)))
      .ToArray();
  }

  /// <summary>
  /// <c>true</c> when at least one upcaster is registered. Callers can short-circuit the read
  /// path entirely when this is <c>false</c>.
  /// </summary>
  public bool HasAny => _upcasters.Length > 0;

  /// <summary>
  /// <c>true</c> when at least one registered upcaster declares a type-change
  /// (<see cref="IEventUpcaster.SourceTypes"/> + <see cref="IEventUpcaster.TargetTypes"/>). Callers
  /// that scope reads by event type can skip the augmentation work entirely when this is <c>false</c>.
  /// </summary>
  public bool HasTypeChanges => _typeChanges.Length > 0;

  /// <summary>
  /// The extra stored input <see cref="Type"/>s to include when reading for a perspective that
  /// subscribes to <paramref name="requestedTypes"/>: the <see cref="IEventUpcaster.SourceTypes"/>
  /// of any type-change upcaster whose <see cref="IEventUpcaster.TargetTypes"/> intersect the request
  /// (and that aren't already requested). Empty when nothing applies — the common case.
  /// </summary>
  public IReadOnlyList<Type> ExtraInputTypesFor(IReadOnlyList<Type> requestedTypes) {
    if (_typeChanges.Length == 0) {
      return Array.Empty<Type>();
    }
    var requested = new HashSet<Type>(requestedTypes);
    List<Type>? extra = null;
    foreach (var c in _typeChanges) {
      if (!c.TargetTypes.Any(requested.Contains)) {
        continue;
      }
      foreach (var s in c.SourceTypes) {
        if (!requested.Contains(s)) {
          (extra ??= new List<Type>()).Add(s);
        }
      }
    }
    return extra is null ? Array.Empty<Type>() : extra.Distinct().ToArray();
  }

  /// <summary>
  /// The <see cref="TypeNameFormatter.Format"/> names of the extra stored input types to include
  /// when scoping a rebuild's stream enumeration to a perspective subscribing to
  /// <paramref name="requestedTypeNames"/>. The name-based twin of <see cref="ExtraInputTypesFor"/>,
  /// for callers that match against the event store's <c>EventType</c> string column.
  /// </summary>
  public IReadOnlyList<string> ExtraInputTypeNamesFor(IReadOnlyList<string> requestedTypeNames) {
    if (_typeChanges.Length == 0) {
      return Array.Empty<string>();
    }
    var requested = new HashSet<string>(requestedTypeNames, StringComparer.Ordinal);
    List<string>? extra = null;
    foreach (var c in _typeChanges) {
      if (!c.TargetTypeNames.Overlaps(requested)) {
        continue;
      }
      foreach (var s in c.SourceTypeNames) {
        if (!requested.Contains(s)) {
          (extra ??= new List<string>()).Add(s);
        }
      }
    }
    return extra is null ? Array.Empty<string>() : extra.Distinct().ToArray();
  }

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
