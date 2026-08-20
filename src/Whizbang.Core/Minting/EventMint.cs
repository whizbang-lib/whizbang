using System;

namespace Whizbang.Core.Minting;

/// <summary>
/// Default <see cref="IEventMint"/> — pure aggregation of the family services the container
/// resolved; no wrapping, no logic. See <see cref="IEventMint"/>.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#minting</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/EventMintTests.cs:EventMint_ExposesTheConstructedFamilies_UnchangedAsync</tests>
public sealed class EventMint : IEventMint {
  /// <summary>Aggregates the resolved family services.</summary>
  /// <param name="composites">The composite family.</param>
  /// <param name="collective">The collective family (placeholder until phase 6).</param>
  /// <param name="checkpoints">The checkpoint family (placeholder until phase 9).</param>
  /// <exception cref="ArgumentNullException">Thrown when any family is null.</exception>
  public EventMint(ICompositeFactory composites, ICollectiveMint collective, ICheckpointMint checkpoints) {
    Composites = composites ?? throw new ArgumentNullException(nameof(composites));
    Collective = collective ?? throw new ArgumentNullException(nameof(collective));
    Checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
  }

  /// <inheritdoc />
  public ICompositeFactory Composites { get; }

  /// <inheritdoc />
  public ICollectiveMint Collective { get; }

  /// <inheritdoc />
  public ICheckpointMint Checkpoints { get; }
}

/// <summary>
/// Default (placeholder) <see cref="ICollectiveMint"/> — carries no members yet; the collective
/// minting surface lands with topology arc phase 6.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/EventMintTests.cs</tests>
public sealed class CollectiveMint : ICollectiveMint {
}

/// <summary>
/// Default (placeholder) <see cref="ICheckpointMint"/> — carries no members yet; the checkpoint
/// minting surface lands with topology arc phase 9.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/EventMintTests.cs</tests>
public sealed class CheckpointMint : ICheckpointMint {
}
