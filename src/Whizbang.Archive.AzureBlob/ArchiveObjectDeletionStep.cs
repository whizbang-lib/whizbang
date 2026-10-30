// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Workers;

namespace Whizbang.Archive.AzureBlob;

/// <summary>
/// The maintenance step that deletes archive objects no manifest row names any more (a stream purge, or a restore or
/// delete whose own deletion failed), so no object is left unreachable in blob storage. It runs in the maintenance cycle,
/// so under the maintainer duty when role assignment manages it, and deletes at most <see cref="BATCH_SIZE"/> due objects
/// per cycle; a failed delete stays queued and is retried after its backoff.
/// </summary>
/// <docs>fundamentals/events/archive-store#purging-a-stream-archived-to-blob</docs>
/// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Purge_QueuesTheObjects_AndTheStepDeletesThemAsync</tests>
/// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs:Registration_AddsTheObjectDeletionStep_OnlyWithThePackageAsync</tests>
public sealed class ArchiveObjectDeletionStep : IMaintenanceStep {
  /// <summary>
  /// The most objects one cycle deletes. A purge of many streams drains over several cycles rather than holding one
  /// cycle for a long run of storage calls.
  /// </summary>
  public const int BATCH_SIZE = 100;

  private readonly AzureBlobArchiveStore _store;

  /// <summary>Creates the step.</summary>
  /// <param name="store">The Azure Blob store that owns the objects.</param>
  public ArchiveObjectDeletionStep(AzureBlobArchiveStore store) {
    ArgumentNullException.ThrowIfNull(store);
    _store = store;
  }

  /// <inheritdoc />
  public string Name => "archive-object-deletion";

  /// <inheritdoc />
  public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    return _store.DeleteQueuedObjectsAsync(BATCH_SIZE, cancellationToken);
  }
}
