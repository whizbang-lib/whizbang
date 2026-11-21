// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Whizbang.Core.Archival;

namespace Whizbang.Storage.AzureBlob;

/// <summary>
/// Archive objects in Azure Blob Storage (#2503): the <see cref="IArchiveObjectStore"/> the database driver's archive
/// coordinator writes through. It moves objects only; the manifest, the locations and the transaction are the driver's.
/// </summary>
/// <remarks>
/// <para>
/// Every put is create-only (<c>If-None-Match: *</c>), so an object is written once and never replaced (#2367); a conflict
/// is reported as <see langword="false"/>, never as an error. Every put sends an MD5 the service checks, so an object is
/// stored exactly as it was sent, and is written at the configured access tier (#1897), set at creation and never
/// changed. The one delete is the ordinary blob delete: never a permanent delete, never snapshots or versions, so with
/// soft delete on a deleted object stays recoverable for the account's retention period.
/// </para>
/// <para>
/// Nothing stored changed when this provider was split from the coordinator: object names, the location's provider
/// (<see cref="PROVIDER_NAME"/>) and the locator are the ones the Azure Blob archive store wrote.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/archive-store#the-azure-blob-store</docs>
/// <tests>tests/Whizbang.Storage.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs</tests>
/// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobArchiveLocationTests.cs</tests>
public sealed class AzureBlobArchiveObjectStore : IArchiveObjectStore {
  /// <summary>The provider name every location and locator of this store records.</summary>
  public const string PROVIDER_NAME = "azure-blob";

  private readonly BlobContainerClient _container;
  private readonly AccessTier? _accessTier;

  /// <summary>Creates the store over a container.</summary>
  /// <param name="container">The container the objects are written to.</param>
  /// <param name="options">The logical connection name, the access tier, and the settings the coordinator writes with.</param>
  /// <exception cref="ArgumentOutOfRangeException">The access tier is not Hot, Cool or Cold, or the settings are ones the
  /// coordinator cannot run with.</exception>
  /// <exception cref="ArgumentException">The connection name is empty or contains <c>;</c> or <c>=</c>.</exception>
  /// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobArchiveLocationTests.cs</tests>
  /// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs:Store_RefusesATierItCannotReadBackFromAsync</tests>
  public AzureBlobArchiveObjectStore(BlobContainerClient container, AzureBlobArchiveOptions options) {
    ArgumentNullException.ThrowIfNull(container);
    ArgumentNullException.ThrowIfNull(options);
    if (AzureBlobArchiveOptions.RefusalFor(options.AccessTier) is { } refusal) {
      throw new ArgumentOutOfRangeException(nameof(options), options.AccessTier, refusal);
    }
    var settings = options.ToSettings();
    if (settings.Refusal() is { } settingsRefusal) {
      throw new ArgumentOutOfRangeException(nameof(options), settingsRefusal);
    }
    if (!AzureBlobArchiveOptions.IsLogicalName(options.ConnectionName)) {
      // Never echoed: a value that looks like a connection string may be one.
      throw new ArgumentException("ConnectionName is a logical name: not empty, and without ';' or '='.", nameof(options));
    }
    _container = container;
    _accessTier = options.AccessTier;
    Settings = settings;
    Location = new ArchiveLocation(PROVIDER_NAME, EndpointOf(container), options.ConnectionName, container.Name);
  }

  /// <inheritdoc />
  /// <remarks>The account endpoint of the container client (never a key, a signature or a connection string), the
  /// logical connection name and the container.</remarks>
  public ArchiveLocation Location { get; }

  /// <inheritdoc />
  public ArchiveObjectStoreSettings Settings { get; }

  /// <summary>
  /// The storage account endpoint a container client reaches: scheme, host, port and any account path segment (an
  /// emulator's), without the container, the query (where a signature sits) or user information.
  /// </summary>
  internal static string EndpointOf(BlobContainerClient container) {
    var uri = container.Uri;
    var path = uri.AbsolutePath.TrimEnd('/');
    return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + path[..path.LastIndexOf('/')];
  }

  /// <inheritdoc />
  public async Task PrepareAsync(CancellationToken cancellationToken = default) {
    try {
      _ = await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    } catch (RequestFailedException ex) {
      throw new ArchiveObjectStoreException("prepare", path: null, Location, ex);
    }
  }

  /// <inheritdoc />
  public async Task<bool> PutAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    var upload = new BlobUploadOptions {
      // The service checks the request's bytes against this hash, so an object is stored exactly as it was sent.
      TransferValidation = new UploadTransferValidationOptions { ChecksumAlgorithm = StorageChecksumAlgorithm.MD5 },
      // #1897: the configured tier, or none (the account's default). Set here, at creation, and never changed after.
      AccessTier = _accessTier,
      // #2367: create only; storage refuses the write when any object already holds the name.
      Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
    };
    try {
      _ = await _container.GetBlobClient(path).UploadAsync(BinaryData.FromBytes(content), upload, cancellationToken).ConfigureAwait(false);
      return true;
    } catch (RequestFailedException ex) when (ex.Status == (int)System.Net.HttpStatusCode.Conflict) {
      return false;
    } catch (RequestFailedException ex) {
      throw new ArchiveObjectStoreException("put", path, Location, ex);
    }
  }

  /// <inheritdoc />
  public async Task<byte[]> GetAsync(string path, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    try {
      return (await _container.GetBlobClient(path).DownloadContentAsync(cancellationToken).ConfigureAwait(false)).Value.Content.ToArray();
    } catch (RequestFailedException ex) {
      throw new ArchiveObjectStoreException("get", path, Location, ex);
    }
  }

  /// <inheritdoc />
  public async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    try {
      return (await _container.GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
    } catch (RequestFailedException ex) {
      throw new ArchiveObjectStoreException("delete", path, Location, ex);
    }
  }

  /// <inheritdoc />
  public async IAsyncEnumerable<ArchiveObjectInfo> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default) {
    await using var pages = _container.GetBlobsAsync(cancellationToken: cancellationToken).AsPages().GetAsyncEnumerator(cancellationToken);
    while (true) {
      Page<BlobItem> page;
      try {
        if (!await pages.MoveNextAsync().ConfigureAwait(false)) {
          yield break;
        }
        page = pages.Current;
      } catch (RequestFailedException ex) {
        throw new ArchiveObjectStoreException("list", path: null, Location, ex);
      }
      foreach (var item in page.Values) {
        yield return new ArchiveObjectInfo(item.Name, item.Properties.CreatedOn);
      }
    }
  }
}
