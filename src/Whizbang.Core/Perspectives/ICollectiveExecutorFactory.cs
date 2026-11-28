// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Makes a driver's set-based executor for a model (#2620), so the collective dispatcher can give every model a
/// fan-out targets its executor without a registration to remember. Each driver's collective registration provides one;
/// a fan-out entry calls it with its model through <see cref="PerspectiveFanOutEntry.CreateExecutor"/>, a typed lambda
/// the generator emits, so no type is made at run time.
/// </summary>
/// <docs>fundamentals/perspectives/multi-stream#how-a-fan-out-applies-live</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutWiringTests.cs:ForDispatch_CreatesOneExecutorPerFanOutModelThatHasNone_AndKeepsTheRegisteredOnesAsync</tests>
public interface ICollectiveExecutorFactory {
  /// <summary>The driver's executor for <typeparamref name="TModel"/>.</summary>
  /// <typeparam name="TModel">The model the executor updates rows of.</typeparam>
  /// <param name="tableName">The model's perspective table, for a driver that addresses tables by name.</param>
  /// <returns>The executor.</returns>
  ICollectiveEventExecutor Create<TModel>(string tableName) where TModel : class;
}
