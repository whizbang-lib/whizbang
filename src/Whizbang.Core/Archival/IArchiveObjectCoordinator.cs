// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// The database driver's archive coordinator (#2503): the <see cref="IArchiveStore"/> that writes a stream's archive as
/// objects through an <see cref="IArchiveObjectStore"/> and keeps the manifest, the locations and the deletion queue in
/// the service's database, in the database's own transaction. A storage provider never touches the database; the
/// coordinator never knows which storage it writes to.
/// </summary>
/// <remarks>
/// The Postgres drivers register one whenever an object store is selected (<see cref="ArchiveObjectStoreSelection"/>).
/// A driver without a coordinator (SQLite) fails the start when a stream lifecycle policy archives, naming the missing
/// piece, rather than at the first archive.
/// </remarks>
/// <docs>fundamentals/events/archive-store#object-stores-and-the-coordinator</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ObjectArchiveCoordinatorTests.cs</tests>
public interface IArchiveObjectCoordinator : IArchiveStore {
  /// <summary>The object store the coordinator writes through.</summary>
  IArchiveObjectStore Objects { get; }

  /// <summary>
  /// The location this coordinator reads, restores, deletes and writes in: <see cref="IArchiveObjectStore.Location"/>.
  /// Every archive record is checked against it.
  /// </summary>
  /// <docs>fundamentals/events/archive-store#archive-locations</docs>
  ArchiveLocation ConfiguredLocation { get; }

  /// <summary>
  /// Acknowledges that the objects of a location now live where this coordinator is configured: the location row
  /// <paramref name="fromLocationId"/> is updated to <see cref="ConfiguredLocation"/>, or, when that location already
  /// exists, its records join it. No record's path is rewritten and no object is moved or deleted.
  /// </summary>
  /// <param name="fromLocationId">The location to repoint: <see cref="ArchiveLocationMismatchException.RecordedLocationId"/>.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The id of the location the records now name.</returns>
  /// <docs>fundamentals/events/archive-store#repointing-an-environment</docs>
  Task<long> RepointLocationAsync(long fromLocationId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Deletes up to <paramref name="maxObjects"/> due objects from the deletion queue: objects a restore, a delete or a
  /// stream purge stopped naming, in <see cref="ConfiguredLocation"/> only.
  /// </summary>
  /// <param name="maxObjects">The most objects this call deletes.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many objects were confirmed deleted.</returns>
  /// <docs>fundamentals/events/archive-store#purging-a-stream-archived-to-blob</docs>
  Task<int> DeleteQueuedObjectsAsync(int maxObjects, CancellationToken cancellationToken = default);

  /// <summary>
  /// Deletes, when a deletion delay is set, every object no record names that storage created at least that long ago.
  /// Without a delay it does nothing and never lists the location.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many objects were confirmed deleted.</returns>
  /// <docs>operations/deployment/restoring-an-environment-with-an-archive#the-orphan-sweep</docs>
  Task<int> SweepOrphanedObjectsAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// The maintenance step's sweep: <see cref="SweepOrphanedObjectsAsync"/> at most once per sweep interval in this
  /// process, and only with a deletion delay set.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many objects were confirmed deleted.</returns>
  /// <docs>operations/deployment/restoring-an-environment-with-an-archive#the-orphan-sweep</docs>
  Task<int> SweepOrphanedObjectsIfDueAsync(CancellationToken cancellationToken = default);
}
