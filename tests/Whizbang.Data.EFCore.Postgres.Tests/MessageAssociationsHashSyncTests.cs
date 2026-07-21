#pragma warning disable CA1707

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the associations-set hash drift detection added to
/// EnsureWhizbangDatabaseInitializedAsync. These lock in that adding or removing
/// an Apply(TEvent) method on a perspective re-syncs wh_message_associations on
/// the next startup, even when the perspective table DDL didn't change.
///
/// Tests simulate "code changed since last init" by mutating the stored hash row
/// (or the wh_message_associations rows) directly, since C# perspectives in the
/// test assembly are fixed at compile time.
/// </summary>
[NotInParallel("EFCorePostgresTests")]
[Category("Integration")]
public class MessageAssociationsHashSyncTests : EFCoreTestBase {

  private const string ServiceName = "Whizbang.Data.EFCore.Postgres.Tests";
  private const string HashFileName = "associations:WorkCoordinationDbContext";

  // ════════════════════════════════════════════════════════════════════════
  // Baseline: the hash row lands on the very first init.
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task FirstInit_WritesAssociationHashRowAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
      $"SELECT content_hash, owner FROM wh_schema_migrations WHERE file_name = '{HashFileName}'");

    await Assert.That((object?)row).IsNotNull()
      .Because("First init must record a wh_schema_migrations row with owner='association' so subsequent startups can short-circuit");
    var dict = (IDictionary<string, object?>)row!;
    await Assert.That((string?)dict["owner"]).IsEqualTo("association");
    await Assert.That(((string?)dict["content_hash"])!).IsNotEmpty();
  }

  // ════════════════════════════════════════════════════════════════════════
  // Add: simulate "new Apply method was added since last init" by wiping the
  // hash row and removing a known association — re-init must re-add the row.
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task StaleHash_TriggersReRegistration_AddsMissingAssociationAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    // Simulate drift: wipe the hash row and delete a known association.
    // The stored hash going missing is the canonical "DB was last touched by an
    // older build that didn't track this" state.
    await conn.ExecuteAsync(
      $"DELETE FROM wh_schema_migrations WHERE file_name = '{HashFileName}'");
    await conn.ExecuteAsync(@"
      DELETE FROM wh_message_associations
      WHERE message_type LIKE '%ActionTestCreatedEvent%'
        AND service_name = '" + ServiceName + "'");

    var missing = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type LIKE '%ActionTestCreatedEvent%'");
    await Assert.That((object?)missing).IsNull()
      .Because("Precondition: the row must be absent before re-init");

    // Re-run init — should detect missing hash row and re-register
    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger: null);

    var restored = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type LIKE '%ActionTestCreatedEvent%'");
    await Assert.That((object?)restored).IsNotNull()
      .Because("Missing hash row must trigger Step 5 re-registration and restore the deleted association");

    var hashRow = await conn.QueryFirstOrDefaultAsync<dynamic>(
      $"SELECT content_hash FROM wh_schema_migrations WHERE file_name = '{HashFileName}'");
    await Assert.That((object?)hashRow).IsNotNull()
      .Because("Hash row must be upserted after successful re-registration");
  }

  // ════════════════════════════════════════════════════════════════════════
  // Remove: simulate "an Apply method was removed" by inserting an orphan row
  // and wiping the hash — re-init must delete the orphan via Step 5's DELETE.
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task StaleHash_TriggersReRegistration_DeletesOrphanAssociationAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    await conn.ExecuteAsync(
      $"DELETE FROM wh_schema_migrations WHERE file_name = '{HashFileName}'");
    await conn.ExecuteAsync(@"
      INSERT INTO wh_message_associations
        (message_type, association_type, target_name, service_name, created_at, updated_at)
      VALUES
        ('GhostEventThatNoLongerExists', 'perspective',
         'Whizbang.Data.EFCore.Postgres.Tests.Perspectives.ActionTestPerspective',
         '" + ServiceName + @"', NOW(), NOW())");

    var orphanBefore = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type = 'GhostEventThatNoLongerExists'");
    await Assert.That((object?)orphanBefore).IsNotNull()
      .Because("Precondition: the orphan row must exist before re-init");

    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger: null);

    var orphanAfter = await conn.QueryFirstOrDefaultAsync<dynamic>(
      "SELECT * FROM wh_message_associations WHERE message_type = 'GhostEventThatNoLongerExists'");
    await Assert.That((object?)orphanAfter).IsNull()
      .Because("Re-init with drifted hash must prune orphaned associations");
  }

  // ════════════════════════════════════════════════════════════════════════
  // Idempotency: second init with matching hash must NOT touch associations.
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task HashMatch_SkipsStep5_LeavesUpdatedAtUnchangedAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var firstCreatedAt = await conn.QueryFirstAsync<DateTime>(@"
      SELECT updated_at FROM wh_message_associations
      WHERE service_name = '" + ServiceName + @"'
      ORDER BY message_type LIMIT 1");

    // Sleep substitute — drive the DB clock forward by issuing a trivial statement.
    // We just need two separate points-in-time in wh_schema_migrations to tell if
    // Step 5 re-touched the row. No Task.Delay (per project test-timing rules).
    await conn.ExecuteAsync("SELECT pg_sleep(0.01)");

    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger: null);

    var secondUpdatedAt = await conn.QueryFirstAsync<DateTime>(@"
      SELECT updated_at FROM wh_message_associations
      WHERE service_name = '" + ServiceName + @"'
      ORDER BY message_type LIMIT 1");

    await Assert.That(secondUpdatedAt).IsEqualTo(firstCreatedAt)
      .Because("When the associations hash is unchanged, Step 5 must skip and leave updated_at alone");
  }

  // ════════════════════════════════════════════════════════════════════════
  // service_name scoping: a different service's rows must be preserved when
  // this service re-registers. Defense-in-depth in case a schema is shared.
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task ReRegistration_PreservesRowsFromOtherServicesAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    await conn.ExecuteAsync(@"
      INSERT INTO wh_message_associations
        (message_type, association_type, target_name, service_name, created_at, updated_at)
      VALUES
        ('OtherServiceEvent', 'perspective', 'OtherServicePerspective',
         'SomeOtherService', NOW(), NOW())");

    // Simulate drift on THIS service so Step 5 runs
    await conn.ExecuteAsync(
      $"DELETE FROM wh_schema_migrations WHERE file_name = '{HashFileName}'");

    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger: null);

    var otherService = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
      SELECT * FROM wh_message_associations
      WHERE service_name = 'SomeOtherService'
        AND message_type = 'OtherServiceEvent'");

    await Assert.That((object?)otherService).IsNotNull()
      .Because("Step 5's orphan DELETE must be scoped to the calling service_name and leave other services' rows alone");
  }

  // ════════════════════════════════════════════════════════════════════════
  // Cascade cleanup: pending wh_perspective_events rows for a removed
  // association must be cleared; non-pending rows must be preserved (audit).
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task RemovedAssociation_ClearsPendingPerspectiveEventsOnlyAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    const string OrphanPerspectiveName =
      "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.OrphanPerspective";
    // Canonical "TypeName, AssemblyName" form — normalize_event_type passes this through
    // unchanged, so wh_message_associations.normalized_message_type and
    // wh_event_store.event_type end up equal and the cascade JOIN matches.
    const string OrphanMessageType = "Whizbang.Tests.GhostRemovedEvent, Whizbang.Tests";

    var streamId = Guid.NewGuid();
    var pendingEventId = Guid.NewGuid();
    var completedEventId = Guid.NewGuid();
    var pendingWorkId = Guid.NewGuid();
    var completedWorkId = Guid.NewGuid();

    await conn.ExecuteAsync(
      $"DELETE FROM wh_schema_migrations WHERE file_name = '{HashFileName}'");
    await conn.ExecuteAsync(@"
      INSERT INTO wh_message_associations
        (message_type, association_type, target_name, service_name,
         normalized_message_type, created_at, updated_at)
      VALUES
        ('" + OrphanMessageType + @"', 'perspective', '" + OrphanPerspectiveName + @"',
         '" + ServiceName + @"', '" + OrphanMessageType + @"', NOW(), NOW())");
    // Two separate events so the (stream_id, perspective_name, event_id) uniqueness on
    // wh_perspective_events doesn't trip — we need one pending row and one completed row.
    await conn.ExecuteAsync(@"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type,
         version, created_at)
      VALUES
        ('" + pendingEventId + "', '" + streamId + "', '" + streamId + @"',
         'TestAggregate', '" + OrphanMessageType + @"',
         1, NOW()),
        ('" + completedEventId + "', '" + streamId + "', '" + streamId + @"',
         'TestAggregate', '" + OrphanMessageType + @"',
         2, NOW())");
    await conn.ExecuteAsync(@"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
      VALUES
        ('" + pendingWorkId + "', '" + streamId + "', '" + OrphanPerspectiveName + @"',
         '" + pendingEventId + @"', 0, 0, NOW()),
        ('" + completedWorkId + "', '" + streamId + "', '" + OrphanPerspectiveName + @"',
         '" + completedEventId + @"', 2, 0, NOW())");

    await using var dbContext = CreateDbContext();
    await dbContext.EnsureWhizbangDatabaseInitializedAsync(logger: null);

    var pending = await conn.QueryFirstOrDefaultAsync<dynamic>(
      $"SELECT * FROM wh_perspective_events WHERE event_work_id = '{pendingWorkId}'");
    var completed = await conn.QueryFirstOrDefaultAsync<dynamic>(
      $"SELECT * FROM wh_perspective_events WHERE event_work_id = '{completedWorkId}'");

    await Assert.That((object?)pending).IsNull()
      .Because("Pending (status=0) perspective_events rows for a removed association must be cleaned up");
    await Assert.That((object?)completed).IsNotNull()
      .Because("Non-pending (status!=0) perspective_events rows must be preserved as audit state");
  }
}
