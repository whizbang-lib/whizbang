using System.Text.Json;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Offloads;

/// <summary>
/// A pluggable hook the publish pipeline invokes after the envelope is
/// serialized to bytes but before the transport's wire-send. Each hook may
/// inspect the bytes, replace them with a substitute envelope's bytes,
/// add destination metadata (e.g., headers), or pass through unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The body-offload feature (claim-check pattern) is one such hook —
/// see <c>BodyOffloadPostSerializeHook</c>. Other use cases follow the
/// same shape: compression, encryption, audit-tagging, custom envelope
/// transforms.
/// </para>
/// <para>
/// Hooks run in <see cref="Order"/> ascending. Each hook sees the prior
/// hook's output as its input (chained pipeline). The publish strategy
/// finalizes after the last hook and hands the result to the transport
/// via the <c>preSerializedBytes</c> hint on
/// <see cref="ITransport.PublishAsync"/>.
/// </para>
/// </remarks>
/// <docs>offloads</docs>
public interface IPostSerializeHook {
  /// <summary>
  /// Lower runs first. Conventions:
  /// <list type="bullet">
  ///   <item>100 = observability / size measurement (pre-mutation)</item>
  ///   <item>500 = compression / encoding (early in the pipeline)</item>
  ///   <item>1000 = body offload / claim-check (runs late so prior hooks see real bytes)</item>
  ///   <item>2000 = encryption / signing (runs after offload so the claim envelope is also protected)</item>
  /// </list>
  /// </summary>
  int Order { get; }

  /// <summary>
  /// Inspect <paramref name="context"/>'s current bytes + envelope and
  /// return a <see cref="PostSerializeResult"/> describing the change
  /// (or pass-through). Receivers handle any side effects the hook
  /// signaled via additional destination metadata or a substituted
  /// envelope.
  /// </summary>
  Task<PostSerializeResult> RunAsync(PostSerializeContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Input to <see cref="IPostSerializeHook.RunAsync"/>. The publish strategy
/// builds this once and threads it through the chain; each hook receives the
/// chain's current state (bytes/envelope/destination), not the original.
/// </summary>
/// <param name="Envelope">The current envelope at this point in the chain — may be the original or a substitute from a prior hook.</param>
/// <param name="EnvelopeType">Assembly-qualified type name of <paramref name="Envelope"/>. Stays accurate as the chain replaces envelopes.</param>
/// <param name="SerializedBytes">The bytes produced by the last serialization step — original envelope's serialization for the first hook; the prior hook's replacement bytes for subsequent hooks.</param>
/// <param name="ContentType">MIME type of <paramref name="SerializedBytes"/>. Usually <c>"application/json"</c>; hooks can change this if they replace bytes with a different encoding.</param>
/// <param name="TransportMaxMessageSizeBytes">Hard wire-ceiling reported by the destination transport's <see cref="ITransport.MaxMessageSizeBytes"/>; null = no enforced limit.</param>
/// <param name="JsonOptions">JsonSerializerOptions in use by the transport — hooks that materialize a replacement envelope use these to re-serialize.</param>
/// <param name="Destination">Resolved transport destination, including metadata. Hooks read but don't mutate; use <see cref="PostSerializeResult.AdditionalDestinationMetadata"/> to add headers.</param>
public sealed record PostSerializeContext(
  IMessageEnvelope Envelope,
  string EnvelopeType,
  ReadOnlyMemory<byte> SerializedBytes,
  string ContentType,
  long? TransportMaxMessageSizeBytes,
  JsonSerializerOptions JsonOptions,
  TransportDestination Destination
);

/// <summary>
/// Output of a hook invocation. The publish strategy merges this back into
/// the chain state for the next hook.
/// </summary>
public sealed record PostSerializeResult {
  /// <summary>Replacement envelope, or null to keep the current one.</summary>
  public IMessageEnvelope? NewEnvelope { get; init; }

  /// <summary>Assembly-qualified type name of <see cref="NewEnvelope"/>, or null if envelope unchanged.</summary>
  public string? NewEnvelopeType { get; init; }

  /// <summary>Replacement serialized bytes, or null to keep the current ones.</summary>
  public ReadOnlyMemory<byte>? NewSerializedBytes { get; init; }

  /// <summary>Replacement content type, or null to keep the current one.</summary>
  public string? NewContentType { get; init; }

  /// <summary>
  /// Headers / metadata the hook wants merged into the outgoing
  /// destination metadata (e.g., <c>whizbang.body-store</c>,
  /// <c>whizbang.is-claim</c>). Keys with non-<c>whizbang.</c> prefixes
  /// are still applied — the convention exists to keep transport-level
  /// facts namespaced and discoverable.
  /// </summary>
  public IReadOnlyDictionary<string, System.Text.Json.JsonElement>? AdditionalDestinationMetadata { get; init; }

  /// <summary>Convenience: pass-through (no changes).</summary>
  public static PostSerializeResult PassThrough() => new();
}
