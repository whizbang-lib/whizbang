// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Tests.Helpers;

/// <summary>
/// Measures what a call allocates because of a cache the runtime keeps behind a weak handle, such as the one behind
/// <see cref="Type.Name"/> (#2548). The call runs a number of times with a full collection before each run, and as many
/// times without one; the difference is what the collections cost it. A path that reads a type's name only when a log
/// level is enabled costs nothing extra with the level off, while one that reads it eagerly rebuilds the name after
/// every collection, so the difference is positive on every run. Nothing here depends on timing: the collections are
/// forced, and everything else the call allocates is the same with or without them.
/// </summary>
internal static class WeakNameCacheProbe {
  /// <summary>How many runs each half of the measurement makes.</summary>
  public const int RUNS = 25;

  /// <summary>
  /// The bytes the call allocated on this thread over <see cref="RUNS"/> runs with a collection before each, minus the
  /// bytes over as many runs without; zero when no weak cache is rebuilt.
  /// </summary>
  /// <param name="call">The call, run synchronously on the measuring thread.</param>
  /// <returns>The extra bytes the collections cost the call.</returns>
  public static long ExtraBytesAfterCollections(Action call) {
    call();
    call();
    var withoutCollections = _measure(call, collect: false);
    var withCollections = _measure(call, collect: true);
    return withCollections - withoutCollections;
  }

  /// <summary>Consumes <paramref name="valueTask"/>, which must already have completed successfully on this thread.</summary>
  /// <param name="valueTask">A value task that completed synchronously.</param>
  public static void RunSynchronously(ValueTask valueTask) {
    if (!valueTask.IsCompletedSuccessfully) {
      throw new InvalidOperationException("The measured call did not complete synchronously and successfully, so its allocations are not all on this thread.");
    }
    valueTask.GetAwaiter().GetResult();
  }

  private static long _measure(Action call, bool collect) {
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < RUNS; i++) {
      if (collect) {
        GC.Collect();
      }
      call();
    }
    return GC.GetAllocatedBytesForCurrentThread() - before;
  }
}
