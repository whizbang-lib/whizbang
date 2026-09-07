using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Coverage for the one branch <see cref="DapperEventTypeRenameToolTests"/> doesn't reach:
/// <see cref="DapperEventTypeRenameTool.DetectRenamesAsync"/>'s guard against a registry row whose
/// <c>pinned_id</c> the compile-time catalog no longer carries at all. Every existing test seeds a
/// registry row AND a matching catalog entry for the same pinned id; none seeds an orphan.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperEventTypeRenameTool.cs</code-under-test>
[NotInParallel("PostgreSQL")]
public class DapperEventTypeRenameToolCoverageTests : PostgresTestBase {

  // A type can be deleted from the compiled build entirely (not renamed — removed), leaving its
  // wh_message_type_registry row and every wh_event_store row it stamped still on disk. Detecting
  // that as a rename target would have nothing to rename it TO; treating it as drift and silently
  // dropping the row would erase provenance for real, historical events. The correct behavior is to
  // leave it alone, which is exactly what returning no PendingRename for it proves.
  [Test]
  public async Task DetectRenamesAsync_OrphanPinnedIdIsSkippedWhileARealRenameStillSurfacesAsync() {
    const string orphanPinnedId = "22222222-2222-2222-2222-222222222222";
    const string renamedPinnedId = "33333333-3333-3333-3333-333333333333";
    const string orphanedClr = "Orphaned.Namespace.RetiredEvent, TestApp";
    const string storedOldName = "Orders.Namespace.OrderPlaced, TestApp";
    const string currentName = "Orders.Namespace.OrderAccepted, TestApp";

    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await conn.ExecuteAsync(
        @"INSERT INTO wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at)
          VALUES (@Clr, @PinnedId::uuid, 'event', NOW())",
        new { Clr = orphanedClr, PinnedId = orphanPinnedId });
      await conn.ExecuteAsync(
        @"INSERT INTO wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at)
          VALUES (@Clr, @PinnedId::uuid, 'event', NOW())",
        new { Clr = storedOldName, PinnedId = renamedPinnedId });
    }

    // The catalog knows the SECOND pinned id (under a new name) and not the first at all.
    var catalog = new _catalogWith(renamedPinnedId, currentName);
    var tool = new DapperEventTypeRenameTool(catalog, ConnectionFactory);

    var detected = await tool.DetectRenamesAsync();

    // The positive half is what makes this test mean anything. Asserting only that the orphan
    // produces no rename passes just as well when the seeded rows were never visible to the tool
    // at all -- an empty result cannot distinguish "skipped it" from "never saw it". Requiring the
    // renamed row to surface proves the loop actually ran over the seeded rows.
    await Assert.That(detected).Count().IsEqualTo(1)
      .Because("the renamed row must be detected, which is what proves the tool saw the seeded "
             + "rows at all -- without it an empty result would pass vacuously");
    await Assert.That(detected[0].PinnedId).IsEqualTo(renamedPinnedId)
      .Because("only the row whose pinned id the catalog still carries can be renamed");
    await Assert.That(detected.Any(r => r.PinnedId == orphanPinnedId)).IsFalse()
      .Because("a type deleted from the build entirely has nothing to rename TO; treating it as "
             + "drift and dropping the row would erase provenance for real historical events");
  }

  /// <summary>A catalog carrying exactly one pinned id, under a name that has since changed.</summary>
  private sealed class _catalogWith(string pinnedId, string clrTypeName) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() =>
      [new MessageTypeCatalogEntry(typeof(object), clrTypeName, "event", pinnedId)];
  }
}
