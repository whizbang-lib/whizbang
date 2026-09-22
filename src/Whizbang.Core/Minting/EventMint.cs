using System;

namespace Whizbang.Core.Minting;

/// <summary>
/// Default <see cref="IEventMint"/> — pure aggregation of the family services the container
/// resolved; no wrapping, no logic. See <see cref="IEventMint"/>.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#minting</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/EventMintTests.cs:EventMint_ExposesTheConstructedFamilies_UnchangedAsync</tests>
/// <remarks>Aggregates the resolved family services.</remarks>
/// <param name="composites">The composite family.</param>
/// <param name="collective">The collective family (placeholder until phase 6).</param>
/// <param name="checkpoints">The checkpoint family (control-class TTL minting).</param>
/// <exception cref="ArgumentNullException">Thrown when any family is null.</exception>
public sealed class EventMint(ICompositeFactory composites, ICollectiveMint collective, ICheckpointMint checkpoints) : IEventMint {

  /// <inheritdoc />
  public ICompositeFactory Composites { get; } = composites ?? throw new ArgumentNullException(nameof(composites));

  /// <inheritdoc />
  public ICollectiveMint Collective { get; } = collective ?? throw new ArgumentNullException(nameof(collective));

  /// <inheritdoc />
  public ICheckpointMint Checkpoints { get; } = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
}

/// <summary>
/// Default (placeholder) <see cref="ICollectiveMint"/> — carries no members yet; the collective
/// minting surface lands with topology arc phase 6.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/EventMintTests.cs</tests>
public sealed class CollectiveMint : ICollectiveMint;
