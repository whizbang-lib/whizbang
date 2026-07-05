#pragma warning disable CA1707

using Azure.Storage.Blobs;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Offloads.AzureBlob;

namespace Whizbang.Offloads.AzureBlob.Tests;

/// <summary>
/// Locks the <see cref="AzureBlobMessageBodyStore.UploadAsync"/> content-type
/// argument guard, which fires before the lazy container ensure — so these
/// tests run with zero Azure connectivity (the fake container client would
/// throw on any actual member call). Azure SDK upload behavior lives in the
/// Azurite integration tests (AzureBlobStoreUploadTests).
/// </summary>
/// <docs>fundamentals/offloads/providers/azure-blob</docs>
public class AzureBlobMessageBodyStoreUploadGuardTests {

  private static readonly byte[] _anyBody = [1, 2, 3];

  [Test]
  public async Task UploadAsync_NullContentType_ThrowsArgumentNullExceptionAsync() {
    var store = _buildStoreWithFakeContainer();

    var ex = await Assert.That(async () => await store.UploadAsync(_anyBody, null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(ex!.ParamName).IsEqualTo("contentType")
      .Because("The guard names the offending parameter and fires before the container ensure — no network call for a null content type.");
  }

  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task UploadAsync_EmptyOrWhitespaceContentType_ThrowsArgumentExceptionAsync(string contentType) {
    var store = _buildStoreWithFakeContainer();

    var ex = await Assert.That(async () => await store.UploadAsync(_anyBody, contentType))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.ParamName).IsEqualTo("contentType")
      .Because("A blank content type would produce a claim the receiver cannot deserialize from — it must be rejected before any bytes leave the process.");
  }

  private static AzureBlobMessageBodyStore _buildStoreWithFakeContainer() {
    // Internal test ctor: hand the store a mock-friendly container client so
    // the argument guard can be exercised with zero Azure connectivity.
    var options = new AzureBlobOffloadOptions {
      ConnectionString = "UseDevelopmentStorage=true",
      ContainerName = "upload-guard-tests",
    };
    return new AzureBlobMessageBodyStore("upload-guard-tests", options, new FakeBlobContainerClient());
  }

  /// <summary>
  /// Uses the Azure SDK's protected mocking ctor. Any actual member call
  /// would throw — the guard tests must fail before touching the client.
  /// </summary>
  private sealed class FakeBlobContainerClient : BlobContainerClient;
}
