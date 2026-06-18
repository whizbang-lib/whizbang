using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Offloads;

/// <summary>
/// Post-serialize hook that implements the claim-check (body offload)
/// pattern. When the serialized bytes exceed
/// <see cref="MessageBodyOffloadOptions.SizeThresholdBytes"/> OR the
/// transport's <c>MaxMessageSizeBytes</c>, this hook:
///
/// <list type="number">
///   <item>Uploads the original bytes to the configured <see cref="IMessageBodyStore"/></item>
///   <item>Builds a claim envelope: same metadata as the original (MessageId, Hops, DispatchContext, source-service stamps), payload = <see cref="BodyClaimEnvelopePayload"/></item>
///   <item>Re-serializes the claim envelope using the supplied <see cref="JsonSerializerOptions"/></item>
///   <item>Adds <c>whizbang.is-claim: true</c>, <c>whizbang.body-store: &lt;providerName&gt;</c>, and <c>whizbang.original-type: &lt;typeName&gt;</c> to destination metadata so receivers can detect and rehydrate</item>
/// </list>
///
/// On the receiver side, the rehydrate flow uses these headers to fetch
/// the original body via the matching provider and deserialize it as
/// <c>whizbang.original-type</c>.
/// </summary>
/// <remarks>
/// Runs at <see cref="Order"/> = 1000 so simpler hooks (size measurement,
/// compression) see the original bytes. Encryption / signing (Order 2000+)
/// should run after offload so the small claim envelope is also covered.
/// </remarks>
/// <docs>fundamentals/offloads/body-offload-hook</docs>
public sealed class BodyOffloadPostSerializeHook : IPostSerializeHook {
#pragma warning disable CA1707
  /// <summary>Destination metadata key set to <c>true</c> when the wire bytes are a claim envelope.</summary>
  public const string IS_CLAIM_METADATA_KEY = "whizbang.is-claim";

  /// <summary>Destination metadata key carrying the body-store provider name.</summary>
  public const string BODY_STORE_METADATA_KEY = "whizbang.body-store";

  /// <summary>Destination metadata key carrying the assembly-qualified original envelope type name.</summary>
  public const string ORIGINAL_TYPE_METADATA_KEY = "whizbang.original-type";
#pragma warning restore CA1707

  private readonly IServiceProvider _serviceProvider;
  private readonly IOptionsMonitor<MessageBodyOffloadOptions> _options;

  /// <summary>Builds the hook. <see cref="IServiceProvider"/> is used to resolve the keyed body store at invocation time so options changes don't require DI rebuild.</summary>
  public BodyOffloadPostSerializeHook(
      IServiceProvider serviceProvider,
      IOptionsMonitor<MessageBodyOffloadOptions> options) {
    _serviceProvider = serviceProvider;
    _options = options;
  }

  /// <inheritdoc />
  public int Order => 1000;

