using System;
using System.Collections.Generic;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Minting;

/// <summary>
/// Selects the composite group key for a constituent. Built via the non-generic
/// <see cref="CompositeGroupKey"/> factories (CA1000 keeps statics off generic types): the
/// canonical source is the outbox routing strategy
/// (<see cref="CompositeGroupKey.FromStrategy{TConstituent}"/> — phase 3's
/// <see cref="IOutboxRoutingStrategy.GetCompositeGroupKey"/>, whose invariant is
/// same key ⇔ same destination). <see cref="CompositeGroupKey.FromKey{TConstituent}"/> covers
/// constituents whose destination was ALREADY projected by the strategy and stamped onto the row
/// (outbox singles carry <c>Destination</c>) or whose split key is an ordering concern layered on
/// top of routing (a redelivery slice splits per stream so the stream id can ride as the broker
/// session key) — the supplied selector MUST remain a projection of the same destination mapping,
/// or a plan will bundle constituents that do not belong on one destination.
/// </summary>
/// <typeparam name="TConstituent">The caller's constituent shape.</typeparam>
/// <docs>fundamentals/messaging/composite-events#minting</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CompositeFactoryTests.cs:Create_FromStrategy_GroupKeyIsTheStrategysCompositeGroupKeyAsync</tests>
public sealed class CompositeGroupKeySelector<TConstituent> {
  private readonly Func<TConstituent, string?> _keyFor;

  internal CompositeGroupKeySelector(Func<TConstituent, string?> keyFor) {
    _keyFor = keyFor;
  }

  /// <summary>The group key for <paramref name="constituent"/>.</summary>
  internal string? KeyFor(TConstituent constituent) => _keyFor(constituent);
}

/// <summary>
/// Factories for <see cref="CompositeGroupKeySelector{TConstituent}"/> — see that type for the
/// invariant the selected key must preserve.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#minting</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CompositeFactoryTests.cs</tests>
public static class CompositeGroupKey {
  /// <summary>
  /// The canonical selector: keys each constituent by the strategy's composite group key for its
  /// message type — route, split, subscribe, and provision stay projections of
  /// <see cref="IOutboxRoutingStrategy.GetDestination"/>.
  /// </summary>
  /// <typeparam name="TConstituent">The caller's constituent shape.</typeparam>
  /// <param name="strategy">The outbox routing strategy that owns the destination mapping.</param>
  /// <param name="ownedDomains">Domains this service owns (from OwnDomains() registration).</param>
  /// <param name="kind">Message kind of the constituents.</param>
  /// <param name="constituentType">Projects a constituent to its message <see cref="Type"/>.</param>
  /// <returns>The strategy-backed selector.</returns>
  /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
  public static CompositeGroupKeySelector<TConstituent> FromStrategy<TConstituent>(
      IOutboxRoutingStrategy strategy,
      IReadOnlySet<string> ownedDomains,
      MessageKind kind,
      Func<TConstituent, Type> constituentType) {
    ArgumentNullException.ThrowIfNull(strategy);
    ArgumentNullException.ThrowIfNull(ownedDomains);
    ArgumentNullException.ThrowIfNull(constituentType);
    return new CompositeGroupKeySelector<TConstituent>(
      c => strategy.GetCompositeGroupKey(constituentType(c), ownedDomains, kind));
  }

  /// <summary>
  /// A caller-supplied key projection, for constituents whose destination the strategy already
  /// stamped (an outbox row's <c>Destination</c>) or whose split key layers an ordering concern
  /// on top of routing (per-stream redelivery chunks). The projection must preserve the
  /// same-key ⇔ same-destination invariant.
  /// </summary>
  /// <typeparam name="TConstituent">The caller's constituent shape.</typeparam>
  /// <param name="keySelector">Projects a constituent to its group key (null groups with null).</param>
  /// <returns>The key-backed selector.</returns>
  /// <exception cref="ArgumentNullException">Thrown when keySelector is null.</exception>
  public static CompositeGroupKeySelector<TConstituent> FromKey<TConstituent>(Func<TConstituent, string?> keySelector) {
    ArgumentNullException.ThrowIfNull(keySelector);
    return new CompositeGroupKeySelector<TConstituent>(keySelector);
  }
}

