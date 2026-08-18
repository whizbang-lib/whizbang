using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tags;

/// <summary>
/// The input a coalesce composite factory receives for one fold: the group being folded, the
/// fetched pending singles (all sharing one destination — a composite has ONE destination),
/// and the binding's fan-out atomicity for the built composite to carry.
/// </summary>
/// <docs>fundamentals/messages/message-tags#coalescing</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/CoalesceShipWorkerTests.cs:RunOnce_BindingFactory_BuildsTheBindingsCompositeAsync</tests>
public sealed record CoalesceFoldBatch {
  /// <summary>The coalesce group (tag string) being folded.</summary>
  public required string Group { get; init; }

  /// <summary>The pending singles this fold bundles, oldest first, all with the same destination.</summary>
  public required IReadOnlyList<OutboxMessage> Singles { get; init; }

  /// <summary>The binding's per-child failure policy, to be carried by the built composite.</summary>
  public required FanoutAtomicity Atomicity { get; init; }
}
