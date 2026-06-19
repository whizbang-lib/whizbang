using System.Text.Json;

namespace Whizbang.Core.Offloads;

/// <summary>
/// Iterates a registered set of <see cref="IPostSerializeHook"/> instances
/// in <see cref="IPostSerializeHook.Order"/> ascending, threading each
/// hook's output into the next hook's input. Produces a single final
/// <see cref="PostSerializeOutcome"/> that the publish strategy hands to
/// the transport.
/// </summary>
/// <remarks>
/// The chain itself is a thin orchestrator — all the logic lives in the
/// individual hooks. Registered as a singleton; consumes
/// <see cref="IEnumerable{T}"/> of <see cref="IPostSerializeHook"/> from DI.
/// </remarks>
/// <docs>offloads</docs>
public sealed class PostSerializeHookChain {
  private readonly IPostSerializeHook[] _orderedHooks;

  /// <summary>
  /// Builds the chain from all registered hooks. Sorts once at
  /// construction (singleton lifetime); subsequent invocations iterate
  /// the pre-sorted array without per-call allocation.
  /// </summary>
  public PostSerializeHookChain(IEnumerable<IPostSerializeHook> hooks) {
    ArgumentNullException.ThrowIfNull(hooks);
    _orderedHooks = hooks.OrderBy(h => h.Order).ToArray();
  }

  /// <summary>True when no hooks are registered — chain is a no-op.</summary>
  public bool IsEmpty => _orderedHooks.Length == 0;

  /// <summary>
  /// Runs every registered hook in order. Each hook receives the chain's
  /// current state (envelope/bytes/metadata) and may replace any of them.
  /// </summary>
  public async Task<PostSerializeOutcome> RunAsync(PostSerializeContext initialContext, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(initialContext);

    var envelope = initialContext.Envelope;
    var envelopeType = initialContext.EnvelopeType;
    var bytes = initialContext.SerializedBytes;
    var contentType = initialContext.ContentType;
    var mergedMetadata = _copyMetadata(initialContext.Destination.Metadata);

    foreach (var hook in _orderedHooks) {
      cancellationToken.ThrowIfCancellationRequested();

      var ctx = initialContext with {
        Envelope = envelope,
        EnvelopeType = envelopeType,
        SerializedBytes = bytes,
        ContentType = contentType,
        Destination = initialContext.Destination with {
          Metadata = mergedMetadata.Count == 0 ? null : mergedMetadata
        }
      };

      var result = await hook.RunAsync(ctx, cancellationToken);
      if (result is null) {
        continue;
      }

      if (result.NewEnvelope is not null) {
        envelope = result.NewEnvelope;
      }
      if (result.NewEnvelopeType is not null) {
        envelopeType = result.NewEnvelopeType;
      }
      if (result.NewSerializedBytes is { } newBytes) {
        bytes = newBytes;
      }
      if (result.NewContentType is not null) {
        contentType = result.NewContentType;
      }
      if (result.AdditionalDestinationMetadata is { } add) {
        foreach (var (k, v) in add) {
          mergedMetadata[k] = v;
        }
      }
    }

    return new PostSerializeOutcome(
      envelope,
      envelopeType,
      bytes,
      contentType,
      mergedMetadata.Count == 0 ? null : mergedMetadata
    );
  }

  private static Dictionary<string, JsonElement> _copyMetadata(IReadOnlyDictionary<string, JsonElement>? source) {
    if (source is null || source.Count == 0) {
      return [];
    }
    var copy = new Dictionary<string, JsonElement>(source.Count);
    foreach (var (k, v) in source) {
      copy[k] = v;
    }
    return copy;
  }
}

/// <summary>
/// Final result of running the hook chain — the values the publish
/// strategy uses to call the transport.
/// </summary>
/// <param name="FinalEnvelope">The envelope after all hooks. May be the original or a substitute (e.g., a claim envelope built by the offload hook).</param>
/// <param name="FinalEnvelopeType">Assembly-qualified type name of <paramref name="FinalEnvelope"/>.</param>
/// <param name="FinalSerializedBytes">Final bytes to wire-send. May be the original or any hook's replacement.</param>
/// <param name="FinalContentType">Final content type.</param>
/// <param name="MergedDestinationMetadata">Original destination metadata merged with every hook's additions. Null if both were empty/null.</param>
public sealed record PostSerializeOutcome(
  Whizbang.Core.Observability.IMessageEnvelope FinalEnvelope,
  string FinalEnvelopeType,
  ReadOnlyMemory<byte> FinalSerializedBytes,
  string FinalContentType,
  IReadOnlyDictionary<string, JsonElement>? MergedDestinationMetadata
);
