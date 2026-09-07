#pragma warning disable CA1707

using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Offloads.AzureBlob;

namespace Whizbang.Offloads.AzureBlob.Tests;

/// <summary>
/// Coverage-round-23 target: <see cref="AzureBlobMessageBodyStore.CheckConnectivityAsync"/>, the
/// health-probe member no existing suite exercises. Azure SDK behavior stays mocked, per the
/// sibling validation/upload-guard suites in this project -- the difference here is the fake
/// container client must actually SUCCEED (return true) instead of throwing, so the probe's whole
/// success path runs end to end with zero Azure connectivity.
/// </summary>
/// <docs>fundamentals/offloads/providers/azure-blob</docs>
/// <tests>src/Whizbang.Offloads.AzureBlob/AzureBlobMessageBodyStore.cs</tests>
public class AzureBlobMessageBodyStoreCoverageTests {

  // ConnectivityHealthSource reads this to decide whether the offload dependency is up. If a
  // reachable container ever reported unhealthy here -- or the round trip to the container client
  // silently stopped happening -- a healthy deployment would be held out of rotation for a
  // dependency that is actually fine.
  [Test]
  public async Task CheckConnectivityAsync_ContainerReachable_ReturnsTrueAsync() {
    var containerClient = new _reachableContainerClient();
    var options = new AzureBlobOffloadOptions {
      ConnectionString = "UseDevelopmentStorage=true",
      ContainerName = "connectivity-tests",
    };
    var store = new AzureBlobMessageBodyStore("connectivity-tests", options, containerClient);

    var reachable = await store.CheckConnectivityAsync();

    await Assert.That(reachable).IsTrue()
      .Because("a successful ExistsAsync round trip means the dependency is reachable, whether or not the container itself exists yet");
    await Assert.That(containerClient.ExistsAsyncCallCount).IsEqualTo(1)
      .Because("the probe must actually call through to the container client, not short-circuit to true");
  }

  // ===== Test doubles =====

  /// <summary>
  /// Mockable BlobContainerClient (protected parameterless constructor + virtual members) that
  /// answers <c>ExistsAsync</c> without any network call. Unlike the sibling suites' bare
  /// <c>FakeBlobContainerClient</c> (which must throw on any real member call to prove the guard
  /// under test fires first), this one must actually succeed so
  /// <see cref="AzureBlobMessageBodyStore.CheckConnectivityAsync"/>'s success path runs.
  /// </summary>
  private sealed class _reachableContainerClient : BlobContainerClient {
    private static readonly Response _rawResponse = new _fakeResponse();

    public int ExistsAsyncCallCount { get; private set; }

    public override Task<Response<bool>> ExistsAsync(CancellationToken cancellationToken = default) {
      ExistsAsyncCallCount++;
      return Task.FromResult(Response.FromValue(true, _rawResponse));
    }
  }

  /// <summary>Minimal Azure.Response for wrapping canned values -- never inspected by the store.</summary>
  private sealed class _fakeResponse : Response {
    public override int Status => 200;
    public override string ReasonPhrase => "OK";
    public override Stream? ContentStream { get; set; }
    public override string ClientRequestId { get; set; } = "unit-test";

    public override void Dispose() {
      // No resources to release.
    }

    protected override bool ContainsHeader(string name) => false;

    protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];

    protected override bool TryGetHeader(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) {
      value = null;
      return false;
    }

    protected override bool TryGetHeaderValues(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IEnumerable<string>? values) {
      values = null;
      return false;
    }
  }
}
