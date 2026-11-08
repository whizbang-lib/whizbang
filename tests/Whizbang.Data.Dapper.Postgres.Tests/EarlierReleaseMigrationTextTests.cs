// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The EF Core driver's text as releases before #1681 rendered it, kept only so the hash upgrade recognizes what they
/// recorded (#2125). Each test pins one behavior of the earlier qualifier that today's qualifier no longer has, or
/// one it shares; the EF Core upgrade test checks the whole against hashes v0.2623.1 really recorded.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/EarlierReleaseMigrationText.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#what-counts-as-a-change</docs>
[Category("Unit")]
public class EarlierReleaseMigrationTextTests {
  /// <summary>The public schema is substituted and not qualified: comments keep bare names.</summary>
  [Test]
  public async Task EFCore_PublicSchema_SubstitutesWithoutQualifyingAsync() =>
    await Assert.That(EarlierReleaseMigrationText.EFCore("SELECT 1 FROM __MIGRATION_SCHEMA__.wh_inbox; -- wh_inbox", "public"))
      .IsEqualTo("SELECT 1 FROM \"public\".wh_inbox; -- wh_inbox");

  /// <summary>An empty schema is the public schema.</summary>
  [Test]
  public async Task EFCore_EmptySchema_IsPublicAsync() =>
    await Assert.That(EarlierReleaseMigrationText.EFCore("SELECT 1 FROM wh_outbox", ""))
      .IsEqualTo("SELECT 1 FROM wh_outbox");

  /// <summary>The migration constants are substituted, as the driver substituted them.</summary>
  [Test]
  public async Task EFCore_SubstitutesTheMigrationConstantsAsync() =>
    await Assert.That(EarlierReleaseMigrationText.EFCore("SELECT __PRIORITY_BACKGROUND__", "public"))
      .IsEqualTo(MigrationConstants.Apply("SELECT __PRIORITY_BACKGROUND__"));

  /// <summary>
  /// A named schema qualifies bare table names everywhere, comments and string literals included: the behavior that
  /// makes the earlier text differ from today's.
  /// </summary>
  [Test]
  public async Task EFCore_NamedSchema_QualifiesBareTableNamesInCommentsAndLiteralsTooAsync() =>
    await Assert.That(EarlierReleaseMigrationText.EFCore(
        "-- reads wh_inbox\nSELECT 'wh_outbox' FROM wh_event_store, __MIGRATION_SCHEMA__.wh_inbox, wh_inbox_id;", "svc"))
      .IsEqualTo("-- reads \"svc\".wh_inbox\nSELECT '\"svc\".wh_outbox' FROM \"svc\".wh_event_store, \"svc\".wh_inbox, wh_inbox_id;");

  /// <summary>A RENAME TO target stays unqualified, as PostgreSQL requires.</summary>
  [Test]
  public async Task EFCore_NamedSchema_LeavesARenameTargetUnqualifiedAsync() =>
    await Assert.That(EarlierReleaseMigrationText.EFCore("ALTER TABLE wh_sequences RENAME TO wh_active_streams;", "svc"))
      .IsEqualTo("ALTER TABLE \"svc\".wh_sequences RENAME TO wh_active_streams;");

  /// <summary>Functions the earlier list named are qualified where created, dropped, granted and commented on.</summary>
  [Test]
  public async Task EFCore_NamedSchema_QualifiesTheEarlierFunctionsAsync() {
    const string SQL = """
      CREATE OR REPLACE FUNCTION perform_maintenance() RETURNS void AS 'x';
      DROP FUNCTION IF EXISTS process_work_batch;
      GRANT EXECUTE ON FUNCTION reconcile_perspective_registry TO someone;
      COMMENT ON FUNCTION register_message_associations IS 'x';
      SELECT other_function();
      """;

    await Assert.That(EarlierReleaseMigrationText.EFCore(SQL, "svc")).IsEqualTo("""
      CREATE OR REPLACE FUNCTION "svc".perform_maintenance() RETURNS void AS 'x';
      DROP FUNCTION IF EXISTS "svc".process_work_batch;
      GRANT EXECUTE ON FUNCTION "svc".reconcile_perspective_registry TO someone;
      COMMENT ON FUNCTION "svc".register_message_associations IS 'x';
      SELECT other_function();
      """);
  }

  /// <summary>The text is required.</summary>
  [Test]
  public async Task EFCore_NullText_ThrowsAsync() =>
    await Assert.That(() => EarlierReleaseMigrationText.EFCore(null!, "svc")).Throws<ArgumentNullException>();
}
