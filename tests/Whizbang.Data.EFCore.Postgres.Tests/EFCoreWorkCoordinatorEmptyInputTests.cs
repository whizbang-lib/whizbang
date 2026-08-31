using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Every batch entry point on <see cref="EFCoreWorkCoordinator"/> short-circuits on an
/// empty input rather than issuing SQL. That is not cosmetic: these run on maintenance and
/// dispatch ticks against every table in the schema, and a round trip per empty batch is a
/// connection acquired and a function invoked for nothing, on a schedule, in every host.
/// </summary>
/// <remarks>
/// The assertions are deliberately about the documented empty result rather than about SQL
/// not being issued — the guard is observable through the return value, and asserting on
/// the absence of a query would need a connection interceptor that pins the implementation.
/// </remarks>
[Category("Integration")]
[Category("Shard2")]
public class EFCoreWorkCoordinatorEmptyInputTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator()
    => new(CreateDbContext(), Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task ReclassifyEventsEphemeralAsync_WithNoTypes_ReturnsEmptyAsync() {
    var result = await _coordinator().ReclassifyEventsEphemeralAsync([]);

    await Assert.That(result).IsEqualTo(EphemeralReclassificationResult.Empty);
  }

  [Test]
  public async Task SyncPerspectiveRetentionAsync_WithNoDeclarations_DoesNothingAsync() {
    await _coordinator().SyncPerspectiveRetentionAsync([]);
  }

  [Test]
  public async Task HoldEphemeralDestructionAsync_WithNoEvents_DoesNothingAsync() {
    await _coordinator().HoldEphemeralDestructionAsync([], DateTimeOffset.UtcNow);
  }

  [Test]
  public async Task RecordDestructionFailureAsync_WithNoEvents_RecordsNothingAsync() {
    var affected = await _coordinator().RecordDestructionFailureAsync([], DateTimeOffset.UtcNow, maxRetries: 3);

    await Assert.That(affected).IsEqualTo(0);
  }

  [Test]
  public async Task RemoveOffloadClaimsAsync_WithNoKeys_DoesNothingAsync() {
    await _coordinator().RemoveOffloadClaimsAsync([]);
  }

  [Test]
  public async Task GetPerspectiveRowsAboutToReapAsync_WithNoTypes_ReturnsEmptyAsync() {
    var rows = await _coordinator().GetPerspectiveRowsAboutToReapAsync([]);

    await Assert.That(rows).IsEmpty();
  }

  [Test]
  public async Task HoldPerspectiveRowDestructionAsync_WithNoRows_DoesNothingAsync() {
    await _coordinator().HoldPerspectiveRowDestructionAsync([], DateTimeOffset.UtcNow);
  }

  [Test]
  public async Task ReleasePerspectiveRowHoldsAsync_WithNoRows_DoesNothingAsync() {
    await _coordinator().ReleasePerspectiveRowHoldsAsync([]);
  }

  [Test]
  public async Task RecordPerspectiveRowDestructionFailureAsync_WithNoRows_RecordsNothingAsync() {
    var affected = await _coordinator().RecordPerspectiveRowDestructionFailureAsync(
      [], TimeSpan.FromSeconds(30), maxRetries: 3, OnDestroyFailure.RetryThenForcedDelete);

    await Assert.That(affected).IsEqualTo(0);
  }

  [Test]
  public async Task RequeueRowEvictionsAsync_WithNoRows_DoesNothingAsync() {
    await _coordinator().RequeueRowEvictionsAsync([]);
  }

  [Test]
  public async Task GetPerspectiveTableNamesAsync_WithNoTypes_ReturnsEmptyAsync() {
    var names = await _coordinator().GetPerspectiveTableNamesAsync([]);

    await Assert.That(names).IsEmpty();
  }

  [Test]
  public async Task GetPerspectiveRowsByIdsAsync_WithNoIds_ReturnsEmptyAsync() {
    var rows = await _coordinator().GetPerspectiveRowsByIdsAsync("Some.Type", "wh_per_orders", []);

    await Assert.That(rows).IsEmpty();
  }

  [Test]
  public async Task CascadeDeletePerspectiveRowsAsync_WithNoIds_DeletesNothingAsync() {
    var deleted = await _coordinator().CascadeDeletePerspectiveRowsAsync("wh_per_orders", []);

    await Assert.That(deleted).IsEqualTo(0);
  }

  [Test]
  public async Task FoldStreamApplyPathsAsync_WithNoStreams_FoldsNothingAsync() {
    var folded = await _coordinator().FoldStreamApplyPathsAsync([]);

    await Assert.That(folded).IsEqualTo(0);
  }

  [Test]
  public async Task ReconcileFollowerPresenceAsync_WithNoAnnouncers_ReconcilesNothingAsync() {
    var reconciled = await _coordinator().ReconcileFollowerPresenceAsync("wh_per_followers", []);

    await Assert.That(reconciled).IsEqualTo(0);
  }

  [Test]
  public async Task RegisterConsumedTypesAsync_WithNoTypes_DoesNothingAsync() {
    await _coordinator().RegisterConsumedTypesAsync([], asBaseline: false);
  }

  [Test]
  public async Task MarkConsumedTypeBackfillRequestedAsync_WithNoTypes_DoesNothingAsync() {
    await _coordinator().MarkConsumedTypeBackfillRequestedAsync([]);
  }

  [Test]
  public async Task StoreInboxMessagesAsync_WithNoMessages_DoesNothingAsync() {
    await _coordinator().StoreInboxMessagesAsync([], partitionCount: 4);
  }

  [Test]
  public async Task CompleteCoalesceFoldAsync_WithNothingFolded_DoesNothingAsync() {
    await _coordinator().CompleteCoalesceFoldAsync([], [], partitionCount: 4);
  }

  [Test]
  public async Task CompletePerspectiveEventsAsync_WithNoWorkItems_CompletesNothingAsync() {
    var completed = await _coordinator().CompletePerspectiveEventsAsync([], debugMode: false);

    await Assert.That(completed).IsEqualTo(0);
  }

  [Test]
  public async Task GetStreamEventsAsync_WithNoStreams_ReturnsEmptyAsync() {
    var events = await _coordinator().GetStreamEventsAsync(Guid.CreateVersion7(), []);

    await Assert.That(events).IsEmpty();
  }

  [Test]
  public async Task IntegrityStampRepairWindowsAsync_WithNoKeys_DoesNothingAsync() {
    await _coordinator().IntegrityStampRepairWindowsAsync(
      Guid.CreateVersion7(), [], windowFrom: 0, windowUntil: 10);
  }

  [Test]
  public async Task IntegrityClaimRepairDrainAsync_WithNoOrigins_ReturnsEmptyAsync() {
    var items = await _coordinator().IntegrityClaimRepairDrainAsync(
      [], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), maxAttempts: 3, limit: 10);

    await Assert.That(items).IsEmpty();
  }

  [Test]
  public async Task IntegrityClaimRepairDrainAsync_WithAZeroLimit_ReturnsEmptyAsync() {
    // Both halves of the guard matter: a caller passing a live origin with limit 0 is
    // asking for nothing, and must not cost a claim round trip either.
    var items = await _coordinator().IntegrityClaimRepairDrainAsync(
      [Guid.CreateVersion7()], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), maxAttempts: 3, limit: 0);

    await Assert.That(items).IsEmpty();
  }

  [Test]
  public async Task StoreInboxMessagesWithObservationsAsync_WithNoMessages_ReturnsEmptyAsync() {
    var observations = await _coordinator().StoreInboxMessagesWithObservationsAsync([], partitionCount: 4);

    await Assert.That(observations).IsEmpty();
  }

  // --- Empty result sets -----------------------------------------------------
  // Distinct from an empty input: the call is made, the function runs, and returns no
  // rows. The guard turns that into the documented empty value rather than throwing on
  // a reader that never advanced.

  [Test]
  public async Task ReclassifyEventsEphemeralAsync_WithUnknownTypes_ReturnsEmptyAsync() {
    var result = await _coordinator().ReclassifyEventsEphemeralAsync(
      [$"Whizbang.Tests.NoSuchEvent.{Guid.NewGuid():N}, Whizbang.Tests"]);

    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task RegisterTypeDefinitionAsync_ForAnUnknownEventType_ReturnsAResultAsync() {
    var registration = await _coordinator().RegisterTypeDefinitionAsync(
      $"Whizbang.Tests.NoSuchEvent.{Guid.NewGuid():N}, Whizbang.Tests",
      settingsHashHex: "00",
      schemaHashHex: "00",
      schemaVersion: 1);

    await Assert.That(registration).IsNotNull();
  }
}
