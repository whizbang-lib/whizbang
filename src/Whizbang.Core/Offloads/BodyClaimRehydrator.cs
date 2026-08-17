using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Offloads;

/// <summary>
/// Receive-side rehydrate for body-offload claim envelopes. The transport
/// deserializes a wire message with the <c>whizbang.is-claim</c> header
/// as <see cref="MessageEnvelope{T}"/> of <see cref="BodyClaimEnvelopePayload"/>;
/// this rehydrator downloads the original body bytes via the registered
/// <see cref="IMessageBodyStore"/>, verifies the SHA-256 hash, deserializes
/// the bytes as the original envelope type, and returns the rehydrated
/// envelope so downstream code (inbox serialization, receptors, perspectives)
/// processes the original message as if the claim wasn't there.
/// </summary>
/// <docs>fundamentals/offloads/message-body-store</docs>
public static class BodyClaimRehydrator {

  /// <summary>
  /// Inspects <paramref name="envelope"/>. When its payload is a
  /// <see cref="BodyClaimEnvelopePayload"/>, downloads + hash-verifies the
  /// original body and returns the rehydrated envelope. Otherwise returns a
  /// pass-through (the original envelope unchanged). On dead-letter
  /// conditions (provider unknown / hash mismatch) returns a failure result
  /// the caller routes to DLQ.
  /// </summary>
  public static async Task<RehydrateResult> MaybeRehydrateAsync(
      IMessageEnvelope envelope,
      string? envelopeTypeHeader,
      JsonSerializerOptions jsonOptions,
      IServiceProvider serviceProvider,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(jsonOptions);
    ArgumentNullException.ThrowIfNull(serviceProvider);

    if (envelope.Payload is not BodyClaimEnvelopePayload claimPayload) {
      // Not a claim — pass through unchanged.
      return RehydrateResult.PassThrough(envelope, envelopeTypeHeader);
    }

    var claim = claimPayload.Claim;

    var store = serviceProvider.GetKeyedService<IMessageBodyStore>(claim.ProviderName);
    if (store is null) {
      return RehydrateResult.DeadLetter(
        MessageFailureReason.BodyClaimProviderUnknown,
        $"Receiver does not have an IMessageBodyStore registered under provider name '{claim.ProviderName}'. Register the matching AddWhizbang*Offload(name) (e.g., AddWhizbangInMemoryOffload, AddWhizbangAzureBlobOffload) on the receiver service.");
    }

    var downloadTimeout = serviceProvider.GetService<IOptions<MessageBodyOffloadOptions>>()?.Value?.DownloadTimeout
      ?? TimeSpan.FromSeconds(100);
    ReadOnlyMemory<byte> downloaded;
    using (var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
      downloadCts.CancelAfter(downloadTimeout);
      try {
        downloaded = await store.DownloadAsync(claim, options: null, downloadCts.Token);
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        throw;  // genuine host shutdown — propagate untouched so the consumer stops cleanly.
      } catch (Exception ex) {
        // Transient store/network failure OR a bounded download timeout. Throw (do NOT dead-letter):
        // the body is very likely present on a later attempt, so the transport redelivers the message
        // for a bounded, automatic retry, and only after the transport's max-delivery count does it
        // DLQ. Integrity / deserialization / unknown-provider failures below stay terminal dead-letters.
        var reason = downloadCts.IsCancellationRequested
          ? $"exceeded the {downloadTimeout.TotalSeconds:0}s download timeout"
          : $"{ex.GetType().Name}: {ex.Message}";
        throw new BodyClaimDownloadException(
          $"Body store '{claim.ProviderName}' download failed for claim '{claim.StorageKey}' ({reason}) — will retry via redelivery.", ex);
      }
    }

    var actualHash = "sha256-" + Convert.ToHexString(SHA256.HashData(downloaded.Span));
    if (!string.Equals(actualHash, claim.ContentHash, StringComparison.Ordinal)) {
      return RehydrateResult.DeadLetter(
        MessageFailureReason.BodyClaimIntegrityFailure,
        $"Body integrity check failed: claim expected {claim.ContentHash}, downloaded body hashes to {actualHash}. Provider '{claim.ProviderName}' storage key '{claim.StorageKey}'.");
    }

    var typeInfo = Whizbang.Core.Serialization.JsonContextRegistry.GetTypeInfoByName(claimPayload.OriginalTypeName, jsonOptions);
    if (typeInfo is null) {
      return RehydrateResult.DeadLetter(
        MessageFailureReason.SerializationError,
        $"No JsonTypeInfo registered for the original envelope type '{claimPayload.OriginalTypeName}'. Receiver cannot rehydrate the body — register the type in a JsonSerializerContext.");
    }

    IMessageEnvelope? rehydrated;
    try {
      rehydrated = JsonSerializer.Deserialize(downloaded.Span, typeInfo) as IMessageEnvelope;
    } catch (JsonException ex) {
      return RehydrateResult.DeadLetter(
        MessageFailureReason.SerializationError,
        $"Failed to deserialize rehydrated body as '{claimPayload.OriginalTypeName}': {ex.Message}");
    }

    if (rehydrated is null) {
      return RehydrateResult.DeadLetter(
        MessageFailureReason.SerializationError,
        $"Deserialized rehydrated body but result is null or wrong shape; expected IMessageEnvelope.");
    }

    // Observe the rehydration (bounded dimensions: message type + namespace — never message IDs).
    var metrics = serviceProvider.GetService<TransportMetrics>();
    if (metrics is not null) {
      var typeTag = new KeyValuePair<string, object?>("message.type", TypeNameFormatter.GetPayloadFullName(claimPayload.OriginalTypeName));
      var nsTag = new KeyValuePair<string, object?>("message.namespace", TypeNameFormatter.GetPayloadNamespace(claimPayload.OriginalTypeName));
      metrics.BodyClaimRehydratedCount.Add(1, typeTag, nsTag);
      metrics.BodyClaimRehydratedBytes.Record(downloaded.Length, typeTag, nsTag);
    }

    // Active-cleanup option: when MessageBodyOffloadOptions.ActiveCleanup is true,
    // surface the claim so the receive-side worker can delete the body after the
    // inbox row commits. When false (default), receivers rely on the provider's
    // storage-level TTL / lifecycle rule — simpler, safer in fan-out topologies.
    var options = serviceProvider.GetService<IOptionsMonitor<MessageBodyOffloadOptions>>()?.CurrentValue;
    var cleanupClaim = options?.ActiveCleanup == true ? claim : null;

    return RehydrateResult.Rehydrated(rehydrated, claimPayload.OriginalTypeName, cleanupClaim);
  }
}

