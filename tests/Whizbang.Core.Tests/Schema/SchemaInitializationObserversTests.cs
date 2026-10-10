// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Schema;

/// <summary>
/// The EF Core schema pass reports a held schema lock through the host's observers; with no provider it reports to
/// nobody rather than failing the start.
/// </summary>
/// <docs>data/turnkey-initialization#watching-initialization</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaInitializationObservers.cs</code-under-test>
public class SchemaInitializationObserversTests {
  [Test]
  public async Task LockContended_TellsEveryRegisteredObserverAsync() {
    var first = new Recorder();
    var second = new Recorder();
    var services = new ServiceCollection();
    services.AddSingleton<ISchemaInitializationObserver>(first);
    services.AddSingleton<ISchemaInitializationObserver>(second);
    await using var provider = services.BuildServiceProvider();

    await SchemaInitializationObservers.LockContendedAsync(provider, "orders", CancellationToken.None);

    await Assert.That(first.Schemas).IsEquivalentTo(["orders"]);
    await Assert.That(second.Schemas).IsEquivalentTo(["orders"]);
  }

  [Test]
  public async Task LockContended_WithNoProvider_TellsNobodyAsync() {
    await Assert.That(async () => await SchemaInitializationObservers.LockContendedAsync(null, "orders", CancellationToken.None))
      .ThrowsNothing();
  }

  private sealed class Recorder : ISchemaInitializationObserver {
    public List<string> Schemas { get; } = [];

    public ValueTask OnSchemaLockContendedAsync(string schema, CancellationToken cancellationToken) {
      Schemas.Add(schema);
      return ValueTask.CompletedTask;
    }
  }
}
