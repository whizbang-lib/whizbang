// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// Where an archive's objects are kept (#2503): a storage provider that puts, gets, deletes and lists objects by their
/// path, and names the location it reaches. It knows no database. The database driver's archive coordinator
/// (<see cref="IArchiveObjectCoordinator"/>) owns the transaction: it locks the stream, stages its events, writes the
/// objects through this store, records the manifest and the location, and commits.
/// </summary>
/// <remarks>
/// <para>
/// The contract every provider keeps:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="PutAsync"/> creates an object and never replaces one: when an object already holds the
/// path it returns <see langword="false"/> and leaves it as it is. The coordinator names objects by their content hash, so
/// a retried write finds its own bytes; it reads them back and compares.</description></item>
/// <item><description><see cref="GetAsync"/> returns an object's bytes exactly as they were put.</description></item>
/// <item><description><see cref="DeleteAsync"/> is the provider's one delete and counts an object already gone as
/// deleted. On storage with soft delete, it is the ordinary delete the retention period covers.</description></item>
/// <item><description><see cref="ListAsync"/> lists every object with its creation time; only the orphan sweep lists, and
/// only with a deletion delay set.</description></item>
/// <item><description>A storage failure is thrown as <see cref="ArchiveObjectStoreException"/>, with the provider's own
/// error as its inner exception, so the coordinator can tell a failed request from a fault in its own
/// logic.</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/events/archive-store#object-stores-and-the-coordinator</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ObjectArchiveCoordinatorTests.cs</tests>
/// <tests>tests/Whizbang.Storage.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs</tests>
public interface IArchiveObjectStore {
  /// <summary>
  /// The location this store reads and writes, from its configuration: the provider name, the account endpoint (never a
  /// key, a signature or a connection string), the logical connection name and the container. The coordinator records it
  /// with every object and checks every record against it (#2137).
  /// </summary>
  ArchiveLocation Location { get; }

  /// <summary>
  /// How the coordinator writes through this store: the compression quality, the chunk size, whether locations are
  /// checked, and the deletion delay. The provider's configuration carries them, so one section configures an archive.
  /// </summary>
  ArchiveObjectStoreSettings Settings { get; }

  /// <summary>Makes the location ready to write: creates the container when it does not exist. Safe to call again.</summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes when the location exists.</returns>
  /// <exception cref="ArchiveObjectStoreException">Storage refused or failed the request.</exception>
  Task PrepareAsync(CancellationToken cancellationToken = default);

  /// <summary>Creates the object <paramref name="path"/> holding <paramref name="content"/>, only when no object holds the path.</summary>
  /// <param name="path">The object's path from the location's root.</param>
  /// <param name="content">The bytes to store.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns><see langword="true"/> when the object was created; <see langword="false"/> when an object already held the
  /// path, which is left as it was.</returns>
  /// <exception cref="ArchiveObjectStoreException">Storage refused or failed the request.</exception>
  Task<bool> PutAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

  /// <summary>Reads the object <paramref name="path"/>.</summary>
  /// <param name="path">The object's path from the location's root.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The object's bytes, exactly as they were put.</returns>
  /// <exception cref="ArchiveObjectStoreException">The object does not exist, or storage refused or failed the request.</exception>
  Task<byte[]> GetAsync(string path, CancellationToken cancellationToken = default);

  /// <summary>Deletes the object <paramref name="path"/>; an object already gone counts as deleted.</summary>
  /// <param name="path">The object's path from the location's root.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns><see langword="true"/> when an object was there to delete.</returns>
  /// <exception cref="ArchiveObjectStoreException">Storage refused or failed the request.</exception>
  Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default);

  /// <summary>Lists every object in the location, with the time storage created it.</summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The objects, in the order storage returns them.</returns>
  /// <exception cref="ArchiveObjectStoreException">Storage refused or failed the request.</exception>
  IAsyncEnumerable<ArchiveObjectInfo> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>One object an <see cref="IArchiveObjectStore"/> lists.</summary>
/// <param name="Path">The object's path from the location's root.</param>
/// <param name="CreatedOn">When storage created it, or <see langword="null"/> when storage does not say; the orphan sweep
/// never deletes an object whose age it cannot show.</param>
/// <docs>fundamentals/events/archive-store#object-stores-and-the-coordinator</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ObjectArchiveCoordinatorTests.cs</tests>
public readonly record struct ArchiveObjectInfo(string Path, DateTimeOffset? CreatedOn);
