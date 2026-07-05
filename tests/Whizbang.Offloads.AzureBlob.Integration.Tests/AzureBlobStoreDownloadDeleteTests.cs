#pragma warning disable CA1707

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;
using Whizbang.Offloads.AzureBlob;

namespace Whizbang.Offloads.AzureBlob.Integration.Tests;

/// <summary>
/// Branch-level tests for <see cref="AzureBlobMessageBodyStore.DownloadAsync"/>
/// and <see cref="AzureBlobMessageBodyStore.DeleteAsync"/> against Azurite.
/// Complements <see cref="AzureBlobStoreRoundTripTests"/> (happy-path round
/// trip, provider-cap refusal, 404 download, strict-mode delete) by locking
/// the remaining branches: per-call MaxBytes overrides, delete of a leased
/// blob (the RequestFailedException-swallow branch), explicit IgnoreMissing,
/// and the not-found error detail the receive side relies on for diagnosis.
/// </summary>
/// <docs>fundamentals/offloads/providers/azure-blob#emulator</docs>
public class AzureBlobStoreDownloadDeleteTests {

  [Test]
  public async Task DownloadAsync_PerCallMaxBytesBelowClaimSize_RefusesDownloadAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);

    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("azurite", opts => {
      opts.ConnectionString = connectionString;
      opts.ContainerName = $"percallcap-{Guid.NewGuid():N}";
      // No provider-level cap — the refusal below must come from the per-call knob.
    });
    await using var provider = services.BuildServiceProvider();
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var claim = await store.UploadAsync(new byte[2_000], "application/octet-stream");

    var ex = await Assert.That(async () =>
      await store.DownloadAsync(claim, new MessageBodyDownloadOptions { MaxBytes = 1_000 }))
      .ThrowsExactly<InvalidOperationException>();

    await Assert.That(ex!.Message).Contains(claim.StorageKey)
      .Because("The refusal names the offending storage key so operators can correlate the claim with the blob.");
    await Assert.That(ex.Message).Contains("MaxBytes")
      .Because("Per-call MaxBytes (MessageBodyDownloadOptions.MaxBytes) must be honored even when the provider has no configured cap — the DLQ rehydrate path tightens per call.");
  }

  [Test]
  public async Task DownloadAsync_PerCallMaxBytesOverridesProviderCap_AllowsDownloadAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);

    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("azurite", opts => {
      opts.ConnectionString = connectionString;
      opts.ContainerName = $"capoverride-{Guid.NewGuid():N}";
      opts.MaxDownloadBytes = 1_000;   // provider cap would refuse a 2 000-byte body
    });
    await using var provider = services.BuildServiceProvider();
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var body = new byte[2_000];
    Random.Shared.NextBytes(body);
    var claim = await store.UploadAsync(body, "application/octet-stream");

    var fetched = await store.DownloadAsync(claim, new MessageBodyDownloadOptions { MaxBytes = 10_000 });

    await Assert.That(fetched.ToArray()).IsEquivalentTo(body)
      .Because("A non-null per-call MaxBytes takes precedence over the provider's MaxDownloadBytes (options?.MaxBytes ?? provider cap) — and a satisfied cap must not disturb byte fidelity.");
  }

  [Test]
  public async Task DownloadAsync_AfterDelete_ThrowsWithStorageKeyAndInnerNotFoundAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);

    var containerName = $"ttlgone-{Guid.NewGuid():N}";
    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("azurite", opts => {
      opts.ConnectionString = connectionString;
      opts.ContainerName = containerName;
    });
    await using var provider = services.BuildServiceProvider();
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    // Simulate the TTL race the error message documents: body uploaded,
    // removed (here by explicit delete), receiver downloads afterwards.
    var claim = await store.UploadAsync("body"u8.ToArray(), "application/octet-stream");
    await store.DeleteAsync(claim);

    var ex = await Assert.That(async () => await store.DownloadAsync(claim))
      .ThrowsExactly<InvalidOperationException>();

    await Assert.That(ex!.Message).Contains(claim.StorageKey)
      .Because("The receive-side operator needs the storage key to check lifecycle rules / TTL for the missing body.");
    await Assert.That(ex.Message).Contains(containerName)
      .Because("The container name pinpoints which provider binding lost the body when multiple providers are registered.");
    await Assert.That(ex.InnerException).IsTypeOf<RequestFailedException>()
      .Because("The original Azure 404 is preserved as InnerException for full diagnostics.");
    await Assert.That(((RequestFailedException)ex.InnerException!).Status).IsEqualTo(404);
  }

  [Test]
  public async Task DeleteAsync_ExplicitIgnoreMissingTrue_MissingBlobDoesNotThrowAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);

    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("azurite", opts => {
      opts.ConnectionString = connectionString;
      opts.ContainerName = $"explicitignore-{Guid.NewGuid():N}";
    });
    await using var provider = services.BuildServiceProvider();
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    // Upload a seed so the container exists, then target a key that was never uploaded.
    var realClaim = await store.UploadAsync("seed"u8.ToArray(), "application/octet-stream");
    var bogusClaim = realClaim with {
      StorageKey = $"never-uploaded/{Guid.NewGuid():N}.bin"
    };

    // Explicit (non-null options) IgnoreMissing=true — the branch where the
    // caller passes options rather than relying on the null-options default.
    await store.DeleteAsync(bogusClaim, new MessageBodyDeleteOptions { IgnoreMissing = true });

    // Prove the blob truly is missing (so the call above exercised the
    // ignore branch, not an accidental successful delete): strict mode on
    // the same claim must raise.
    await Assert.That(async () =>
      await store.DeleteAsync(bogusClaim, new MessageBodyDeleteOptions { IgnoreMissing = false }))
      .ThrowsExactly<InvalidOperationException>()
      .Because("Strict-mode delete of the same missing key throws — confirming the explicit IgnoreMissing=true call was the idempotent-tolerance branch.");
  }

  [Test]
  public async Task DeleteAsync_LeasedBlob_DefaultIgnoreMissing_SwallowsFailureAndLeavesBlobAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);

    var containerName = $"leasedlenient-{Guid.NewGuid():N}";
    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("azurite", opts => {
      opts.ConnectionString = connectionString;
      opts.ContainerName = containerName;
    });
    await using var provider = services.BuildServiceProvider();
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var body = "leased body"u8.ToArray();
    var claim = await store.UploadAsync(body, "application/octet-stream");

    // Acquire an infinite lease directly via the SDK so the provider's
    // unconditioned delete fails with 412 (lease id missing) — the only
    // RequestFailedException DeleteIfExistsAsync does NOT swallow itself.
    var leaseClient = new BlobServiceClient(connectionString)
      .GetBlobContainerClient(containerName)
      .GetBlobClient(claim.StorageKey)
      .GetBlobLeaseClient();
    await leaseClient.AcquireAsync(BlobLeaseClient.InfiniteLeaseDuration);
    try {
      // Default options → IgnoreMissing=true → the catch-when(ignoreMissing)
      // branch swallows the request failure and returns normally.
      await store.DeleteAsync(claim);

      var fetched = await store.DownloadAsync(claim);
      await Assert.That(fetched.ToArray()).IsEquivalentTo(body)
        .Because("The tolerated delete failure must leave the leased blob fully intact — swallow means 'someone else owns cleanup', never partial deletion.");
    } finally {
      await leaseClient.ReleaseAsync();
    }
  }

  [Test]
  public async Task DeleteAsync_LeasedBlob_StrictMode_PropagatesRequestFailedExceptionAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);

    var containerName = $"leasedstrict-{Guid.NewGuid():N}";
    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("azurite", opts => {
      opts.ConnectionString = connectionString;
      opts.ContainerName = containerName;
    });
    await using var provider = services.BuildServiceProvider();
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var claim = await store.UploadAsync("leased body"u8.ToArray(), "application/octet-stream");

    var leaseClient = new BlobServiceClient(connectionString)
      .GetBlobContainerClient(containerName)
      .GetBlobClient(claim.StorageKey)
      .GetBlobLeaseClient();
    await leaseClient.AcquireAsync(BlobLeaseClient.InfiniteLeaseDuration);
    try {
      var ex = await Assert.That(async () =>
        await store.DeleteAsync(claim, new MessageBodyDeleteOptions { IgnoreMissing = false }))
        .ThrowsExactly<RequestFailedException>();

      await Assert.That(ex!.Status).IsEqualTo(412)
        .Because("Strict mode (IgnoreMissing=false) must propagate the raw Azure failure — forensic tooling needs the real 412 lease conflict, not a swallowed no-op.");
    } finally {
      await leaseClient.ReleaseAsync();
    }
  }
}
