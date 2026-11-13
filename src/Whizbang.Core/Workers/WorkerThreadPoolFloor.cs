// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// Establishes a reserve of ready thread-pool worker threads so Whizbang's background workers
/// cannot starve the host's own request pipeline — in particular its liveness endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Whizbang schedules its workers (drain, dispatch, audit, maintenance) on the host's thread pool,
/// so the load that starves the pool is Whizbang's. In a CPU-limited container the runtime seeds
/// the pool's minimum worker count from the CPU allowance — a ~1.7-core limit yields 2 — and past
/// that minimum it injects new threads at only one or two per second. A burst of asynchronous
/// database completions therefore queues ahead of everything else, including a health endpoint
/// that performs no I/O at all.
/// </para>
/// <para>
/// Observed live: pods were SIGKILLed about every two minutes (50–66 restarts overnight) because a
/// trivial liveness endpoint could not be answered within a 10-second probe timeout. Each restart
/// re-ran the backlog that caused the starvation, so the kill prevented the recovery it was meant
/// to trigger. <see cref="InboxDispatchWorker"/> already documented the remedy — raise the pool
/// minimum at the host — but leaving that to every consumer means every consumer learns it from an
/// outage. Registering the worker pipeline now establishes the floor itself.
/// </para>
/// <para>The floor is only ever RAISED. A host or operator that tuned the pool higher keeps it.</para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/WorkerThreadPoolFloorTests.cs</tests>
public static class WorkerThreadPoolFloor {

  private const int DEFAULT_FLOOR_VALUE = 64;
  private const int THREADS_PER_PROCESSOR_VALUE = 8;

  /// <summary>Minimum ready worker threads, however small the CPU allowance is.</summary>
  public static int DefaultFloor => DEFAULT_FLOOR_VALUE;

  /// <summary>Ready worker threads per available processor on larger hosts.</summary>
  public static int ThreadsPerProcessor => THREADS_PER_PROCESSOR_VALUE;

  /// <summary>
  /// The minimum worker count to apply: the largest of the current minimum (never lowered), an
  /// explicit <paramref name="configuredFloor"/>, and the CPU-scaled default.
  /// </summary>
  /// <param name="currentMinimum">The pool's current minimum worker count.</param>
  /// <param name="processorCount">Processors available to this process.</param>
  /// <param name="configuredFloor">Operator override; ignored when null or non-positive.</param>
  public static int Compute(int currentMinimum, int processorCount, int? configuredFloor) {
    var target = configuredFloor is > 0
      ? configuredFloor.Value
      : Math.Max(DEFAULT_FLOOR_VALUE, processorCount * THREADS_PER_PROCESSOR_VALUE);
    return Math.Max(currentMinimum, target);
  }

  /// <summary>The floor as a derived setting, with the inputs it came from. Inert stub.</summary>
  /// <param name="currentMinimum">The pool's current minimum.</param>
  /// <param name="processorCount">The processor count.</param>
  /// <param name="configuredFloor">The configured floor, if any.</param>
  /// <returns>The derived floor.</returns>
  public static Whizbang.Core.Configuration.DerivedSetting<int> Derive(int currentMinimum, int processorCount, int? configuredFloor) =>
    Whizbang.Core.Configuration.DerivedSetting.Of(
      "Whizbang:Workers:ThreadPoolFloor",
      Compute(currentMinimum, processorCount, configuredFloor),
      configuredFloor is > 0
        ? "the configured floor, never below the current minimum"
        : "max(DefaultFloor, ProcessorCount x ThreadsPerProcessor), never below the current minimum",
      Whizbang.Core.Configuration.SettingInput.Of("ProcessorCount", processorCount),
      Whizbang.Core.Configuration.SettingInput.Of("ThreadsPerProcessor", THREADS_PER_PROCESSOR_VALUE),
      Whizbang.Core.Configuration.SettingInput.Of("DefaultFloor", DEFAULT_FLOOR_VALUE),
      Whizbang.Core.Configuration.SettingInput.Of("CurrentMinimum", currentMinimum));

  /// <summary>
  /// The floor the process runs with, as the last <see cref="Apply"/> derived it (before the first, as it would derive
  /// it now): the value the startup settings report shows, with its inputs.
  /// </summary>
  public static Whizbang.Core.Configuration.DerivedSetting<int> Applied { get; private set; } = _deriveNow(null);

  private static Whizbang.Core.Configuration.DerivedSetting<int> _deriveNow(int? configuredFloor) {
    ThreadPool.GetMinThreads(out var currentWorker, out _);
    return Derive(currentWorker, Environment.ProcessorCount, configuredFloor);
  }

  /// <summary>
  /// Applies the computed floor to the running thread pool and returns the worker minimum now in
  /// effect. Idempotent — re-applying the same floor changes nothing.
  /// </summary>
  /// <param name="configuredFloor">Operator override; ignored when null or non-positive.</param>
  public static int Apply(int? configuredFloor = null) {
    ThreadPool.GetMinThreads(out var currentWorker, out var currentIo);
    Applied = Derive(currentWorker, Environment.ProcessorCount, configuredFloor);
    var target = Applied.Value;
    if (target > currentWorker) {
      // Completion-port threads sit on the same path — asynchronous database I/O completes on
      // them — so they get the same reserve.
      ThreadPool.SetMinThreads(target, Math.Max(currentIo, target));
    }
    ThreadPool.GetMinThreads(out var appliedWorker, out _);
    return appliedWorker;
  }
}
