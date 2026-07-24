using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Sqlite;

namespace Whizbang.Data.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Direct tests for <see cref="SqliteGuidHandler"/> — the Dapper type
/// handler that round-trips <see cref="Guid"/> values through SQLite's
/// TEXT-only Guid representation. Covers <c>Parse</c>'s three branches
/// (string, Guid, throw-on-other), <c>SetValue</c>'s parameter-write
/// path, and the <c>Register</c> static initializer.
/// </summary>
/// <docs>data/dapper/sqlite-type-handlers</docs>
public class SqliteGuidHandlerTests {

  [Test]
  public async Task Parse_StringValue_ReturnsGuidAsync() {
    var handler = new SqliteGuidHandler();
    var expected = Guid.NewGuid();

    var result = handler.Parse(expected.ToString());

    await Assert.That(result).IsEqualTo(expected);
  }

  [Test]
  public async Task Parse_GuidValue_ReturnsSameGuidAsync() {
    var handler = new SqliteGuidHandler();
    var expected = Guid.NewGuid();

    var result = handler.Parse(expected);

    await Assert.That(result).IsEqualTo(expected);
  }

  [Test]
  public async Task Parse_NonStringNonGuidValue_ThrowsInvalidCastExceptionAsync() {
    var handler = new SqliteGuidHandler();

    await Assert.That(() => handler.Parse(42))
      .Throws<InvalidCastException>();
  }

  [Test]
  public async Task SetValue_WritesGuidAsLowercaseStringToParameterAsync() {
    var handler = new SqliteGuidHandler();
    var guid = Guid.NewGuid();
    var parameter = new SqliteParameter();

    handler.SetValue(parameter, guid);

    // The handler stores Guid as its lowercase string representation —
    // round-trips cleanly through Guid.Parse.
    await Assert.That(parameter.Value).IsTypeOf<string>();
    await Assert.That((string)parameter.Value!).IsEqualTo(guid.ToString());
  }

  [Test]
  public async Task Register_AddsTypeHandlerToDapperGlobalRegistryAsync() {
    // Calling Register is idempotent at the test level — Dapper accepts
    // multiple registrations. Just verify the call doesn't throw and the
    // handler subsequently works for a round-trip query.
    SqliteGuidHandler.Register();

    using var connection = new SqliteConnection("DataSource=:memory:");
    connection.Open();
    connection.Execute("CREATE TABLE t (id TEXT)");

    var input = Guid.NewGuid();
    connection.Execute("INSERT INTO t (id) VALUES (@Id)", new { Id = input });

    var output = connection.QuerySingle<Guid>("SELECT id FROM t");
    await Assert.That(output).IsEqualTo(input);
  }
}
