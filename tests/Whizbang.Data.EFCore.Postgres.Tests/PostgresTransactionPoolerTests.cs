// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2347: a connection that goes through a transaction-mode pooler says so with
/// <c>Whizbang:Postgres:&lt;connection&gt;:TransactionPooler</c>, and the driver then stops resetting the session
/// (<c>DISCARD ALL</c>) each time a connection returns to its pool: the pooler hands out a different server session
/// per transaction, so the reset only costs a statement. Off by default: against a direct server the reset is what
/// keeps one borrower's session state from leaking to the next.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresTransactionPooler.cs</code-under-test>
/// <docs>operations/configuration/configuration-reference#transaction-mode-poolers</docs>
[Category("Shard3")]
public class PostgresTransactionPoolerTests {
  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  [Test]
  [Arguments("db")]
  [Arguments("reporting")]
  public async Task AConnectionMarkedAsBehindAPooler_StopsTheResetOnCloseAsync(string connection) {
    var config = _config(($"Whizbang:Postgres:{connection}:TransactionPooler", "true"));
    var builder = new NpgsqlConnectionStringBuilder("Host=pooler;Database=app");

    PostgresTransactionPooler.Apply(config, connection, builder);

    await Assert.That(PostgresTransactionPooler.IsConfigured(config, connection)).IsTrue();
    await Assert.That(builder.NoResetOnClose).IsTrue();
    await Assert.That(builder.MaxAutoPrepare).IsEqualTo(0).Because("auto-prepare stays off behind a pooler");
  }

  /// <summary>The default: unset, false, unreadable, another connection's key or no configuration resets as Npgsql does.</summary>
  [Test]
  [Arguments(null)]
  [Arguments("false")]
  [Arguments("sometimes")]
  public async Task WithoutTheKey_TheSessionIsResetAsBeforeAsync(string? value) {
    var config = value is null
      ? _config(("Whizbang:Postgres:other:TransactionPooler", "true"))
      : _config(("Whizbang:Postgres:db:TransactionPooler", value));
    var builder = new NpgsqlConnectionStringBuilder("Host=db;Database=app");

    PostgresTransactionPooler.Apply(config, "db", builder);
    PostgresTransactionPooler.Apply(null, "db", builder);

    await Assert.That(PostgresTransactionPooler.IsConfigured(config, "db")).IsFalse();
    await Assert.That(PostgresTransactionPooler.IsConfigured(null, "db")).IsFalse();
    await Assert.That(builder.NoResetOnClose).IsFalse();
  }

  [Test]
  public async Task Apply_RefusesNoBuilderAsync() =>
    await Assert.That(() => PostgresTransactionPooler.Apply(null, "db", null!)).Throws<ArgumentNullException>();
}
