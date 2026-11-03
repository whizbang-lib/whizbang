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
[NotInParallel("EFCorePostgresTests")]
[InheritsTests]
public class DurableIntegrityRepairLedgerContractTests : IntegrityRepairLedgerContractTests {
  private readonly Database _database = new();
  private readonly List<WorkCoordinationDbContext> _contexts = [];
  private ServiceProvider? _services;

  [Before(Test)]
  public Task SetupDatabaseAsync() => _database.SetupAsync();

  [After(Test)]
  public async Task TeardownDatabaseAsync() {
    if (_services is not null) {
      await _services.DisposeAsync();
    }
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
    await _database.TeardownAsync();
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
