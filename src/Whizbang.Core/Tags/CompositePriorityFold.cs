namespace Whizbang.Core.Tags;

/// <summary>
/// How a coalesce binding decides the number a minted composite carries, folded from the singles it bundles.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#composites</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/CoalesceShipWorkerTests.cs:RunOnce_DefaultFold_CompositeCarriesTheMostUrgentMemberAsync</tests>
public enum CompositePriorityFold {
  /// <summary>The most urgent member's number (the default): the bundle is never scheduled behind the member somebody waits on.</summary>
  MostUrgent,
  /// <summary>The least urgent member's number: the bundle waits with its slowest member.</summary>
  LeastUrgent,
  /// <summary>The binding's <see cref="CoalescePolicyOptions.PriorityFor"/> callback decides, with the batch in hand.</summary>
  Manual,
}
