using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// That a perspective table this release creates is recorded in the microsecond form, settled,
/// the moment it is created.
/// </summary>
/// <remarks>
/// A fresh table has nothing to convert, and a ledger that did not say so would have the rewrite
/// scan it on the next start to find that out. Recorded at creation and settled, it is never
/// scanned. The initializer under test is the generated one, run against a fresh database the way
/// every test in this base class runs it.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class FreshTableFormTests : EFCoreTestBase {
  /// <summary>Every perspective table the initializer created has a settled ledger row at form 2.</summary>
  [Test]
  public async Task ATableTheInitializerCreatedIsSettledAtTheMicrosecondFormAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
      "SELECT temporal_form::text || '/' || CASE WHEN settled_at IS NULL THEN 'open' ELSE 'settled' END "
      + "FROM wh_perspective_forms WHERE table_name = 'wh_per_order'", connection);

    await Assert.That((string?)await command.ExecuteScalarAsync()).IsEqualTo("2/settled")
      .Because("a table created by this release holds nothing in an older form, and saying so at "
        + "creation is what keeps the rewrite from ever scanning it");
  }

  /// <summary>Running the initializer again neither duplicates nor reopens the row.</summary>
  [Test]
  public async Task RunningTheInitializerAgainLeavesTheRowAloneAsync() {
    await using (var context = CreateDbContext()) {
      await context.EnsureWhizbangDatabaseInitializedAsync();
    }

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
      "SELECT count(*)::text || '/' || min(temporal_form)::text || '/' || count(settled_at)::text "
      + "FROM wh_perspective_forms WHERE table_name = 'wh_per_order'", connection);

    await Assert.That((string?)await command.ExecuteScalarAsync()).IsEqualTo("1/2/1");
  }
}
