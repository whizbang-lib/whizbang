// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Containers;

/// <summary>
/// The connection a fixture runs its own setup on carries a hang guard instead of the default command timeout, so
/// how busy the shared server is decides how long setup takes and not whether it succeeds (#1940, #1941).
/// </summary>
/// <remarks>
/// A containment fixture seeded 200,000 rows on a connection with Npgsql's default 30-second command timeout.
/// With another shard loading the same container the client cancelled the insert, and four of its tests failed
/// in setup. These tests pin the policy that replaced it: the test's own timeout bounds a setup statement through
/// its token, and a guard far above any measured wait catches a hang.
/// </remarks>
[Category("Unit")]
[Category("Shard1")]
public class PerTestDatabaseSetupPolicyTests {
  private const string CONNECTION_STRING =
    "Host=localhost;Port=5432;Database=fixture_db;Username=whizbang_user;Command Timeout=30;Timezone=UTC";

  [Test]
  public async Task ForSetup_ReplacesTheCommandTimeoutWithTheHangGuardAsync() {
    var setup = new NpgsqlConnectionStringBuilder(PerTestDatabaseFactory.ForSetup(CONNECTION_STRING));

    await Assert.That(setup.CommandTimeout).IsEqualTo(PerTestDatabaseFactory.SETUP_STATEMENT_GUARD_SECONDS)
      .Because("a setup statement waits as long as the shared server's load makes it, up to a guard that only a hang reaches");
  }

  [Test]
  public async Task ForSetup_KeepsEverythingElseAboutTheConnectionAsync() {
    var original = new NpgsqlConnectionStringBuilder(CONNECTION_STRING);
    var setup = new NpgsqlConnectionStringBuilder(PerTestDatabaseFactory.ForSetup(CONNECTION_STRING));

    await Assert.That(setup.Host).IsEqualTo(original.Host);
    await Assert.That(setup.Port).IsEqualTo(original.Port);
    await Assert.That(setup.Database).IsEqualTo(original.Database)
      .Because("the setup runs against the fixture's own database, not the server's default one");
    await Assert.That(setup.Username).IsEqualTo(original.Username);
    await Assert.That(setup.Timezone).IsEqualTo(original.Timezone);
  }

  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task ForSetup_WithoutAConnectionString_ThrowsAsync(string connectionString) {
    await Assert.That(() => PerTestDatabaseFactory.ForSetup(connectionString)).Throws<ArgumentException>();
  }

  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task CreateFromTemplate_WithoutATemplate_ThrowsBeforeCreatingAnythingAsync(string template) {
    await Assert.That(async () => await PerTestDatabaseFactory.CreateFromTemplateAsync("tpl", template))
      .Throws<ArgumentException>()
      .Because("a blank template would read as CREATE DATABASE without one, a silently empty copy");
  }
}
