using System.Collections.Generic;

namespace Whizbang.Core.Minting;

/// <summary>
/// The one composite splitter: constituents in, publish-ready plans out. Unifies the three
/// splitting concerns every composite producer was hand-rolling — the routing strategy's group
/// key (a composite is published to exactly ONE destination, so every constituent inside it must
/// belong there: same key ⇔ same destination), a sender-side count cap, and a byte budget — and
/// invokes a caller-supplied family builder once per emitted chunk.
/// </summary>
/// <remarks>
/// Reached through <see cref="IEventMint.Composites"/> (or injected directly by framework
/// workers). Consumers construct minted composite family instances only inside the request's
/// <see cref="CompositeMintRequest{TConstituent}.BuildComposite"/> builder — direct construction
/// elsewhere is flagged by analyzer <c>WHIZ150</c>.
/// </remarks>
/// <docs>fundamentals/messaging/composite-events#minting</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CompositeFactoryTests.cs</tests>
public interface ICompositeFactory {
  /// <summary>
  /// Splits <paramref name="request"/>'s constituents into composite plans: grouped by the
  /// request's group-key selector (groups surface in first-occurrence order, constituents in
  /// input order), then chunked inside each group by the count cap and the byte budget. The
  /// family builder runs once per chunk; each plan pairs the chunk with the composite built
  /// from it.
  /// </summary>
  /// <typeparam name="TConstituent">The caller's constituent shape (outbox rows, stored event
  /// rows, typed events — the factory never inspects it beyond the request's selectors).</typeparam>
  /// <param name="request">Constituents, group-key source, family builder, and bounds.</param>
  /// <returns>The publish-ready plans, in deterministic group/chunk order.</returns>
  /// <exception cref="System.ArgumentNullException">Thrown when request is null.</exception>
  /// <exception cref="System.ArgumentOutOfRangeException">Thrown when a provided bound is not positive.</exception>
  /// <exception cref="System.InvalidOperationException">Thrown when a byte budget is set without a
  /// size selector, or the builder returns null.</exception>
  IReadOnlyList<CompositeEnvelopePlan<TConstituent>> Create<TConstituent>(CompositeMintRequest<TConstituent> request);
}
