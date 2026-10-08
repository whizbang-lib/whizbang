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
/// Each connection has its own command timeout, keyed by the connection's name the same way its
/// connection string is: <c>Whizbang:Postgres:db:CommandTimeoutSeconds</c>, <c>…:db-direct:…</c>,
/// <c>…:db-init:…</c>. The key wins over a <c>Command Timeout</c> written in the string.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresCommandTimeouts.cs</code-under-test>
[Category("Shard3")]
public class PostgresCommandTimeoutsTests {
  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  [Test]
  [Arguments("db")]
  [Arguments("db-direct")]
  [Arguments("db-init")]
  [Arguments("reporting-direct")]
  public async Task EachConnection_ReadsItsOwnKeyAsync(string connection) {
    var config = _config(($"Whizbang:Postgres:{connection}:CommandTimeoutSeconds", "77"),
                         ("Whizbang:Postgres:other:CommandTimeoutSeconds", "5"));

    await Assert.That(PostgresCommandTimeouts.Configured(config, connection)).IsEqualTo(77);
  }

  [Test]
  [Arguments(null)]
  [Arguments("soon")]
  [Arguments("-1")]
  public async Task AbsentOrUnreadable_IsNotConfiguredAsync(string? value) {
    var config = value is null ? _config() : _config(("Whizbang:Postgres:db:CommandTimeoutSeconds", value));

    await Assert.That(PostgresCommandTimeouts.Configured(config, "db")).IsNull();
    await Assert.That(PostgresCommandTimeouts.Configured(null, "db")).IsNull();
  }

  [Test]
  public async Task Apply_TheKeyWinsOverTheStringAsync() {
    var builder = new NpgsqlConnectionStringBuilder("Host=h;Command Timeout=15");

    PostgresCommandTimeouts.Apply(_config(("Whizbang:Postgres:db:CommandTimeoutSeconds", "90")), "db", builder);

    await Assert.That(builder.CommandTimeout).IsEqualTo(90);
  }

  [Test]
  public async Task Apply_WithoutAKey_KeepsTheStringsValueAsync() {
    var builder = new NpgsqlConnectionStringBuilder("Host=h;Command Timeout=15");

    PostgresCommandTimeouts.Apply(_config(), "db", builder);

    await Assert.That(builder.CommandTimeout).IsEqualTo(15);
  }

  [Test]
  public async Task ApplyTo_RewritesAConnectionStringAsync() {
    var config = _config(("Whizbang:Postgres:db-direct:CommandTimeoutSeconds", "45"));

    var rewritten = PostgresCommandTimeouts.ApplyTo(config, "db-direct", "Host=h");
    var untouched = PostgresCommandTimeouts.ApplyTo(config, "db", "Host=h");

    await Assert.That(new NpgsqlConnectionStringBuilder(rewritten).CommandTimeout).IsEqualTo(45);
    await Assert.That(untouched).IsEqualTo("Host=h");
  }
}
