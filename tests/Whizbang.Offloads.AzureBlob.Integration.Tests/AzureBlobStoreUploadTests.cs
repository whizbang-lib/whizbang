#pragma warning disable CA1707

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;
using Whizbang.Offloads.AzureBlob;

namespace Whizbang.Offloads.AzureBlob.Integration.Tests;

/// <summary>
/// Branch-level tests for <see cref="AzureBlobMessageBodyStore.UploadAsync"/>
/// against Azurite. Complements <see cref="AzureBlobStoreRoundTripTests"/>
/// (which locks the basic upload→download→delete cycle) by verifying the
/// upload internals directly against the storage service: claim descriptor
/// fields, storage-key shape, blob content/headers/metadata as the service
/// stored them, caller-metadata merging, the lazy container-creation paths
/// (fresh create, pre-existing container, per-instance short-circuit), and
/// the ensure-retry behavior after a failed or cancelled first upload.
/// </summary>
/// <docs>fundamentals/offloads/providers/azure-blob#emulator</docs>
public class AzureBlobStoreUploadTests {

  /// <summary>Storage keys are date-partitioned GUID blob names: yyyy/MM/dd/&lt;32 hex&gt;.bin.</summary>
  private static readonly Regex _storageKeyPattern = new(
    @"^\d{4}/\d{2}/\d{2}/[0-9a-f]{32}\.bin$", RegexOptions.None, TimeSpan.FromSeconds(1));

  private static ServiceProvider _buildProvider(string connectionString, string containerName, Action<AzureBlobOffloadOptions>? extra = null) {
    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("azurite", opts => {
      opts.ConnectionString = connectionString;
      opts.ContainerName = containerName;
      extra?.Invoke(opts);
    });
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task UploadAsync_HappyPath_ClaimDescribesBlobAndServiceStoresExactBytesAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);
    var containerName = $"uploadhappy-{Guid.NewGuid():N}";
    await using var provider = _buildProvider(connectionString, containerName);
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var body = new byte[48 * 1024];
    Random.Shared.NextBytes(body);
    var expectedHash = "sha256-" + Convert.ToHexString(SHA256.HashData(body));

    var before = DateTimeOffset.UtcNow;
    var claim = await store.UploadAsync(body, "application/json");
    var after = DateTimeOffset.UtcNow;

    // Claim descriptor fields — the receiver's entire view of the upload.
    await Assert.That(claim.ProviderName).IsEqualTo("azurite")
      .Because("The receiver resolves the matching store by ProviderName; the claim must carry the provider key it was uploaded through.");
    await Assert.That(claim.Size).IsEqualTo(48L * 1024L);
    await Assert.That(claim.ContentHash).IsEqualTo(expectedHash);
    await Assert.That(claim.ContentType).IsEqualTo("application/json")
      .Because("The receiver deserializes the downloaded body using the claim's ContentType — it must echo what the caller declared.");
    await Assert.That(_storageKeyPattern.IsMatch(claim.StorageKey)).IsTrue()
      .Because($"Storage keys are date-partitioned GUID blob names (yyyy/MM/dd/<guid>.bin) so lifecycle rules can prune by prefix; got '{claim.StorageKey}'.");
    await Assert.That(claim.UploadedAt >= before && claim.UploadedAt <= after).IsTrue()
      .Because("UploadedAt is stamped at upload completion — it must fall inside the call window, not be a default or cached value.");

    // Verify against the service directly (not through the store) so a
    // download-side bug can't mask an upload-side one.
    var blobClient = new BlobServiceClient(connectionString)
      .GetBlobContainerClient(containerName)
      .GetBlobClient(claim.StorageKey);

    var content = await blobClient.DownloadContentAsync();
    await Assert.That(content.Value.Content.ToArray()).IsEquivalentTo(body)
      .Because("The service must hold exactly the bytes handed to UploadAsync — byte fidelity is the claim-check pattern's core guarantee.");

