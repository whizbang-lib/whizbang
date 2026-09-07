using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="PostgresMigrationProvider.GetMigration"/>'s not-found branch — no
/// existing test asks for a script name that isn't an embedded resource. Reading embedded resource
/// names is pure assembly reflection over compiled-in resources, not a database call. No database
/// is used in this file.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresMigrationProvider.cs</code-under-test>
public class PostgresMigrationProviderCoverageTests {

  // Schema tooling (e.g. a CLI asking "show me migration X") depends on null meaning "this
  // migration doesn't exist" rather than throwing or returning a stale/wrong script — a caller
  // that doesn't check for null would otherwise silently apply the wrong SQL.
  [Test]
  public async Task GetMigration_UnknownScriptName_ReturnsNullAsync() {
    var provider = new PostgresMigrationProvider();

    var result = provider.GetMigration("this_script_does_not_exist");

    await Assert.That(result).IsNull()
      .Because("a script name with no matching embedded resource must report \"not found\" via "
             + "null, not throw or fabricate a result");
  }
}
