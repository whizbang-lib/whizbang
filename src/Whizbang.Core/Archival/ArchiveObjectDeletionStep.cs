// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Workers;

namespace Whizbang.Core.Archival;

/// <summary>
/// The maintenance step that deletes archive objects no manifest row names any more (a stream purge, or a restore or
/// delete whose own deletion failed), so no object is left unreachable in storage. It runs in the maintenance cycle, so
/// under the maintainer duty when role assignment manages it, and deletes at most <see cref="BATCH_SIZE"/> due objects
/// per cycle; a failed delete stays queued and is retried after its backoff. With a deletion delay set, a deletion is
/// due only that long after it was queued, and the step also sweeps the location for orphaned objects at most once per
/// sweep interval. With no coordinator registered, it does nothing.
/// </summary>
/// <docs>fundamentals/events/archive-store#purging-a-stream-archived-to-blob</docs>
/// <docs>operations/deployment/restoring-an-environment-with-an-archive#the-orphan-sweep</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs:DeletionStep_DrainsThenSweeps_AndWithoutACoordinatorDoesNothingAsync</tests>
/// <tests>tests/Whizbang.Storage.AzureBlob.Integration.Tests/AzureBlobArchiveStoreDeleteAfterTests.cs:Step_SweepsAtMostOncePerInterval_AndOnlyWithADelayAsync</tests>
/// <tests>tests/Whizbang.Storage.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Purge_QueuesTheObjects_AndTheStepDeletesThemAsync</tests>
/// <param name="coordinator">The archive coordinator that owns the objects, or <see langword="null"/> for none.</param>
public sealed class ArchiveObjectDeletionStep(IArchiveObjectCoordinator? coordinator) : IMaintenanceStep {
  /// <summary>
  /// The most objects one cycle deletes. A purge of many streams drains over several cycles rather than holding one
  /// cycle for a long run of storage calls.
  /// </summary>
  public const int BATCH_SIZE = 100;

  private readonly IArchiveObjectCoordinator? _coordinator = coordinator;

  /// <inheritdoc />
  public string Name => "archive-object-deletion";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    if (_coordinator is null) {
      return;
    }
    _ = await _coordinator.DeleteQueuedObjectsAsync(BATCH_SIZE, cancellationToken).ConfigureAwait(false);
    // #2367: with a deletion delay, the daily orphan sweep; without one, nothing lists the location.
    _ = await _coordinator.SweepOrphanedObjectsIfDueAsync(cancellationToken).ConfigureAwait(false);
  }
}
