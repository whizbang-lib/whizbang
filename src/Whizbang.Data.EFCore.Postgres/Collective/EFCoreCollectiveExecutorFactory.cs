// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// Makes the EF Core executor of a model a perspective fan-out targets (#2620), with the registration's apply policy and
/// hooks, so a declared fan-out needs no <c>AddCollectiveExecutorEFCore&lt;TModel&gt;()</c>. EF Core finds the model's
/// table from the context's model, so the table name is not needed.
/// </summary>
/// <param name="options">The apply policy, or null for the default.</param>
/// <param name="hooks">The collective apply hooks, or null for the framework's defaults.</param>
/// <docs>fundamentals/perspectives/multi-stream#how-a-fan-out-applies-live</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/FanOutApplyEFCoreTests.cs:ARename_UpdatesTheCustomersOrders_AndLeavesAnotherCustomersAloneAsync</tests>
public sealed class EFCoreCollectiveExecutorFactory(CollectiveApplyOptions? options, CollectiveApplyHookRegistry? hooks)
    : ICollectiveExecutorFactory {
  /// <inheritdoc />
  public ICollectiveEventExecutor Create<TModel>(string tableName) where TModel : class =>
    new EFCoreCollectiveEventExecutor<TModel>(options, hooks);
}
