namespace Whizbang.Core.Minting;

/// <summary>
/// The event mint: the single facade over the framework's minted event families —
/// <c>mint.Composites</c> (wire-only composite bundles), <c>mint.Collective</c> (collective
/// events), and <c>mint.Checkpoints</c> (control-class checkpoint minting). Pure aggregation:
/// each family keeps its own focused interface; the facade exists so a producer injects ONE
/// service and tests mock ONE seam.
/// </summary>
/// <remarks>
/// Registered turnkey inside <c>AddWhizbang()</c> (core pipeline — never a per-assembly generated
/// registration, which multi-assembly hosts can strip).
/// </remarks>
/// <docs>fundamentals/messaging/composite-events#minting</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/EventMintTests.cs</tests>
public interface IEventMint {
  /// <summary>The composite family — the group-key/count/byte splitter behind every composite producer.</summary>
  ICompositeFactory Composites { get; }

  /// <summary>The collective family. Placeholder — its minting surface lands with topology arc phase 6.</summary>
  ICollectiveMint Collective { get; }

  /// <summary>The checkpoint family. Placeholder — its minting surface (control-class TTL minting) lands with topology arc phase 9.</summary>
  ICheckpointMint Checkpoints { get; }
}

/// <summary>
/// The collective-event mint family. Empty by design at phase 4: the facade fixes the shape
/// (<c>mint.Collective</c>) now so producers bind to a stable seam; the minting members land with
/// topology arc phase 6.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/EventMintTests.cs</tests>
public interface ICollectiveMint {
}

/// <summary>
/// The checkpoint mint family. Empty by design at phase 4: the facade fixes the shape
/// (<c>mint.Checkpoints</c>) now so producers bind to a stable seam; the minting members
/// (control-class checkpoints with TTL ≈ 2× cadence) land with topology arc phase 9.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/EventMintTests.cs</tests>
public interface ICheckpointMint {
}
