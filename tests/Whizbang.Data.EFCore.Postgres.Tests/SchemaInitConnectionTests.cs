// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The schema-initialization connection is found under the same name startup migrations read, so schema work
/// after startup runs on the same privileged role.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/SchemaInitConnection.cs</code-under-test>
[Category("Shard1")]
public class SchemaInitConnectionTests {
  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  [Test]
  public async Task FromConfiguration_ReadsTheInitSuffixOfTheContextsNameAsync() {
    var config = _config(
      ("ConnectionStrings:orders-db", "Host=pooler;Username=app"),
      ("ConnectionStrings:orders-db-init", "Host=primary;Username=owner"));

    var init = SchemaInitConnection.FromConfiguration(config, "orders-db", legacyConnectionStringName: null);

    await Assert.That(init.ConnectionString).IsEqualTo("Host=primary;Username=owner");
  }

  [Test]
  public async Task FromConfiguration_WithNoInitKey_IsNoneAsync() {
    var config = _config(("ConnectionStrings:orders-db", "Host=pooler;Username=app"));

    var init = SchemaInitConnection.FromConfiguration(config, "orders-db", legacyConnectionStringName: null);

    await Assert.That(init.ConnectionString).IsNull()
      .Because("without an -init key the work stays on the application's own connection, as before");
  }

  [Test]
  public async Task FromConfiguration_WhenOnlyTheLegacyNameIsConfigured_ReadsItsInitKeyAsync() {
    // Startup resolves the name the same way, so the two can never disagree about which role migrates.
    var config = _config(
      ("ConnectionStrings:OrdersDb", "Host=pooler;Username=app"),
      ("ConnectionStrings:OrdersDb-init", "Host=primary;Username=owner"));

    var init = SchemaInitConnection.FromConfiguration(config, "orders-db", legacyConnectionStringName: "OrdersDb");

    await Assert.That(init.ConnectionString).IsEqualTo("Host=primary;Username=owner");
  }

  [Test]
  public async Task FromConfiguration_WithoutConfiguration_IsNoneAsync() {
    var init = SchemaInitConnection.FromConfiguration(null, "orders-db", legacyConnectionStringName: null);

    await Assert.That(init.ConnectionString).IsNull();
  }

  [Test]
  public async Task FromConfiguration_RejectsAnEmptyNameAsync() {
    await Assert.That(() => SchemaInitConnection.FromConfiguration(_config(), "", null)).Throws<ArgumentException>();
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("   ")]
  public async Task ABlankConnectionString_IsNoneAsync(string? value) {
    await Assert.That(new SchemaInitConnection(value).ConnectionString).IsNull();
  }

  [Test]
  public async Task For_ReturnsTheConnectionRegisteredUnderTheContextTypeAsync() {
    var services = new ServiceCollection();
    services.AddKeyedSingleton<SchemaInitConnection>(typeof(string), new SchemaInitConnection("Host=primary"));
    await using var provider = services.BuildServiceProvider();

    await Assert.That(SchemaInitConnection.For(provider, typeof(string))).IsEqualTo("Host=primary");
    await Assert.That(SchemaInitConnection.For(provider, typeof(int))).IsNull()
      .Because("another context's registration is not this one's");
  }

  [Test]
  public async Task For_RejectsNullArgumentsAsync() {
    await using var provider = new ServiceCollection().BuildServiceProvider();

    await Assert.That(() => SchemaInitConnection.For(null!, typeof(string))).Throws<ArgumentNullException>();
    await Assert.That(() => SchemaInitConnection.For(provider, null!)).Throws<ArgumentNullException>();
  }
}
