#pragma warning disable CA1707

using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;
using Whizbang.Offloads.AzureBlob;

namespace Whizbang.Offloads.AzureBlob.Integration.Tests;

/// <summary>
/// End-to-end round-trip tests for <see cref="AzureBlobMessageBodyStore"/>
/// against a real Azurite container (Testcontainers). Proves the provider
/// works against the Azurite emulator using the standard connection-string
/// pattern — same code path that runs against live Azure Blob Storage in
/// production.
/// </summary>
/// <remarks>
/// Per the Whizbang offloads contract, the same provider impl serves both
/// emulator and live without behavioral divergence. These tests lock that
/// invariant against Azurite specifically — the live path additionally
/// runs through the same code in production deployments.
/// </remarks>
/// <docs>fundamentals/offloads/providers/azure-blob#emulator</docs>
public class AzureBlobStoreRoundTripTests {

  [Test]
  public async Task UploadDownloadDelete_RoundTripsBodyAndHashAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);

    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("azurite", opts => {
      opts.ConnectionString = connectionString;
      opts.ContainerName = $"roundtrip-{Guid.NewGuid():N}";   // unique per test
    });
    await using var provider = services.BuildServiceProvider();
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var body = new byte[64 * 1024];
    Random.Shared.NextBytes(body);
    var expectedHash = "sha256-" + Convert.ToHexString(SHA256.HashData(body));

    // Upload — provider auto-creates the container on first call
    var claim = await store.UploadAsync(body, "application/octet-stream");

    await Assert.That(claim.ProviderName).IsEqualTo("azurite");
    await Assert.That(claim.Size).IsEqualTo(64L * 1024L);
    await Assert.That(claim.ContentHash).IsEqualTo(expectedHash)
      .Because("Hash MUST match across emulator and live — the receive-side rehydrate relies on this exact equality to detect storage corruption / MITM.");

    // Download — verify byte-for-byte fidelity
    var fetched = await store.DownloadAsync(claim);
    await Assert.That(fetched.ToArray()).IsEquivalentTo(body)
      .Because("Round-trip preserves every byte — claim-check pattern's whole point is the receiver gets exactly what the sender uploaded.");

    // Delete — provider returns successfully
    await store.DeleteAsync(claim);

    // Second delete is idempotent by default (IgnoreMissing = true)
    await store.DeleteAsync(claim);
  }

  [Test]
  public async Task DownloadAsync_NonexistentBlob_ThrowsInvalidOperationExceptionAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);

    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("azurite", opts => {
      opts.ConnectionString = connectionString;
      opts.ContainerName = $"notfound-{Guid.NewGuid():N}";
    });
    await using var provider = services.BuildServiceProvider();
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    // Upload something so the container gets created, then craft a claim
    // referencing a different key in the same container.
    var realClaim = await store.UploadAsync("seed"u8.ToArray(), "application/octet-stream");
    var bogusClaim = realClaim with {
      StorageKey = $"never-uploaded/{Guid.NewGuid():N}.bin",
      ContentHash = "sha256-DOESNTMATTER",
      Size = 100
    };

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await store.DownloadAsync(bogusClaim));
  }

  [Test]
  public async Task DeleteAsync_StrictMode_NonexistentBlobThrowsAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);

    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("azurite", opts => {
      opts.ConnectionString = connectionString;
      opts.ContainerName = $"strict-{Guid.NewGuid():N}";
    });
    await using var provider = services.BuildServiceProvider();
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var realClaim = await store.UploadAsync("seed"u8.ToArray(), "application/octet-stream");
    var bogusClaim = realClaim with {
      StorageKey = $"never-uploaded/{Guid.NewGuid():N}.bin"
    };

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await store.DeleteAsync(bogusClaim, new MessageBodyDeleteOptions { IgnoreMissing = false }));
  }

  [Test]
  public async Task DownloadAsync_OverMaxBytesCap_RefusesAsync() {
    var connectionString = await AzuriteFixture.EnsureStartedAsync(CancellationToken.None);

    var services = new ServiceCollection();
    services.AddWhizbangAzureBlobOffload("azurite", opts => {
      opts.ConnectionString = connectionString;
      opts.ContainerName = $"maxbytes-{Guid.NewGuid():N}";
      opts.MaxDownloadBytes = 1_000;
    });
    await using var provider = services.BuildServiceProvider();
    var store = provider.GetRequiredKeyedService<IMessageBodyStore>("azurite");

    var body = new byte[2_000];
    var claim = await store.UploadAsync(body, "application/octet-stream");

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await store.DownloadAsync(claim))
      .Because("Defensive MaxDownloadBytes cap refuses oversized claims pre-download — protects receivers from tampered claim tickets that would otherwise pull arbitrarily large blobs.");
  }
}