  /// <inheritdoc />
  public async Task<PostSerializeResult> RunAsync(PostSerializeContext context, CancellationToken cancellationToken) {
    var opts = _options.CurrentValue;

    // No provider configured → never offload. Caller (transport) will hit
    // wire-ceiling failures on its own if the message is too big; the
    // publish strategy decides whether to hard-fail or let the transport
    // raise.
    if (string.IsNullOrWhiteSpace(opts.ProviderName)) {
      return PostSerializeResult.PassThrough();
    }

    var size = context.SerializedBytes.Length;
    var underAppThreshold = size < opts.SizeThresholdBytes;
    var underTransportCeiling = context.TransportMaxMessageSizeBytes is not long max || size <= max;

    if (underAppThreshold && underTransportCeiling) {
      return PostSerializeResult.PassThrough();
    }

    var store = _serviceProvider.GetKeyedService<IMessageBodyStore>(opts.ProviderName)
      ?? throw new InvalidOperationException(
        $"MessageBodyOffloadOptions.ProviderName = '{opts.ProviderName}' but no IMessageBodyStore was registered under that key. " +
        "Register via services.AddWhizbangMessageBodyStore<T>(name) (or a typed wrapper like AddWhizbangInMemoryOffload(name)) before the offload hook runs.");

    var claim = await store.UploadAsync(context.SerializedBytes, context.ContentType, options: null, cancellationToken);

    // Observe the offload (bounded dimensions: message type + namespace — never message IDs).
    var metrics = _serviceProvider.GetService<TransportMetrics>();
    if (metrics is not null) {
      var typeTag = new KeyValuePair<string, object?>("message.type", Whizbang.Core.TypeNameFormatter.GetPayloadFullName(context.EnvelopeType));
      var nsTag = new KeyValuePair<string, object?>("message.namespace", Whizbang.Core.TypeNameFormatter.GetPayloadNamespace(context.EnvelopeType));
      metrics.BodyOffloadCount.Add(1, typeTag, nsTag);
      metrics.BodyOffloadBytes.Record(size, typeTag, nsTag);
    }

    var claimPayload = new BodyClaimEnvelopePayload(claim, context.ContentType, context.EnvelopeType);
    var claimEnvelope = _buildClaimEnvelope(context.Envelope, claimPayload);
    var claimEnvelopeType = claimEnvelope.GetType().AssemblyQualifiedName
      ?? throw new InvalidOperationException("Claim envelope type must have an assembly-qualified name.");

    var typeInfo = context.JsonOptions.GetTypeInfo(claimEnvelope.GetType())
      ?? throw new InvalidOperationException(
        $"No JsonTypeInfo found for claim envelope type {claimEnvelope.GetType().FullName}. " +
        "Ensure MessageEnvelope<BodyClaimEnvelopePayload> is registered via JsonContextRegistry.");

    var claimJson = JsonSerializer.Serialize(claimEnvelope, typeInfo);
    var claimBytes = Encoding.UTF8.GetBytes(claimJson);

    var addedMetadata = new Dictionary<string, JsonElement> {
      [IS_CLAIM_METADATA_KEY] = JsonDocument.Parse("true").RootElement,
      [BODY_STORE_METADATA_KEY] = JsonDocument.Parse(_jsonQuote(opts.ProviderName!)).RootElement,
      [ORIGINAL_TYPE_METADATA_KEY] = JsonDocument.Parse(_jsonQuote(context.EnvelopeType)).RootElement,
    };

    return new PostSerializeResult {
      NewEnvelope = claimEnvelope,
      NewEnvelopeType = claimEnvelopeType,
      NewSerializedBytes = claimBytes,
      NewContentType = context.ContentType,
      AdditionalDestinationMetadata = addedMetadata,
    };
  }

  /// <summary>
  /// Hand-rolled JSON string-literal builder so we don't reach for
  /// reflection-based JsonSerializer.Serialize&lt;string&gt;, which trips
  /// IL2026 in AOT mode. Escapes the four chars that matter for
  /// metadata header values: backslash, quote, newline, tab.
  /// </summary>
  private static string _jsonQuote(string value) {
    var sb = new StringBuilder(value.Length + 2);
    sb.Append('"');
    foreach (var c in value) {
      switch (c) {
        case '\\': sb.Append("\\\\"); break;
        case '"': sb.Append("\\\""); break;
        case '\n': sb.Append("\\n"); break;
        case '\r': sb.Append("\\r"); break;
        case '\t': sb.Append("\\t"); break;
        default:
          if (c < 0x20) {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
          } else {
            sb.Append(c);
          }
          break;
      }
    }
    sb.Append('"');
    return sb.ToString();
  }

  /// <summary>
  /// Constructs a claim envelope that preserves the original envelope's
  /// identity, journey, and source-service stamps; only the payload
  /// changes. Routing-visible properties (Subject derived from the
  /// original type elsewhere) are unaffected.
  /// </summary>
  private static MessageEnvelope<BodyClaimEnvelopePayload> _buildClaimEnvelope(
      IMessageEnvelope original,
      BodyClaimEnvelopePayload payload) {
    return new MessageEnvelope<BodyClaimEnvelopePayload> {
      Version = original.Version,
      DispatchContext = original.DispatchContext,
      MessageId = original.MessageId,
      Payload = payload,
      Hops = original.Hops,
      ReceptorInvocations = original.ReceptorInvocations,
      SourceServiceId = original.SourceServiceId,
      SourceCommitSequence = original.SourceCommitSequence,
      CausedByServiceId = original.CausedByServiceId,
      CausedByCommitSequence = original.CausedByCommitSequence,
    };
  }
}
