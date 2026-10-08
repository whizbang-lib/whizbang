// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Naming;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Naming;

/// <summary>
/// A context that names no connection string reads <c>db</c>. Until its keys are renamed, a service
/// configured under the name earlier releases derived from the class keeps working: the derived name
/// is used when nothing is configured under <c>db</c>, and startup says to rename it.
/// </summary>
public class ConnectionStringNameFallbackTests {
  private static IConfiguration _config(params string[] keys) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(keys.Select(k => new KeyValuePair<string, string?>($"ConnectionStrings:{k}", "Host=h")))
      .Build();

  [Test]
  [Arguments("OrderServiceDbContext", "orderservice-db")]
  [Arguments("ChatDbContext", "chat-db")]
  [Arguments("Foo", "foo-db")]
  public async Task LegacyName_IsTheNameEarlierReleasesDerivedAsync(string className, string expected) {
    await Assert.That(WhizbangNamingConvention.LegacyConnectionStringName(className)).IsEqualTo(expected);
  }

  [Test]
  [Arguments("db")]
  [Arguments("db-direct")]
  [Arguments("db-init")]
  public async Task AnyDbKey_KeepsDbAsync(string configured) {
    var logged = new List<string>();

    var name = WhizbangNamingConvention.ResolveConnectionStringName(
      _config(configured, "orders-db"), "db", "orders-db", new ListLogger(logged));

    await Assert.That(name).IsEqualTo("db");
    await Assert.That(logged).IsEmpty();
  }

  [Test]
  [Arguments("orders-db")]
  [Arguments("orders-db-direct")]
  [Arguments("orders-db-init")]
  public async Task OnlyTheLegacyName_IsUsedAndReportedAsync(string configured) {
    var logged = new List<string>();

    var name = WhizbangNamingConvention.ResolveConnectionStringName(
      _config(configured), "db", "orders-db", new ListLogger(logged));

    await Assert.That(name).IsEqualTo("orders-db");
    await Assert.That(logged).Count().IsEqualTo(1);
    await Assert.That(logged[0]).Contains("orders-db").And.Contains("ConnectionStrings:db");
  }

  [Test]
  public async Task OnlyTheLegacyName_WithoutALogger_IsStillUsedAsync() {
    var name = WhizbangNamingConvention.ResolveConnectionStringName(_config("orders-db"), "db", "orders-db");

    await Assert.That(name).IsEqualTo("orders-db");
  }

  [Test]
  public async Task NeitherName_KeepsDbAsync() {
    var name = WhizbangNamingConvention.ResolveConnectionStringName(_config("other"), "db", "orders-db");

    await Assert.That(name).IsEqualTo("db");
  }

  [Test]
  public async Task NoLegacyName_KeepsTheNameAsync() {
    var name = WhizbangNamingConvention.ResolveConnectionStringName(_config("orders-db"), "reporting", legacyName: null);

    await Assert.That(name).IsEqualTo("reporting");
  }

  [Test]
  public async Task NullConfiguration_KeepsTheNameAsync() {
    var name = WhizbangNamingConvention.ResolveConnectionStringName(null, "db", "orders-db");

    await Assert.That(name).IsEqualTo("db");
  }

  private sealed class ListLogger(List<string> sink) : ILogger {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (IsEnabled(logLevel)) {
        sink.Add(formatter(state, exception));
      }
    }
  }
}
