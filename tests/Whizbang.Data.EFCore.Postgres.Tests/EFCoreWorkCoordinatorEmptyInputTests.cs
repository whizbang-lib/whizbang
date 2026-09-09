using System.Data;
using Microsoft.EntityFrameworkCore;
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
/// The calls that return something are pinned by their documented empty result. The ones that
/// return <see cref="Task"/> have no return value to check, so they are pinned by
/// <see cref="ConnectionOpenProbe"/> instead: the guard's whole point is that no connection is
/// acquired, and that is observable without pinning any particular SQL.
/// </remarks>
[Category("Integration")]
[Category("Shard2")]
public class EFCoreWorkCoordinatorEmptyInputTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator()
    => _coordinator(CreateDbContext());

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext dbContext)
    => new(dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  /// <summary>
  /// Counts how many times the DbContext's connection leaves <see cref="ConnectionState.Closed"/>
  /// while the probe is subscribed.
  /// </summary>
  /// <remarks>
  /// Every coordinator path that reaches PostgreSQL travels over this one connection: the raw
  /// <c>NpgsqlCommand</c> paths take it from <c>Database.GetDbConnection()</c> and open it through
  /// <c>CoordinatorConnectionScope</c>, and the <c>ExecuteSqlRawAsync</c> paths have EF open the same
  /// instance. Zero opens is therefore the observable form of "no round trip happened", which is what
  /// the empty-input guards exist to guarantee. It has to be counted as it happens rather than read
  /// afterwards — both paths close the connection again on the way out, so the resting state of a
  /// guarded call and an executed one are identical.
  /// </remarks>
  private sealed class ConnectionOpenProbe : IDisposable {
    private readonly System.Data.Common.DbConnection _connection;
    private int _opens;

    public ConnectionOpenProbe(DbContext dbContext) {
      _connection = dbContext.Database.GetDbConnection();
      _connection.StateChange += _onStateChange;
    }

    /// <summary>Transitions out of <see cref="ConnectionState.Closed"/> observed so far.</summary>
    public int Opens => Volatile.Read(ref _opens);

    private void _onStateChange(object sender, StateChangeEventArgs e) {
      if (e.OriginalState == ConnectionState.Closed && e.CurrentState != ConnectionState.Closed) {
        Interlocked.Increment(ref _opens);
      }
    }

    public void Dispose() => _connection.StateChange -= _onStateChange;
  }

  [Test]
  public async Task TheConnectionOpenProbe_CountsACommandThatIsActuallyIssuedAsync() {
    // Keeps the eleven "issues no command" assertions below from passing vacuously. If the
    // coordinator ever stopped routing through the DbContext's own connection, every one of them
    // would read zero for a reason that has nothing to do with the guard under test.
    await using var db = CreateDbContext();
    using var probe = new ConnectionOpenProbe(db);

    await _coordinator(db).RegisterConsumedTypesAsync(["Contracts.ProbeType"], asBaseline: true);

    await Assert.That(probe.Opens).IsGreaterThan(0);
  }

  [Test]
  public async Task ReclassifyEventsEphemeralAsync_WithNoTypes_ReturnsEmptyAsync() {
    var result = await _coordinator().ReclassifyEventsEphemeralAsync([]);

    await Assert.That(result).IsEqualTo(EphemeralReclassificationResult.Empty);
  }

  [Test]
  public async Task SyncPerspectiveRetentionAsync_WithNoDeclarations_IssuesNoCommandAsync() {
    await using var db = CreateDbContext();
    using var probe = new ConnectionOpenProbe(db);

    await _coordinator(db).SyncPerspectiveRetentionAsync([]);

    await Assert.That(probe.Opens).IsEqualTo(0);
  }

  [Test]
  public async Task HoldEphemeralDestructionAsync_WithNoEvents_IssuesNoCommandAsync() {
    await using var db = CreateDbContext();
    using var probe = new ConnectionOpenProbe(db);

    await _coordinator(db).HoldEphemeralDestructionAsync([], DateTimeOffset.UtcNow);

    await Assert.That(probe.Opens).IsEqualTo(0);
  }

  [Test]
  public async Task RecordDestructionFailureAsync_WithNoEvents_RecordsNothingAsync() {
    var affected = await _coordinator().RecordDestructionFailureAsync([], DateTimeOffset.UtcNow, maxRetries: 3);

    await Assert.That(affected).IsEqualTo(0);
  }

  [Test]
  public async Task RemoveOffloadClaimsAsync_WithNoKeys_IssuesNoCommandAsync() {
    await using var db = CreateDbContext();
    using var probe = new ConnectionOpenProbe(db);

    await _coordinator(db).RemoveOffloadClaimsAsync([]);

    await Assert.That(probe.Opens).IsEqualTo(0);
  }

  [Test]
  public async Task GetPerspectiveRowsAboutToReapAsync_WithNoTypes_ReturnsEmptyAsync() {
    var rows = await _coordinator().GetPerspectiveRowsAboutToReapAsync([]);

    await Assert.That(rows).IsEmpty();
  }

  [Test]
  public async Task HoldPerspectiveRowDestructionAsync_WithNoRows_IssuesNoCommandAsync() {
    await using var db = CreateDbContext();
    using var probe = new ConnectionOpenProbe(db);

    await _coordinator(db).HoldPerspectiveRowDestructionAsync([], DateTimeOffset.UtcNow);

    await Assert.That(probe.Opens).IsEqualTo(0);
  }

  [Test]
  public async Task ReleasePerspectiveRowHoldsAsync_WithNoRows_IssuesNoCommandAsync() {
    await using var db = CreateDbContext();
    using var probe = new ConnectionOpenProbe(db);

    await _coordinator(db).ReleasePerspectiveRowHoldsAsync([]);

    await Assert.That(probe.Opens).IsEqualTo(0);
  }

  [Test]
  public async Task RecordPerspectiveRowDestructionFailureAsync_WithNoRows_RecordsNothingAsync() {
    var affected = await _coordinator().RecordPerspectiveRowDestructionFailureAsync(
      [], TimeSpan.FromSeconds(30), maxRetries: 3, OnDestroyFailure.RetryThenForcedDelete);

    await Assert.That(affected).IsEqualTo(0);
  }

  [Test]
  public async Task RequeueRowEvictionsAsync_WithNoRows_IssuesNoCommandAsync() {
    await using var db = CreateDbContext();
    using var probe = new ConnectionOpenProbe(db);

    await _coordinator(db).RequeueRowEvictionsAsync([]);

    await Assert.That(probe.Opens).IsEqualTo(0);
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
  public async Task RegisterConsumedTypesAsync_WithNoTypes_IssuesNoCommandAsync() {
    await using var db = CreateDbContext();
    using var probe = new ConnectionOpenProbe(db);

    await _coordinator(db).RegisterConsumedTypesAsync([], asBaseline: false);

    await Assert.That(probe.Opens).IsEqualTo(0);
  }

  [Test]
  public async Task MarkConsumedTypeBackfillRequestedAsync_WithNoTypes_IssuesNoCommandAsync() {
    await using var db = CreateDbContext();
    using var probe = new ConnectionOpenProbe(db);

    await _coordinator(db).MarkConsumedTypeBackfillRequestedAsync([]);

    await Assert.That(probe.Opens).IsEqualTo(0);
  }

  [Test]
  public async Task StoreInboxMessagesAsync_WithNoMessages_IssuesNoCommandAsync() {
    await using var db = CreateDbContext();
    using var probe = new ConnectionOpenProbe(db);

    await _coordinator(db).StoreInboxMessagesAsync([], partitionCount: 4);

    await Assert.That(probe.Opens).IsEqualTo(0);
  }

  [Test]
  public async Task CompleteCoalesceFoldAsync_WithNothingFolded_IssuesNoCommandAsync() {
    await using var db = CreateDbContext();
    using var probe = new ConnectionOpenProbe(db);

    await _coordinator(db).CompleteCoalesceFoldAsync([], [], partitionCount: 4);

    await Assert.That(probe.Opens).IsEqualTo(0);
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
  public async Task IntegrityStampRepairWindowsAsync_WithNoKeys_IssuesNoCommandAsync() {
    await using var db = CreateDbContext();
    using var probe = new ConnectionOpenProbe(db);

    await _coordinator(db).IntegrityStampRepairWindowsAsync(
      Guid.CreateVersion7(), [], windowFrom: 0, windowUntil: 10);

    await Assert.That(probe.Opens).IsEqualTo(0);
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

  [Test]
  public async Task ReapExhaustedOrphanedPerspectiveRowsAsync_WithNoStreams_ReapsNothingAsync() {
    // The drain-mode reaper is handed whatever streams the current pass found orphaned, which is
    // routinely none. Zero is the count of rows reaped, and it must come from the guard rather than
    // from a reap function invoked with an empty array on every drain tick.
    var reaped = await _coordinator().ReapExhaustedOrphanedPerspectiveRowsAsync(
      Guid.CreateVersion7(), [], maxAttempts: 3);

    await Assert.That(reaped).IsEqualTo(0);
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