/// <summary>
/// One chunk handed to a <see cref="CompositeMintRequest{TConstituent}.BuildComposite"/> builder:
/// the group key the chunk belongs to and its constituents, in input order.
/// </summary>
/// <typeparam name="TConstituent">The caller's constituent shape.</typeparam>
/// <param name="GroupKey">The chunk's group key (a projection of the transport destination).</param>
/// <param name="Constituents">The chunk's constituents, in input order — never empty.</param>
/// <docs>fundamentals/messaging/composite-events#minting</docs>
public sealed record CompositeConstituentBatch<TConstituent>(
  string? GroupKey,
  IReadOnlyList<TConstituent> Constituents);

/// <summary>
/// Input to <see cref="ICompositeFactory.Create"/>: the constituents, the group-key source, the
/// family builder, and the sender-side bounds. The receive-side defensive cap
/// (<see cref="ICompositeEvent.MaxInnerEventsAllowed"/>) is a separate, receiver-owned guard —
/// these bounds protect the SENDER (memory during serialization, broker message-size limits).
/// </summary>
/// <typeparam name="TConstituent">The caller's constituent shape.</typeparam>
/// <docs>fundamentals/messaging/composite-events#minting</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CompositeFactoryTests.cs</tests>
public sealed record CompositeMintRequest<TConstituent> {
  /// <summary>The constituents to fold, in the order the plans must preserve.</summary>
  public required IReadOnlyList<TConstituent> Constituents { get; init; }

  /// <summary>The group-key source — see <see cref="CompositeGroupKeySelector{TConstituent}"/>.</summary>
  public required CompositeGroupKeySelector<TConstituent> GroupKey { get; init; }

  /// <summary>
  /// Builds the family's composite instance from one chunk. Invoked once per emitted plan. This
  /// is the ONE sanctioned construction site for minted composite types (analyzer <c>WHIZ150</c>
  /// flags direct construction elsewhere).
  /// </summary>
  public required Func<CompositeConstituentBatch<TConstituent>, CompositeEventBase> BuildComposite { get; init; }

  /// <summary>
  /// Count cap per composite (sender-side; e.g. a redelivery pump's
  /// <c>MaxInnerEventsPerComposite</c> or a coalesce binding's <c>MaxBatchCount</c>). Null =
  /// unbounded. Must be positive when provided.
  /// </summary>
  public int? MaxConstituentsPerComposite { get; init; }

  /// <summary>
  /// Byte budget per composite, measured by <see cref="ConstituentSizeBytes"/> over the raw
  /// constituent bodies. A chunk flushes once it crosses the budget even below the count cap;
  /// a single constituent larger than the budget still plans, alone. Null = no byte accounting.
  /// Must be positive when provided, and requires <see cref="ConstituentSizeBytes"/>.
  /// </summary>
  public long? MaxBytesPerComposite { get; init; }

  /// <summary>The per-constituent size accounting for <see cref="MaxBytesPerComposite"/>.</summary>
  public Func<TConstituent, long>? ConstituentSizeBytes { get; init; }
}

/// <summary>
/// One publish-ready plan out of <see cref="ICompositeFactory.Create"/>: a chunk of constituents
/// (all sharing one group key ⇒ one transport destination) and the family composite built from
/// it. The caller owns what happens next — serialize and publish wire-only (redelivery), or
/// complete a transactional fold (coalesce).
/// </summary>
/// <typeparam name="TConstituent">The caller's constituent shape.</typeparam>
/// <docs>fundamentals/messaging/composite-events#minting</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CompositeFactoryTests.cs</tests>
public sealed record CompositeEnvelopePlan<TConstituent> {
  /// <summary>The plan's group key (a projection of the transport destination).</summary>
  public required string? GroupKey { get; init; }

  /// <summary>The constituents this plan folds, in input order — never empty.</summary>
  public required IReadOnlyList<TConstituent> Constituents { get; init; }

  /// <summary>The composite instance the family builder produced for this chunk.</summary>
  public required CompositeEventBase Composite { get; init; }
}
