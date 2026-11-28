// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The executors the collective dispatcher runs (#2620): the registered ones, then one for each model a perspective fan-out
/// targets that has none, made by the driver's <see cref="ICollectiveExecutorFactory"/>. A declared fan-out therefore
/// needs no executor registration; one registered for the model still wins.
/// </summary>
/// <docs>fundamentals/perspectives/multi-stream#how-a-fan-out-applies-live</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutWiringTests.cs</tests>
public static class CollectiveExecutors {
  /// <summary>The registered executors, then one per fan-out model that has none.</summary>
  /// <param name="registered">The executors registered in the container.</param>
  /// <param name="fanOuts">The host's fan-outs.</param>
  /// <param name="factory">The driver's executor factory.</param>
  /// <returns>Every executor the dispatcher runs, one per model.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutWiringTests.cs:ForDispatch_CreatesOneExecutorPerFanOutModelThatHasNone_AndKeepsTheRegisteredOnesAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutWiringTests.cs:ForDispatch_AFanOutWithNoFactoryLambda_GetsNoExecutorAsync</tests>
  public static IReadOnlyList<ICollectiveEventExecutor> ForDispatch(
      IReadOnlyList<ICollectiveEventExecutor> registered, IReadOnlyList<PerspectiveFanOutEntry> fanOuts,
      ICollectiveExecutorFactory factory) {
    ArgumentNullException.ThrowIfNull(registered);
    ArgumentNullException.ThrowIfNull(fanOuts);
    ArgumentNullException.ThrowIfNull(factory);
    var executors = new List<ICollectiveEventExecutor>(registered);
    var models = new HashSet<Type>(registered.Select(e => e.ModelType));
    foreach (var fanOut in fanOuts) {
      if (fanOut.CreateExecutor is { } create && models.Add(fanOut.ModelType)) {
        executors.Add(create(factory));
      }
    }
    return executors;
  }
}
