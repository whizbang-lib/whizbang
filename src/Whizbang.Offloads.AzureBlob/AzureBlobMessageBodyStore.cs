using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Offloads;

namespace Whizbang.Offloads.AzureBlob;

/// <summary>
/// Production <see cref="IMessageBodyStore"/> backed by Azure Blob Storage.
/// Works transparently against both the Azurite emulator and live Azure
/// Blob — the connection string distinguishes them via the standard SDK
/// conventions (<c>UseDevelopmentStorage=true</c> / emulator endpoint vs.
/// account connection string).
/// </summary>
/// <remarks>
/// <para>
/// Uploads compute a SHA-256 hash so the receive-side rehydrate can
/// integrity-check downloads. Storage keys are blob names within the
/// configured container — opaque to callers; only the claim ticket matters.
/// </para>
/// <para>
/// The provider lazily creates the configured container on first upload
/// (CreateIfNotExists). For TTL-based cleanup, configure a blob lifecycle
/// rule on the storage account; <see cref="DeleteAsync"/> defaults to
/// idempotent so fan-out double-delete is safe.
/// </para>
/// </remarks>
/// <docs>fundamentals/offloads/providers/azure-blob</docs>
public sealed class AzureBlobMessageBodyStore : IMessageBodyStore {
  private readonly AzureBlobOffloadOptions _options;
  private readonly ILogger<AzureBlobMessageBodyStore>? _logger;
  private readonly BlobContainerClient _containerClient;
  private int _containerEnsured;

  /// <summary>
  /// Builds the store from options bound by name (the provider-name DI key).
  /// </summary>
  public AzureBlobMessageBodyStore(
      [ServiceKey] string providerName,
      IOptionsMonitor<AzureBlobOffloadOptions> options,
      ILogger<AzureBlobMessageBodyStore>? logger = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
    ArgumentNullException.ThrowIfNull(options);
    ProviderName = providerName;
    _options = options.Get(providerName);
    if (string.IsNullOrWhiteSpace(_options.ConnectionString)) {
      // Fall back to the unnamed default binding for tests / single-provider setups.
      _options = options.CurrentValue;
    }
    _logger = logger;

    if (string.IsNullOrWhiteSpace(_options.ConnectionString)) {
      throw new InvalidOperationException(
        $"AzureBlobOffloadOptions.ConnectionString is required for provider '{providerName}'. " +
        "Call services.Configure<AzureBlobOffloadOptions>(\"" + providerName + "\", opts => opts.ConnectionString = ...).");
    }
    if (string.IsNullOrWhiteSpace(_options.ContainerName)) {
      throw new InvalidOperationException(
        $"AzureBlobOffloadOptions.ContainerName is required for provider '{providerName}'.");
    }

    var serviceClient = new BlobServiceClient(_options.ConnectionString);
    _containerClient = serviceClient.GetBlobContainerClient(_options.ContainerName);
  }

  /// <summary>Test-only ctor that takes a pre-built container client (so unit tests can hand-roll a mock).</summary>
  internal AzureBlobMessageBodyStore(
      string providerName,
      AzureBlobOffloadOptions options,
      BlobContainerClient containerClient,
      ILogger<AzureBlobMessageBodyStore>? logger = null) {
    ProviderName = providerName;
    _options = options;
    _containerClient = containerClient;
    _logger = logger;
  }

  /// <inheritdoc />
  public string ProviderName { get; }

  /// <inheritdoc />
  public async Task<MessageBodyClaim> UploadAsync(
      ReadOnlyMemory<byte> body, string contentType,
      MessageBodyUploadOptions? options = null,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
    await _ensureContainerAsync(cancellationToken);

    var storageKey = $"{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}.bin";
    var blobClient = _containerClient.GetBlobClient(storageKey);

    var hash = "sha256-" + Convert.ToHexString(SHA256.HashData(body.Span));

    var uploadOptions = new BlobUploadOptions {
      HttpHeaders = new BlobHttpHeaders {
        ContentType = contentType,
      },
      AccessTier = _options.DefaultAccessTier,
    };

    if (options?.Metadata is { Count: > 0 } metaIn) {
      uploadOptions.Metadata = new Dictionary<string, string>(metaIn);
    } else {
      uploadOptions.Metadata = new Dictionary<string, string>();
    }
    uploadOptions.Metadata["whizbang_content_hash"] = hash;

    using var stream = _toReadOnlyStream(body);
    await blobClient.UploadAsync(stream, uploadOptions, cancellationToken);

    return new MessageBodyClaim(
      ProviderName: ProviderName,
      StorageKey: storageKey,
      Size: body.Length,
      ContentHash: hash,
      ContentType: contentType,
      UploadedAt: DateTimeOffset.UtcNow);
  }

  /// <inheritdoc />
  public async Task<ReadOnlyMemory<byte>> DownloadAsync(
      MessageBodyClaim claim, MessageBodyDownloadOptions? options = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(claim);

    var cap = options?.MaxBytes ?? _options.MaxDownloadBytes;
    if (cap is long max && claim.Size > max) {
      throw new InvalidOperationException(
        $"Claim '{claim.StorageKey}' size {claim.Size} exceeds MaxBytes cap {max}; refusing download.");
    }

    var blobClient = _containerClient.GetBlobClient(claim.StorageKey);
    try {
      var response = await blobClient.DownloadContentAsync(cancellationToken);
      return response.Value.Content.ToMemory();
    } catch (RequestFailedException ex) when (ex.Status == 404) {
      throw new InvalidOperationException(
        $"Body not found at '{claim.StorageKey}' in container '{_options.ContainerName}'. " +
        "TTL may have removed it before the receiver downloaded.", ex);
    }
  }

  /// <inheritdoc />
  public async Task DeleteAsync(
      MessageBodyClaim claim, MessageBodyDeleteOptions? options = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(claim);

    var blobClient = _containerClient.GetBlobClient(claim.StorageKey);
    var ignoreMissing = options?.IgnoreMissing ?? new MessageBodyDeleteOptions().IgnoreMissing;
    try {
      var deleted = await blobClient.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, conditions: null, cancellationToken);
      if (!deleted.Value && !ignoreMissing) {
        throw new InvalidOperationException(
          $"Blob '{claim.StorageKey}' not found in container '{_options.ContainerName}' and IgnoreMissing=false.");
      }
    } catch (RequestFailedException) when (ignoreMissing) {
      // Tolerated: TTL or another consumer already deleted. Log left to caller.
    }
  }

  private async Task _ensureContainerAsync(CancellationToken cancellationToken) {
    if (Interlocked.CompareExchange(ref _containerEnsured, 1, 0) != 0) {
      return;
    }
    try {
      await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    } catch (Exception) {
      // Reset so a subsequent call can retry; first-write success is the target.
      Interlocked.Exchange(ref _containerEnsured, 0);
      throw;
    }
  }

  private static MemoryStream _toReadOnlyStream(ReadOnlyMemory<byte> body) {
    return new MemoryStream(body.ToArray(), writable: false);
  }
}
