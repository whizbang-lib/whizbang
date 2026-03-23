#pragma warning disable CA1707

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests verifying that register_message_associations() correctly
/// handles create, update, and remove of perspective associations — including
/// events from IPerspectiveWithActionsFor (e.g., soft delete, purge events).
/// </summary>
/// <docs>fundamentals/perspectives/perspectives-with-actions</docs>
[NotInParallel("EFCorePostgresTests")]
[Category("Integration")]
public class MessageAssociationRegistrationTests : EFCoreTestBase {

  // ════════════════════════════════════════════════════════════════════════
  //  CREATE: IPerspectiveWithActionsFor events get registered
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task SchemaInit_RegistersIPerspectiveWithActionsForEvents_InMessageAssociationsAsync() {
    // Arrange — EFCoreTestBase.SetupAsync already calls InitializeDatabaseAsync
    // which runs register_message_associations() with the generated JSON.
    // The generated JSON should include ActionTestSoftDeletedEvent and ActionTestPurgedEvent
    // from ActionTestPerspective which uses IPerspectiveWithActionsFor.

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    // Act — query associations for WithActions events
    var softDeleteAssoc = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type LIKE '%ActionTestSoftDeletedEvent%'");
    var purgeAssoc = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type LIKE '%ActionTestPurgedEvent%'");

    // Assert — both IPerspectiveWithActionsFor events must be registered
    await Assert.That((object?)softDeleteAssoc).IsNotNull()
      .Because("ActionTestSoftDeletedEvent from IPerspectiveWithActionsFor must be in wh_message_associations");
    await Assert.That((object?)purgeAssoc).IsNotNull()
      .Because("ActionTestPurgedEvent from IPerspectiveWithActionsFor must be in wh_message_associations");
  }

  [Test]
  public async Task SchemaInit_RegistersIPerspectiveForEvents_InMessageAssociationsAsync() {
    // Verify IPerspectiveFor events are also registered (baseline)
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var createdAssoc = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type LIKE '%ActionTestCreatedEvent%'");

    await Assert.That((object?)createdAssoc).IsNotNull()
      .Because("ActionTestCreatedEvent from IPerspectiveFor must be in wh_message_associations");
  }

  // ════════════════════════════════════════════════════════════════════════
  //  UPDATE: Re-registration updates timestamps
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task SchemaInit_CalledTwice_UpdatesTimestampsWithoutDuplicatesAsync() {
    // Arrange
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    // Get initial timestamps
    var before = await conn.QueryAsync<(string message_type, DateTime updated_at)>(
      "SELECT message_type, updated_at FROM wh_message_associations WHERE message_type LIKE '%ActionTest%' ORDER BY message_type");
    var beforeList = before.ToList();

    // Act — re-initialize (calls register_message_associations again)
    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger: null);

    // Get updated timestamps
    var after = await conn.QueryAsync<(string message_type, DateTime updated_at)>(
      "SELECT message_type, updated_at FROM wh_message_associations WHERE message_type LIKE '%ActionTest%' ORDER BY message_type");
    var afterList = after.ToList();

    // Assert — same count (no duplicates), timestamps updated
    await Assert.That(afterList.Count).IsEqualTo(beforeList.Count)
      .Because("Re-registration should not create duplicates");
    // Timestamps should be >= before (updated_at gets bumped)
    for (int i = 0; i < beforeList.Count; i++) {
      await Assert.That(afterList[i].updated_at).IsGreaterThanOrEqualTo(beforeList[i].updated_at);
    }
  }

  // ════════════════════════════════════════════════════════════════════════
  //  REMOVE: Orphaned associations get deleted
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task SchemaInit_RemovesOrphanedAssociations_OnReRegistrationAsync() {
    // Arrange — insert a fake association that doesn't exist in generated code
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    await conn.ExecuteAsync(
      @"INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
        VALUES ('FakeOrphanedEvent', 'perspective', 'FakeOrphanedPerspective', 'Whizbang.Data.EFCore.Postgres.Tests', NOW(), NOW())");

    var orphanExists = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type = 'FakeOrphanedEvent'");
    await Assert.That((object?)orphanExists).IsNotNull()
      .Because("Setup: orphaned association should exist before re-registration");

    // Act — re-initialize (reconciliation should remove orphan)
    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger: null);

    // Assert — orphaned association removed
    var orphanAfter = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type = 'FakeOrphanedEvent'");
    await Assert.That((object?)orphanAfter).IsNull()
      .Because("Orphaned associations not in generated code must be removed during reconciliation");
  }
}
