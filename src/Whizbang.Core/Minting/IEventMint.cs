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

  /// <summary>The checkpoint family — the control class's TTL authority behind every control-plane publisher.</summary>
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
/// The checkpoint mint family: where a control-class message acquires its broker LIFETIME.
/// A supersedable control signal is re-derived on the next cadence, so minting it with
/// <c>TimeToLive ≈ 2× cadence</c> means a superseded copy expires on the broker instead of
/// queueing — a control backlog becomes structurally impossible rather than merely unlikely.
/// </summary>
/// <remarks>
/// The derivation lives here, at construction, precisely so it is not recomputed at every
/// control-plane publish site. Callers stamp the minted lifetime onto their destination with
/// <c>ControlMessageTtl.Stamp</c>; each transport lifts it into its native expiry.
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CheckpointMintTests.cs</tests>
public interface ICheckpointMint {
  /// <summary>
  /// Mints <paramref name="request"/>'s payload with the lifetime its cadence implies.
  /// </summary>
  /// <typeparam name="TPayload">The control message type.</typeparam>
  /// <param name="request">The payload plus the cadence it is emitted on.</param>
  /// <returns>The unchanged payload plus its lifetime (null when TTL minting is disabled).</returns>
  MintedControlMessage<TPayload> Mint<TPayload>(ControlMintRequest<TPayload> request)
    where TPayload : notnull;
}
