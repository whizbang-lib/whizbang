using System;
using Microsoft.Extensions.Options;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Minting;

/// <summary>
/// A request to mint a control-class message: the payload plus the cadence it is emitted on.
/// The cadence — not a hard-coded duration — is what the mint needs, because the class's rule is
/// relative ("outlive your own emission by <c>CadenceMultiplier</c> cadences"), so a host that
/// retunes a worker's interval gets a correct TTL without touching the publish site.
/// </summary>
/// <typeparam name="TPayload">The control message type.</typeparam>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CheckpointMintTests.cs:Mint_DerivesTimeToLiveFromTheCadenceAsync</tests>
public sealed record ControlMintRequest<TPayload> where TPayload : notnull {
  /// <summary>The control message being minted.</summary>
  public required TPayload Payload { get; init; }

  /// <summary>The emitter's cadence — the interval between successive emissions of this signal.</summary>
  public required TimeSpan Cadence { get; init; }

  /// <summary>
  /// Per-call lifetime override, for an emitter whose cadence does not describe its supersession
  /// (a one-shot answer to a request, say). Beats both the derivation and the configured override.
  /// </summary>
  public TimeSpan? TimeToLive { get; init; }
}

/// <summary>
/// A minted control-class message: the payload, unchanged, plus the broker lifetime it must carry.
/// Null <see cref="TimeToLive"/> means "no lifetime" — the killswitch result, and the pre-phase-9
/// wire shape exactly.
/// </summary>
/// <typeparam name="TPayload">The control message type.</typeparam>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CheckpointMintTests.cs:Mint_Disabled_MintsNoTimeToLiveAsync</tests>
public sealed record MintedControlMessage<TPayload> where TPayload : notnull {
  /// <summary>The control message, byte-for-byte as the caller supplied it.</summary>
  public required TPayload Payload { get; init; }

  /// <summary>The lifetime to stamp on the destination, or null for the broker default.</summary>
  public required TimeSpan? TimeToLive { get; init; }
}

/// <summary>
/// Default <see cref="ICheckpointMint"/> — the control class's TTL authority (topology arc
/// phase 9). Every control-plane publish site mints here instead of computing a lifetime of its
/// own, so the rule lives in exactly one place and a host retunes it through
/// <see cref="ControlClassOptions"/> rather than through code.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CheckpointMintTests.cs</tests>
public sealed class CheckpointMint : ICheckpointMint {
  private readonly ControlClassOptions _options;

  /// <summary>Creates a mint over the shipped defaults (test/manual construction).</summary>
  public CheckpointMint() : this(Options.Create(new ControlClassOptions())) {
  }

  /// <summary>Creates a mint over the host's control-class options.</summary>
  /// <param name="options">The control-class options.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
  public CheckpointMint(IOptions<ControlClassOptions> options) {
    ArgumentNullException.ThrowIfNull(options);
    _options = options.Value;
  }

  /// <inheritdoc />
  public MintedControlMessage<TPayload> Mint<TPayload>(ControlMintRequest<TPayload> request)
      where TPayload : notnull {
    ArgumentNullException.ThrowIfNull(request);

    return new MintedControlMessage<TPayload> {
      Payload = request.Payload,
      TimeToLive = _options.Enabled
        ? request.TimeToLive ?? _options.EffectiveTimeToLive(request.Cadence)
        : null,
    };
  }
}
