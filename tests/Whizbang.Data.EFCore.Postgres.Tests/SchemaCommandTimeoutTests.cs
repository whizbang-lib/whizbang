// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Schema initialization takes its command timeout from the initialization connection string
/// (<c>ConnectionStrings:&lt;name&gt;-init</c>) when that string sets one, and otherwise keeps its own
/// ten minutes: a table rewrite legitimately runs far longer than an ordinary command, so an
/// application's short query timeout must never reach it.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaCommandTimeout.cs</code-under-test>
[Category("Shard3")]
public class SchemaCommandTimeoutTests {
  [Test]
  [Arguments("Host=db;Command Timeout=900", 900)]
  [Arguments("Host=db;CommandTimeout=1200", 1200)]
  [Arguments("Host=db;command timeout=45", 45)]
  [Arguments("Host=db;Command Timeout=0", 0)]
  public async Task AnInitStringThatSetsATimeout_DecidesItAsync(string initConnectionString, int expected) {
    await Assert.That(SchemaCommandTimeout.Resolve(initConnectionString)).IsEqualTo(expected);
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("Host=db;Port=5432")]
  [Arguments("Host=db;Command Timeout=soon")]
  [Arguments("Host=db;Command Timeout=-5")]
  [Arguments("not a connection string")]
  public async Task OtherwiseTheSchemaKeepsItsOwnTenMinutesAsync(string? initConnectionString) {
    await Assert.That(SchemaCommandTimeout.Resolve(initConnectionString)).IsEqualTo(600);
  }
}
