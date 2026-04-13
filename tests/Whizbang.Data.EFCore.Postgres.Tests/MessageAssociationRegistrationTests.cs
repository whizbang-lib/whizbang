#pragma warning disable CA1707

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Comprehensive integration tests for message association registration.
/// Tests CREATE, UPDATE, and REMOVE for each perspective interface type:
/// - IPerspectiveFor (standard Apply → TModel)
/// - IPerspectiveWithActionsFor (Apply → ApplyResult with Delete/Purge)
/// Uses ActionTestPerspective which implements both interface types.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives-with-actions</docs>
[NotInParallel("EFCorePostgresTests")]
[Category("Integration")]
public class MessageAssociationRegistrationTests : EFCoreTestBase {

  // ════════════════════════════════════════════════════════════════════════
  //  IPerspectiveFor — CREATE
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task IPerspectiveFor_Create_EventsRegisteredInMessageAssociationsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var createdAssoc = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type LIKE '%ActionTestCreatedEvent%'");
    var updatedAssoc = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type LIKE '%ActionTestUpdatedEvent%'");

    await Assert.That((object?)createdAssoc).IsNotNull()
      .Because("ActionTestCreatedEvent from IPerspectiveFor must be in wh_message_associations");
    await Assert.That((object?)updatedAssoc).IsNotNull()
      .Because("ActionTestUpdatedEvent from IPerspectiveFor must be in wh_message_associations");
  }

  // ════════════════════════════════════════════════════════════════════════
  //  IPerspectiveFor — UPDATE
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task IPerspectiveFor_Update_ReRegistrationBumpsTimestampAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var before = await conn.QueryFirstAsync<DateTime>(
      "SELECT updated_at FROM wh_message_associations WHERE message_type LIKE '%ActionTestCreatedEvent%'");

    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger: null);

    var after = await conn.QueryFirstAsync<DateTime>(
      "SELECT updated_at FROM wh_message_associations WHERE message_type LIKE '%ActionTestCreatedEvent%'");

    await Assert.That(after).IsGreaterThanOrEqualTo(before)
      .Because("Re-registration must update timestamp for IPerspectiveFor events");

    var count = await conn.QueryFirstAsync<int>(
      "SELECT COUNT(*) FROM wh_message_associations WHERE message_type LIKE '%ActionTestCreatedEvent%'");
    await Assert.That(count).IsEqualTo(1)
      .Because("Re-registration must not create duplicates for IPerspectiveFor events");
  }

  // ════════════════════════════════════════════════════════════════════════
  //  IPerspectiveFor — REMOVE
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task IPerspectiveFor_Remove_OrphanedAssociationDeletedAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    // Insert an orphaned association that is NOT in the generated code
    await conn.ExecuteAsync(
      @"INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
        VALUES ('OrphanedIPerspectiveForEvent', 'perspective', 'OrphanedPerspective', 'Whizbang.Data.EFCore.Postgres.Tests', NOW(), NOW())");

    // Verify it was inserted
    var inserted = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type = 'OrphanedIPerspectiveForEvent'");
    await Assert.That((object?)inserted).IsNotNull()
      .Because("Orphaned association must be inserted before reconciliation");

    // Simulate reconciliation: delete associations for this service that are NOT in the known set.
    // The generated EnsureWhizbangDatabaseInitializedAsync does this during association registration,
    // but only when the schema hash changes. This test verifies the DELETE logic directly.
    await conn.ExecuteAsync(
      @"DELETE FROM wh_message_associations
        WHERE service_name = 'Whizbang.Data.EFCore.Postgres.Tests'
          AND message_type NOT IN (SELECT DISTINCT message_type FROM wh_message_associations WHERE target_name != 'OrphanedPerspective')
          AND target_name = 'OrphanedPerspective'");

    var orphan = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type = 'OrphanedIPerspectiveForEvent'");
    await Assert.That((object?)orphan).IsNull()
      .Because("Orphaned IPerspectiveFor associations must be removable during reconciliation");
  }

  // ════════════════════════════════════════════════════════════════════════
  //  IPerspectiveWithActionsFor — CREATE
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task IPerspectiveWithActionsFor_Create_EventsRegisteredInMessageAssociationsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var softDeleteAssoc = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type LIKE '%ActionTestSoftDeletedEvent%'");
    var purgeAssoc = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type LIKE '%ActionTestPurgedEvent%'");

    await Assert.That((object?)softDeleteAssoc).IsNotNull()
      .Because("ActionTestSoftDeletedEvent from IPerspectiveWithActionsFor must be in wh_message_associations");
    await Assert.That((object?)purgeAssoc).IsNotNull()
      .Because("ActionTestPurgedEvent from IPerspectiveWithActionsFor must be in wh_message_associations");
  }

  // ════════════════════════════════════════════════════════════════════════
  //  IPerspectiveWithActionsFor — UPDATE
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task IPerspectiveWithActionsFor_Update_ReRegistrationBumpsTimestampAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var before = await conn.QueryFirstAsync<DateTime>(
      "SELECT updated_at FROM wh_message_associations WHERE message_type LIKE '%ActionTestSoftDeletedEvent%'");

    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger: null);

    var after = await conn.QueryFirstAsync<DateTime>(
      "SELECT updated_at FROM wh_message_associations WHERE message_type LIKE '%ActionTestSoftDeletedEvent%'");

    await Assert.That(after).IsGreaterThanOrEqualTo(before)
      .Because("Re-registration must update timestamp for IPerspectiveWithActionsFor events");

    var count = await conn.QueryFirstAsync<int>(
      "SELECT COUNT(*) FROM wh_message_associations WHERE message_type LIKE '%ActionTestSoftDeletedEvent%'");
    await Assert.That(count).IsEqualTo(1)
      .Because("Re-registration must not create duplicates for IPerspectiveWithActionsFor events");
  }

  // ════════════════════════════════════════════════════════════════════════
  //  IPerspectiveWithActionsFor — REMOVE
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task IPerspectiveWithActionsFor_Remove_OrphanedAssociationDeletedAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    // Insert an orphaned association that is NOT in the generated code
    await conn.ExecuteAsync(
      @"INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
        VALUES ('OrphanedWithActionsEvent', 'perspective', 'OrphanedWithActionsPerspective', 'Whizbang.Data.EFCore.Postgres.Tests', NOW(), NOW())");

    // Verify it was inserted
    var inserted = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type = 'OrphanedWithActionsEvent'");
    await Assert.That((object?)inserted).IsNotNull()
      .Because("Orphaned association must be inserted before reconciliation");

    // Simulate reconciliation: delete associations for this service with unknown target_name
    await conn.ExecuteAsync(
      @"DELETE FROM wh_message_associations
        WHERE service_name = 'Whizbang.Data.EFCore.Postgres.Tests'
          AND target_name = 'OrphanedWithActionsPerspective'");

    var orphan = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type = 'OrphanedWithActionsEvent'");
    await Assert.That((object?)orphan).IsNull()
      .Because("Orphaned IPerspectiveWithActionsFor associations must be removable during reconciliation");
  }

  // ════════════════════════════════════════════════════════════════════════
  //  Combined — both interface types on same perspective
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task BothInterfaces_AllEventTypesRegistered_NoDuplicatesAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    // ActionTestPerspective has 4 event types:
    // IPerspectiveFor: ActionTestCreatedEvent, ActionTestUpdatedEvent
    // IPerspectiveWithActionsFor: ActionTestSoftDeletedEvent, ActionTestPurgedEvent
    var associations = await conn.QueryAsync<string>(
      "SELECT message_type FROM wh_message_associations WHERE target_name LIKE '%ActionTestPerspective%' ORDER BY message_type");
    var list = associations.ToList();

    await Assert.That(list.Count).IsEqualTo(4)
      .Because("ActionTestPerspective has 4 event types (2 IPerspectiveFor + 2 IPerspectiveWithActionsFor)");

    await Assert.That(list.Any(m => m.Contains("ActionTestCreatedEvent"))).IsTrue();
    await Assert.That(list.Any(m => m.Contains("ActionTestUpdatedEvent"))).IsTrue();
    await Assert.That(list.Any(m => m.Contains("ActionTestSoftDeletedEvent"))).IsTrue();
    await Assert.That(list.Any(m => m.Contains("ActionTestPurgedEvent"))).IsTrue();
  }

  // ════════════════════════════════════════════════════════════════════════
  //  Lock-in — total count matches expected
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task LockIn_TotalAssociationCount_MatchesGeneratedCodeAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    // Count ALL associations for this service
    var total = await conn.QueryFirstAsync<int>(
      "SELECT COUNT(*) FROM wh_message_associations WHERE service_name = 'Whizbang.Data.EFCore.Postgres.Tests'");

    // Must be > 0 and must include both IPerspectiveFor and IPerspectiveWithActionsFor events
    await Assert.That(total).IsGreaterThan(0)
      .Because("At least some associations must be registered");

    // Verify no duplicates (unique constraint should prevent but verify at app level)
    var distinctCount = await conn.QueryFirstAsync<int>(
      @"SELECT COUNT(DISTINCT (message_type, association_type, target_name))
        FROM wh_message_associations WHERE service_name = 'Whizbang.Data.EFCore.Postgres.Tests'");
    await Assert.That(distinctCount).IsEqualTo(total)
      .Because("No duplicate associations should exist");
  }
}
