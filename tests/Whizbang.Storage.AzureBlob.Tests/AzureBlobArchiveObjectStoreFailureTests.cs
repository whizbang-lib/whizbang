// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;

namespace Whizbang.Storage.AzureBlob.Tests;

/// <summary>
/// A request about the whole location that storage refuses (#2503): preparing the container and listing its objects.
/// Each is reported as an <see cref="ArchiveObjectStoreException"/> naming the operation and the location, with the
/// service's own error inside, so the coordinator can tell a storage failure from a defect. Storage is a real client
/// over an in-process handler that refuses every request; nothing leaves the process.
/// </summary>
/// <docs>fundamentals/events/archive-store#object-stores-and-the-coordinator</docs>
public class AzureBlobArchiveObjectStoreFailureTests {
  private static (AzureBlobArchiveObjectStore Store, RefusingHandler Handler) _refusedStore() {
    var handler = new RefusingHandler();
    var options = new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
    options.Retry.MaxRetries = 0;
    var container = new BlobContainerClient(new Uri("https://account.blob.core.windows.net/archive"), options);
    return (new AzureBlobArchiveObjectStore(container, new AzureBlobArchiveOptions { ContainerName = "archive" }), handler);
  }

  [Test]
  public async Task Prepare_RefusedByStorage_IsAStoreException_NamingTheLocation_WithTheServicesErrorInsideAsync() {
    var (store, handler) = _refusedStore();

    var thrown = await Assert.ThrowsAsync<ArchiveObjectStoreException>(() => store.PrepareAsync());

    await Assert.That(handler.Requests).IsEqualTo(1).Because("the container was asked for, and refused");
    await Assert.That(thrown!.Operation).IsEqualTo("prepare");
    await Assert.That(thrown.Path).IsNull().Because("preparing is a request about the whole location");
    await Assert.That(thrown.Location).IsEqualTo(store.Location);
    await Assert.That(thrown.InnerException).IsTypeOf<RequestFailedException>();
    await Assert.That(((RequestFailedException)thrown.InnerException!).Status).IsEqualTo(403);
  }

  [Test]
  public async Task List_RefusedByStorage_IsAStoreException_NamingTheLocation_WithTheServicesErrorInsideAsync() {
    var (store, handler) = _refusedStore();

    var thrown = await Assert.ThrowsAsync<ArchiveObjectStoreException>(async () => {
      await foreach (var _ in store.ListAsync()) {
        // Nothing is listed: the first page is refused.
      }
    });

    await Assert.That(handler.Requests).IsEqualTo(1).Because("the first page was asked for, and refused");
    await Assert.That(thrown!.Operation).IsEqualTo("list");
    await Assert.That(thrown.Path).IsNull().Because("listing is a request about the whole location");
    await Assert.That(thrown.Location).IsEqualTo(store.Location);
    await Assert.That(((RequestFailedException)thrown.InnerException!).Status).IsEqualTo(403);
  }

  /// <summary>Answers every request with 403, as storage does to a client it does not authorize.</summary>
  private sealed class RefusingHandler : HttpMessageHandler {
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      Requests++;
      var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request };
      response.Headers.Add("x-ms-error-code", "AuthorizationFailure");
      return Task.FromResult(response);
    }
  }
}
