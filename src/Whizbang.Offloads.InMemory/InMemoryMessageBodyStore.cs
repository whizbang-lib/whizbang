using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Offloads;

namespace Whizbang.Offloads.InMemory;

/// <summary>
/// In-memory <see cref="IMessageBodyStore"/> for dev, test, and integration
/// fixtures. Bodies live in a process-local <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// and disappear on restart. NOT suitable for production: bodies aren't
/// shared across processes/services, and there's no durability.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the role of <c>Whizbang.Transports.InMemory</c> in the transport
/// pattern — fast, deterministic, isolated, and lets the rest of the
/// offload pipeline (send-side strategy, receive-side resolver, composite
/// expansion) be exercised end-to-end without an external blob service.
/// </para>
/// <para>
/// Computes a SHA-256 <see cref="MessageBodyClaim.ContentHash"/> on upload
/// so receivers can verify integrity. Storage keys are UUIDv4 — opaque to
/// callers; only the claim ticket matters.
/// </para>
/// </remarks>
/// <docs>fundamentals/offloads/providers/in-memory</docs>
public sealed class InMemoryMessageBodyStore : IMessageBodyStore {
  private readonly ConcurrentDictionary<string, byte[]> _bodies = new();

  /// <summary>
  /// Builds a store whose <see cref="ProviderName"/> is supplied by DI.
  /// The DI key registered via <see cref="InMemoryOffloadServiceCollectionExtensions.AddWhizbangInMemoryOffload"/>
  /// is passed through here so the receiver-side resolver can match.
  /// </summary>
  public InMemoryMessageBodyStore([ServiceKey] string providerName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
    ProviderName = providerName;
  }

  /// <inheritdoc />
  public string ProviderName { get; }

  /// <inheritdoc />
  public Task<MessageBodyClaim> UploadAsync(
      ReadOnlyMemory<byte> body,
      string contentType,
      MessageBodyUploadOptions? options = null,
      CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

    var storageKey = $"inmemory://{Guid.NewGuid():N}";
    var copy = body.ToArray();          // own the bytes — caller's memory may be pooled/recycled
    _bodies[storageKey] = copy;

    var hashBytes = SHA256.HashData(copy);
    var contentHash = "sha256-" + Convert.ToHexString(hashBytes);

    var claim = new MessageBodyClaim(
      ProviderName: ProviderName,
      StorageKey: storageKey,
      Size: copy.Length,
      ContentHash: contentHash,
      ContentType: contentType,
      UploadedAt: DateTimeOffset.UtcNow);

    return Task.FromResult(claim);
  }

  /// <inheritdoc />
  public Task<ReadOnlyMemory<byte>> DownloadAsync(
      MessageBodyClaim claim,
      MessageBodyDownloadOptions? options = null,
      CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(claim);

    if (!_bodies.TryGetValue(claim.StorageKey, out var body)) {
      throw new InvalidOperationException(
        $"In-memory body not found for storage key '{claim.StorageKey}'. " +
        "This indicates either a restart between upload and download, or a producer/consumer running in different processes — InMemoryMessageBodyStore is single-process only.");
    }

    if (options?.MaxBytes is long max && body.Length > max) {
      throw new InvalidOperationException(
        $"Body size {body.Length} exceeds requested MaxBytes cap {max}.");
    }

    return Task.FromResult<ReadOnlyMemory<byte>>(body);
  }

  /// <inheritdoc />
  public Task DeleteAsync(
      MessageBodyClaim claim,
      MessageBodyDeleteOptions? options = null,
      CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(claim);

    var removed = _bodies.TryRemove(claim.StorageKey, out _);
    var ignoreMissing = options?.IgnoreMissing ?? new MessageBodyDeleteOptions().IgnoreMissing;
    if (!removed && !ignoreMissing) {
      throw new InvalidOperationException(
        $"In-memory body not found for storage key '{claim.StorageKey}' and IgnoreMissing=false.");
    }
    return Task.CompletedTask;
  }
}
