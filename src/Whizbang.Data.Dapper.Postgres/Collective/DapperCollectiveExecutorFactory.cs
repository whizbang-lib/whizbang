// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;

namespace Whizbang.Data.Dapper.Postgres.Collective;

/// <summary>
/// Makes the Dapper executor of a model a perspective fan-out targets (#2620), on the table the generated fan-out entry
/// names, with the registration's apply policy, hooks and sibling tables, so a declared fan-out needs no
/// <c>AddCollectiveExecutorDapper&lt;TModel&gt;(table)</c>.
/// </summary>
/// <param name="tables">The shared Dapper table registry, for queries over sibling perspectives.</param>
/// <param name="options">The apply policy, or null for the default.</param>
/// <param name="loggerFactory">The logger factory, or null for no apply log.</param>
/// <param name="hooks">The collective apply hooks, or null for the framework's defaults.</param>
/// <docs>fundamentals/perspectives/multi-stream#how-a-fan-out-applies-live</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperFanOutApplyTests.cs:ARename_UpdatesTheCustomersOrders_AndLeavesAnotherCustomersAloneAsync</tests>
public sealed class DapperCollectiveExecutorFactory(
    DapperCollectiveTableRegistry tables, CollectiveApplyOptions? options, ILoggerFactory? loggerFactory,
    CollectiveApplyHookRegistry? hooks) : ICollectiveExecutorFactory {
  /// <inheritdoc />
  public ICollectiveEventExecutor Create<TModel>(string tableName) where TModel : class {
    tables.Add(typeof(TModel), tableName);
    return new DapperCollectiveEventExecutor<TModel>(
      tableName, tables.Tables, options, loggerFactory?.CreateLogger<DapperCollectiveEventExecutor<TModel>>(), hooks);
  }
}
