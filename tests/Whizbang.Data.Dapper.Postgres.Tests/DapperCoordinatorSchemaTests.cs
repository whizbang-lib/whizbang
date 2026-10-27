// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The schema the Dapper work coordinator qualifies its statements with, as the connection string configures it.
/// </summary>
/// <remarks>
/// The connection string's <c>Search Path</c> is configuration, so it is read from the string rather than from the
/// session: a pooled connection (PgBouncer in transaction mode, for one) does not carry the startup
/// <c>search_path</c> through to the server connection a statement lands on (#1508).
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperCoordinatorSchema.cs</code-under-test>
public class DapperCoordinatorSchemaTests {
  private const string BASE = "Host=localhost;Database=whizbang";

  [Test]
  [Arguments("svc", "svc")]
  [Arguments("svc, public", "svc")]
  [Arguments(" svc ,public", "svc")]
  [Arguments("Svc_Orders", "svc_orders")]
  [Arguments("\"Svc Orders\", public", "Svc Orders")]
  [Arguments("\"a\"\"b\"", "a\"b")]
  [Arguments("$user, svc", "svc")]
  [Arguments("\"$user\", public", "public")]
  [Arguments(", svc", "svc")]
  [Arguments("\"Svc", "\"svc")]
  public async Task TheFirstSchemaTheSearchPathNames_IsTheSchemaAsync(string searchPath, string expected) {
    var schema = DapperCoordinatorSchema.FromConnectionString($"{BASE};Search Path={_quoteForConnectionString(searchPath)}");

    await Assert.That(schema).IsEqualTo(expected)
      .Because("an unquoted name folds to lower case and a quoted one is taken literally, as the server resolves them");
  }

  [Test]
  [Arguments("")]
  [Arguments("$user")]
  [Arguments(" , ")]
  public async Task ASearchPathThatNamesNoSchema_ConfiguresNoneAsync(string searchPath) {
    var schema = DapperCoordinatorSchema.FromConnectionString($"{BASE};Search Path={_quoteForConnectionString(searchPath)}");

    await Assert.That(schema).IsNull();
  }

  [Test]
  public async Task AConnectionStringWithoutASearchPath_ConfiguresNoneAsync() {
    await Assert.That(DapperCoordinatorSchema.FromConnectionString(BASE)).IsNull()
      .Because("with nothing configured the coordinator asks the server once, which honors a role's or database's default");
  }

  // A connection-string value holding a quote, a comma or a semicolon has to be quoted itself; the builder's own
  // quoting rules are what the coordinator reads back, so the test writes the value the same way.
  private static string _quoteForConnectionString(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
