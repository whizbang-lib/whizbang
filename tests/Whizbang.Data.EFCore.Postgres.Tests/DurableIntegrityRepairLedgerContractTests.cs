// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The repair-ladder contract (<see cref="IntegrityRepairLedgerContractTests"/>) against the durable ledger: a
/// <see cref="CoordinatorIntegrityRepairLedger"/> over a real database, one per test. The in-memory fallback runs the
/// same cases in <c>InMemoryIntegrityRepairLedgerContractTests</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/CoordinatorIntegrityRepairLedger.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/320_RepairWaitsForItsAnswer.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
[InheritsTests]
public sealed class DurableIntegrityRepairLedgerContractTests : IntegrityRepairLedgerContractTests, IAsyncDisposable {
  private readonly Database _database = new();
  private readonly List<WorkCoordinationDbContext> _contexts = [];
  private ServiceProvider? _services;

  [Before(Test)]
  public Task SetupDatabaseAsync() => _database.SetupAsync();

  /// <summary>Disposes the test's services and contexts, then drops its database (TUnit disposes each test's instance).</summary>
  public async ValueTask DisposeAsync() {
    if (_services is not null) {
      await _services.DisposeAsync();
    }
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
    await _database.DisposeAsync();
  }

  /// <inheritdoc />
  protected override Task<IIntegrityRepairLedger> CreateLedgerAsync() {
    _services = new ServiceCollection()
      .AddScoped<IWorkCoordinator>(_ => {
        var ctx = _database.NewContext();
        _contexts.Add(ctx);
        return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
      })
      .BuildServiceProvider();
    return Task.FromResult<IIntegrityRepairLedger>(
      new CoordinatorIntegrityRepairLedger(_services.GetRequiredService<IServiceScopeFactory>()));
  }

  /// <summary>A database of its own per test, set up and torn down by this class's hooks.</summary>
  private sealed class Database : EFCoreTestBase {
    public WorkCoordinationDbContext NewContext() => CreateDbContext();
  }
}
