// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Makes the in-memory executor a replay folds a model's fan-outs with (#2621), so a model a fan-out targets gets its
/// replay twin with no registration to remember, as <see cref="ICollectiveExecutorFactory"/> gives it its live executor.
/// The replay applier provides one; a fan-out entry calls it with its model through
/// <see cref="PerspectiveFanOutEntry.CreateInMemoryExecutor"/>, a typed lambda the generator emits, so no type is made at
/// run time.
/// </summary>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-on-rebuild-and-rewind</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/FanOutReplayApplierTests.cs:AFanOutModelWithNoRegisteredExecutor_GetsItsReplayTwinFromTheEntryAsync</tests>
public interface ICollectiveInMemoryExecutorFactory {
  /// <summary>The in-memory executor for <typeparamref name="TModel"/>.</summary>
  /// <typeparam name="TModel">The model the executor folds rows of.</typeparam>
  /// <returns>The executor.</returns>
  ICollectiveInMemoryExecutor Create<TModel>() where TModel : class;
}