/// <summary>
/// Outcome of <see cref="BodyClaimRehydrator.MaybeRehydrateAsync"/>.
/// Either the envelope was not a claim (pass-through), the claim was
/// successfully rehydrated, or the message must be dead-lettered.
/// </summary>
public sealed record RehydrateResult {
  /// <summary>The envelope to continue processing (original or rehydrated). Null when <see cref="IsDeadLetter"/> is true.</summary>
  public IMessageEnvelope? Envelope { get; init; }

  /// <summary>The envelope type name to use downstream (header value for pass-through; <c>OriginalTypeName</c> for rehydrated).</summary>
  public string? EnvelopeType { get; init; }

  /// <summary>True if processing must stop and the message be dead-lettered.</summary>
  public bool IsDeadLetter { get; init; }

  /// <summary>Failure reason when <see cref="IsDeadLetter"/> is true.</summary>
  public MessageFailureReason FailureReason { get; init; }

  /// <summary>Human-readable failure description for logs / dashboards.</summary>
  public string? FailureDescription { get; init; }

  /// <summary>True when the input envelope carried a body claim and was successfully rehydrated.</summary>
  public bool WasRehydrated { get; init; }

  /// <summary>
  /// When <see cref="MessageBodyOffloadOptions.ActiveCleanup"/> is enabled
  /// AND this result represents a successful rehydrate, carries the claim
  /// the worker should delete after the inbox commit succeeds. Null
  /// otherwise — receivers rely on the provider's storage-level TTL.
  /// </summary>
  public MessageBodyClaim? PendingCleanupClaim { get; init; }

  /// <summary>Builds a pass-through result (input was not a claim envelope).</summary>
  public static RehydrateResult PassThrough(IMessageEnvelope envelope, string? envelopeType)
    => new() { Envelope = envelope, EnvelopeType = envelopeType, IsDeadLetter = false, WasRehydrated = false };

  /// <summary>Builds a rehydrated result (input was a claim envelope; the body was downloaded + deserialized successfully).</summary>
  public static RehydrateResult Rehydrated(IMessageEnvelope envelope, string envelopeType, MessageBodyClaim? pendingCleanupClaim = null)
    => new() {
      Envelope = envelope,
      EnvelopeType = envelopeType,
      IsDeadLetter = false,
      WasRehydrated = true,
      PendingCleanupClaim = pendingCleanupClaim
    };

  /// <summary>Builds a dead-letter result (rehydrate failed and the message MUST NOT be processed).</summary>
  public static RehydrateResult DeadLetter(MessageFailureReason reason, string description)
    => new() { IsDeadLetter = true, FailureReason = reason, FailureDescription = description };
}
