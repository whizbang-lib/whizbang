// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.PostgresExtensions;

/// <summary>
/// #1753: a libpq client program (pg_repack) is given its connection through the environment variables libpq reads,
/// never through its arguments, which any user on the host can list. The connection string is read with Npgsql's own
/// parser, so every synonym it accepts maps the same way.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/LibpqEnvironment.cs</code-under-test>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
public sealed class LibpqEnvironmentTests {
  [Test]
  public async Task EverySetting_MapsToItsLibpqVariableAsync() {
    var environment = LibpqEnvironment.FromConnectionString(
      "Server=db.example;Port=6432;Database=orders;User Id=owner;Password=pw;Passfile=/run/pgpass;SSL Mode=VerifyFull;"
      + "Root Certificate=/certs/root.crt;SSL Certificate=/certs/client.crt;SSL Key=/certs/client.key;"
      + "Application Name=orders-api;Timeout=7");

    await Assert.That(environment).IsEquivalentTo(new Dictionary<string, string> {
      ["PGHOST"] = "db.example",
      ["PGPORT"] = "6432",
      ["PGDATABASE"] = "orders",
      ["PGUSER"] = "owner",
      ["PGPASSWORD"] = "pw",
      ["PGPASSFILE"] = "/run/pgpass",
      ["PGSSLMODE"] = "verify-full",
      ["PGSSLROOTCERT"] = "/certs/root.crt",
      ["PGSSLCERT"] = "/certs/client.crt",
      ["PGSSLKEY"] = "/certs/client.key",
      ["PGAPPNAME"] = "orders-api",
      ["PGCONNECT_TIMEOUT"] = "7",
    });
  }

  [Test]
  public async Task UnsetSettings_AreLeftToLibpqsOwnDefaultsAsync() {
    var environment = LibpqEnvironment.FromConnectionString("Host=localhost;Database=orders;Username=owner");

    await Assert.That(environment.ContainsKey("PGPASSWORD")).IsFalse()
      .Because("with no password in the string libpq looks in the host's own password file or service file");
    await Assert.That(environment.ContainsKey("PGPASSFILE")).IsFalse();
    await Assert.That(environment.ContainsKey("PGSSLROOTCERT")).IsFalse();
    await Assert.That(environment.ContainsKey("PGSSLCERT")).IsFalse();
    await Assert.That(environment.ContainsKey("PGSSLKEY")).IsFalse();
    await Assert.That(environment.ContainsKey("PGAPPNAME")).IsFalse();
    await Assert.That(environment["PGHOST"]).IsEqualTo("localhost");
    await Assert.That(environment["PGPORT"]).IsEqualTo("5432");
    await Assert.That(environment["PGSSLMODE"]).IsEqualTo("prefer").Because("Npgsql's default, the same as libpq's");
  }

  [Test]
  public async Task NoHostOrDatabaseOrUser_SetsNoneOfThemAsync() {
    var environment = LibpqEnvironment.FromConnectionString("Port=5432");

    await Assert.That(environment.ContainsKey("PGHOST")).IsFalse();
    await Assert.That(environment.ContainsKey("PGDATABASE")).IsFalse();
    await Assert.That(environment.ContainsKey("PGUSER")).IsFalse();
  }

  [Test]
  [Arguments("Disable", "disable")]
  [Arguments("Allow", "allow")]
  [Arguments("Prefer", "prefer")]
  [Arguments("Require", "require")]
  [Arguments("VerifyCA", "verify-ca")]
  [Arguments("VerifyFull", "verify-full")]
  public async Task SslMode_IsSpelledAsLibpqSpellsItAsync(string npgsql, string libpq) {
    var environment = LibpqEnvironment.FromConnectionString($"Host=h;SSL Mode={npgsql}");

    await Assert.That(environment["PGSSLMODE"]).IsEqualTo(libpq);
  }

  [Test]
  public async Task NullConnectionString_IsRefusedAsync() {
    await Assert.That(() => LibpqEnvironment.FromConnectionString(null!)).Throws<ArgumentNullException>();
  }
}
