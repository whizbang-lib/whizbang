// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2620: a fan-out reaches its rows through the perspective pipeline's associations step and the perspective worker,
/// which only the EF Core Postgres driver provides. A Dapper host has neither, so a fan-out declared there would never
/// change a row. Its schema initialization refuses the host before it touches the database, naming the fan-outs and
/// the driver that runs them.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperSchemaInitializationRunner.cs</code-under-test>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-design-choices</docs>
public class DapperFanOutRefusalTests {
  [Test]
  [Timeout(60000)]
  public async Task ADapperHostThatDeclaresAFanOut_IsRefusedAtSchemaInitialization_BeforeItConnectsAsync(CancellationToken cancellationToken) {
    var services = new ServiceCollection()
      .AddSingleton<IPerspectiveFanOutRegistry>(new Listed([new PerspectiveFanOutEntry(
        typeof(object), typeof(RefusedOrder), typeof(RefusedRenamed), "CustomerId", "customer_id", static (_, _) => null!,
        (Expression<Func<RefusedOrder, object?>>)(row => row.CustomerId))]))
      .BuildServiceProvider();
    // No server listens here: a runner that got past the refusal would fail to connect instead.
    const string unreachable = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1";
    var runner = new DapperSchemaInitializationRunner(
      unreachable, new PostgresOptions(), () => throw new InvalidOperationException("never initialized"),
      NullLogger<DapperSchemaInitializationRunner>.Instance, services);

    var refused = await Assert.That(async () => await runner.RunAsync(cancellationToken)).Throws<InvalidOperationException>();

    await Assert.That(refused!.Message).Contains(nameof(RefusedOrder));
    await Assert.That(refused.Message).Contains("EF Core");
  }

  [Test]
  public async Task ADapperHostWithNoFanOut_IsNotRefusedForOneAsync() {
    await using var services = new ServiceCollection().BuildServiceProvider();

    await Assert.That(DapperSchemaInitializationRunner.FanOutRefusal(services)).IsNull();
    await Assert.That(DapperSchemaInitializationRunner.FanOutRefusal(null)).IsNull();
  }

  internal sealed class RefusedOrder {
    public Guid CustomerId { get; init; }
  }

  internal sealed record RefusedRenamed : IEvent;

  private sealed class Listed(IReadOnlyList<PerspectiveFanOutEntry> entries) : IPerspectiveFanOutRegistry {
    public IReadOnlyList<PerspectiveFanOutEntry> Entries => entries;
  }
}