    var props = await blobClient.GetPropertiesAsync();
    await Assert.That(props.Value.ContentType).IsEqualTo("application/json")
      .Because("ContentType is persisted as the blob's HTTP header so out-of-band tooling (portal, az cli) sees the real MIME type.");
    await Assert.That(props.Value.Metadata.Count).IsEqualTo(1)
      .Because("With no caller metadata, the blob carries exactly one metadata entry: the integrity hash.");
    await Assert.That(props.Value.Metadata["whizbang_content_hash"]).IsEqualTo(expectedHash)
      .Because("The hash is stored ON the blob so operators can integrity-check a body without possessing the claim ticket.");
  }

  [Test]
  public async Task UploadAsync_WithCallerMetadata_PersistsCallerEntriesAlongsideHashAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);
    var containerName = $"uploadmeta-{Guid.NewGuid():N}";
    await using var provider = _buildProvider(connectionString, containerName);
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var body = "metadata body"u8.ToArray();
    var claim = await store.UploadAsync(body, "application/octet-stream", new MessageBodyUploadOptions {
      Metadata = new Dictionary<string, string> {
        ["correlation_id"] = "corr-12345",
        ["source_service"] = "orders-api",
      },
    });

    var props = await new BlobServiceClient(connectionString)
      .GetBlobContainerClient(containerName)
      .GetBlobClient(claim.StorageKey)
      .GetPropertiesAsync();

    await Assert.That(props.Value.Metadata.Count).IsEqualTo(3)
      .Because("Two caller entries plus the provider's own integrity hash — the provider merges, never replaces.");
    await Assert.That(props.Value.Metadata["correlation_id"]).IsEqualTo("corr-12345");
    await Assert.That(props.Value.Metadata["source_service"]).IsEqualTo("orders-api");
    await Assert.That(props.Value.Metadata["whizbang_content_hash"]).IsEqualTo(claim.ContentHash)
      .Because("The integrity hash MUST survive caller metadata being supplied — it is appended after the caller's dictionary is copied.");
  }

  [Test]
  public async Task UploadAsync_SameBodyTwice_ProducesDistinctKeysWithSameHashAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);
    var containerName = $"uploadtwice-{Guid.NewGuid():N}";
    await using var provider = _buildProvider(connectionString, containerName);
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var body = "identical body"u8.ToArray();
    var first = await store.UploadAsync(body, "application/octet-stream");
    // Second upload on the same instance also exercises the container-ensure
    // short-circuit (Interlocked flag already set — no second CreateIfNotExists).
    var second = await store.UploadAsync(body, "application/octet-stream");

    await Assert.That(second.StorageKey).IsNotEqualTo(first.StorageKey)
      .Because("Every upload gets a fresh GUID key — identical bodies never collide, so overwrite/conflict is structurally impossible.");
    await Assert.That(second.ContentHash).IsEqualTo(first.ContentHash)
      .Because("Identical bytes hash identically — keys diverge, integrity hashes must not.");

    // Both blobs exist independently: deleting one must not disturb the other.
    await store.DeleteAsync(first);
    var survivor = await store.DownloadAsync(second);
    await Assert.That(survivor.ToArray()).IsEquivalentTo(body)
      .Because("Distinct keys mean distinct blobs — fan-out senders can delete their own claim without racing a sibling's copy.");
  }

  [Test]
  public async Task UploadAsync_ContainerPreCreated_CreateIfNotExistsTolerantAndRoundTripsAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);
    var containerName = $"preexisting-{Guid.NewGuid():N}";

    // Create the container OUT-OF-BAND before the store's first upload, so
    // the lazy ensure hits the "already exists" arm of CreateIfNotExists
    // (the fresh-create arm is covered by every other test in this class).
    await new BlobServiceClient(connectionString)
      .GetBlobContainerClient(containerName)
      .CreateAsync();

    await using var provider = _buildProvider(connectionString, containerName);
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var body = "pre-existing container body"u8.ToArray();
    var claim = await store.UploadAsync(body, "application/octet-stream");

    var fetched = await store.DownloadAsync(claim);
    await Assert.That(fetched.ToArray()).IsEquivalentTo(body)
      .Because("A container provisioned by ops (IaC, portal) must be usable as-is — CreateIfNotExists tolerates the 409 and the upload proceeds normally.");
  }

  [Test]
  public async Task UploadAsync_EmptyBody_ZeroByteBlobRoundTripsWithEmptyHashAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);
    var containerName = $"emptybody-{Guid.NewGuid():N}";
    await using var provider = _buildProvider(connectionString, containerName);
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var expectedEmptyHash = "sha256-" + Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty));

    var claim = await store.UploadAsync(ReadOnlyMemory<byte>.Empty, "application/octet-stream");

    await Assert.That(claim.Size).IsEqualTo(0L);
    await Assert.That(claim.ContentHash).IsEqualTo(expectedEmptyHash)
      .Because("The empty body hashes to the well-known SHA-256 of zero bytes — the receive-side integrity check must pass for legitimately empty payloads.");

    var fetched = await store.DownloadAsync(claim);
    await Assert.That(fetched.Length).IsEqualTo(0)
      .Because("A zero-byte upload round-trips as zero bytes — no padding, no sentinel content.");
  }

  [Test]
  public async Task UploadAsync_DefaultAccessTierCool_BlobStoredAtConfiguredTierAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);
    var containerName = $"cooltier-{Guid.NewGuid():N}";
    await using var provider = _buildProvider(connectionString, containerName,
      opts => opts.DefaultAccessTier = AccessTier.Cool);
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var body = "cool tier body"u8.ToArray();
    var claim = await store.UploadAsync(body, "application/octet-stream");

    var props = await new BlobServiceClient(connectionString)
      .GetBlobContainerClient(containerName)
      .GetBlobClient(claim.StorageKey)
      .GetPropertiesAsync();
    await Assert.That(props.Value.AccessTier).IsEqualTo(AccessTier.Cool.ToString())
      .Because("DefaultAccessTier flows into BlobUploadOptions.AccessTier — cost-tiering configured on the provider must reach the storage service, not be silently dropped.");

    var fetched = await store.DownloadAsync(claim);
    await Assert.That(fetched.ToArray()).IsEquivalentTo(body)
      .Because("Tiering is a cost knob only — Cool-tier blobs download with full byte fidelity.");
  }

  [Test]
  public async Task UploadAsync_PreCancelledToken_ThrowsThenNextUploadRecoversAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);
    var containerName = $"cancelled-{Guid.NewGuid():N}";
    await using var provider = _buildProvider(connectionString, containerName);
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    var body = "recovery body"u8.ToArray();

    // First upload dies inside the lazy container-ensure (cancellation
    // surfaces as OperationCanceledException / TaskCanceledException).
    await Assert.That(async () =>
      await store.UploadAsync(body, "application/octet-stream", cancellationToken: cts.Token))
      .Throws<OperationCanceledException>();

    // The ensure guard must have been RESET by the failure — a fresh token
    // on the same instance retries container creation and succeeds. If the
    // flag stayed latched, this upload would 404 against a container that
    // was never created.
    var claim = await store.UploadAsync(body, "application/octet-stream");
    var fetched = await store.DownloadAsync(claim);
    await Assert.That(fetched.ToArray()).IsEquivalentTo(body)
      .Because("A cancelled first upload must not poison the store instance — the container-ensure flag resets so the next call re-attempts CreateIfNotExists.");
  }

  [Test]
  public async Task UploadAsync_InvalidContainerName_ThrowsRequestFailedOnEveryAttemptAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);
    // Uppercase + underscore violate Azure container naming; the ctor only
    // validates non-whitespace, so the failure surfaces at first upload.
    await using var provider = _buildProvider(connectionString, "Invalid_Container_NAME");
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var body = "never stored"u8.ToArray();

    var first = await Assert.That(async () =>
      await store.UploadAsync(body, "application/octet-stream"))
      .Throws<RequestFailedException>();
    await Assert.That(first!.Status).IsEqualTo(400)
      .Because("The service rejects the container name itself (400 InvalidResourceName) — the raw Azure failure propagates so operators see the real misconfiguration.");

    // Second attempt must fail the SAME way: the ensure flag was reset by
    // the failed create, so CreateIfNotExists runs again (400). Had the flag
    // stayed latched, this call would instead 404 at the blob upload against
    // the never-created container.
    var second = await Assert.That(async () =>
      await store.UploadAsync(body, "application/octet-stream"))
      .Throws<RequestFailedException>();
    await Assert.That(second!.Status).IsEqualTo(400)
      .Because("A 400 (not 404) on retry proves the ensure guard reset and container creation was re-attempted — misconfiguration stays loudly diagnosable on every call.");
  }
}
