using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Offloads;

/// <summary>
/// Shared helpers used by wire transports to detect body-offload claim
/// envelopes during receive and rehydrate them upstream. Keeps the
/// transport-side claim handling DRY across RabbitMQ + Azure Service Bus
/// + future wire transports.
/// </summary>
/// <docs>fundamentals/offloads/body-offload-receive</docs>
public static class BodyClaimWireHelper {

  /// <summary>
  /// Returns the <see cref="JsonTypeInfo"/> to use for deserializing a wire
  /// message based on whether the <c>whizbang.is-claim</c> header is set.
  /// When the header is present and truthy, the wire bytes are a
  /// <see cref="MessageEnvelope{T}"/> of <see cref="BodyClaimEnvelopePayload"/>
  /// (NOT the type the <c>ENVELOPE_TYPE_HEADER</c> claims — that header
  /// stays as the original type for filter-routing visibility).
  /// </summary>
  /// <param name="envelopeTypeHeaderValue">The value of the transport's envelope-type header (assembly-qualified type name of the ORIGINAL envelope).</param>
  /// <param name="isClaimHeaderValue">The value of <c>whizbang.is-claim</c> header; <c>null</c> = absent. Comparison is exact-string against <c>"true"</c>.</param>
  /// <param name="jsonOptions">JsonSerializerOptions populated via <c>JsonContextRegistry.CreateCombinedOptions</c>.</param>
  /// <returns>The JsonTypeInfo to use for deserialization, or <c>null</c> if no matching type info is registered.</returns>
  public static JsonTypeInfo? ResolveDeserializeTypeInfo(
      string envelopeTypeHeaderValue,
      string? isClaimHeaderValue,
      JsonSerializerOptions jsonOptions)
    => ResolveDeserializeTypeInfo(envelopeTypeHeaderValue, isClaimHeaderValue, jsonOptions, typeInfoByName: null);

  /// <summary>
  /// Resolver-delegate overload of <see cref="ResolveDeserializeTypeInfo(string,string?,JsonSerializerOptions)"/>.
  /// Lets a transport inject its own type-name → <see cref="JsonTypeInfo"/> resolver (e.g. for
  /// testability) while still sharing the claim-detection + resolve flow. When
  /// <paramref name="typeInfoByName"/> is null, falls back to
  /// <c>JsonContextRegistry.GetTypeInfoByName</c> (identical to the 3-arg overload). The claim path
  /// resolves <see cref="MessageEnvelope{T}"/> of <see cref="BodyClaimEnvelopePayload"/> from
  /// <paramref name="jsonOptions"/> and never consults the resolver.
  /// </summary>
  /// <param name="typeInfoByName">Optional resolver from a CLR type name to a registered <see cref="JsonTypeInfo"/>.</param>
  public static JsonTypeInfo? ResolveDeserializeTypeInfo(
      string envelopeTypeHeaderValue,
      string? isClaimHeaderValue,
      JsonSerializerOptions jsonOptions,
      Func<string, JsonSerializerOptions, JsonTypeInfo?>? typeInfoByName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(envelopeTypeHeaderValue);
    ArgumentNullException.ThrowIfNull(jsonOptions);

    if (IsClaimHeader(isClaimHeaderValue)) {
      return jsonOptions.GetTypeInfo(typeof(MessageEnvelope<BodyClaimEnvelopePayload>));
    }

    var resolver = typeInfoByName ?? Whizbang.Core.Serialization.JsonContextRegistry.GetTypeInfoByName;
    return resolver(envelopeTypeHeaderValue, jsonOptions);
  }

  /// <summary>
  /// Returns true when the supplied header value matches the truthy
  /// representation written by <see cref="BodyOffloadPostSerializeHook"/>
  /// — currently the exact string <c>"true"</c>.
  /// </summary>
  public static bool IsClaimHeader(string? isClaimHeaderValue) =>
    string.Equals(isClaimHeaderValue, "true", StringComparison.Ordinal);
}
