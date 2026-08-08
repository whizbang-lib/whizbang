using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Whizbang starts its background workers on the host's thread pool, so it is Whizbang's own load
/// that can starve the host's HTTP pipeline. In a CPU-limited container the runtime seeds the
/// pool's minimum worker count from the CPU allowance (a ~1.7-core limit yields 2) and injects
/// further threads at only one or two per second, so a burst of asynchronous database completions
/// from the drain, audit and outbox workers queues ahead of everything else for tens of seconds.
/// <para>
/// Observed live: pods were SIGKILLed roughly every two minutes — 50 to 66 restarts overnight —
/// because a liveness endpoint that touches nothing at all could not be answered inside a
/// 10-second probe timeout. The restart re-ran the same backlog, so the probe destroyed the only
/// process able to clear the condition it was reporting.
/// </para>
/// <para>
/// <see cref="WorkerThreadPoolFloor"/> establishes the reserve as part of registering the worker
/// pipeline, rather than leaving every consumer to discover this the hard way. It only ever
/// raises the minimum.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerThreadPoolFloor.cs</code-under-test>
public class WorkerThreadPoolFloorTests {

  [Test]
  public async Task Compute_RaisesSmallContainerDefaultToTheFloorAsync() {
    var computed = WorkerThreadPoolFloor.Compute(currentMinimum: 2, processorCount: 2, configuredFloor: null);

    await Assert.That(computed).IsGreaterThanOrEqualTo(WorkerThreadPoolFloor.DefaultFloor)
      .Because("a 2-thread minimum lets a burst of worker database completions queue ahead of the " +
               "host's health endpoint for longer than a probe timeout");
  }

  [Test]
  public async Task Compute_NeverLowersAnAlreadyHigherMinimumAsync() {
    await Assert.That(WorkerThreadPoolFloor.Compute(currentMinimum: 400, processorCount: 8, configuredFloor: null))
      .IsEqualTo(400)
      .Because("a host or operator that already tuned the pool higher must keep its value — " +
               "the framework raises a floor, it does not impose a ceiling");
  }

  [Test]
  public async Task Compute_HonorsConfiguredFloorAndIgnoresNonPositiveAsync() {
    await Assert.That(WorkerThreadPoolFloor.Compute(2, 2, configuredFloor: 128)).IsEqualTo(128);
    await Assert.That(WorkerThreadPoolFloor.Compute(2, 2, configuredFloor: 0))
      .IsGreaterThanOrEqualTo(WorkerThreadPoolFloor.DefaultFloor)
      .Because("zero is a misconfiguration, not an instruction to disable the reserve");
  }

  [Test]
  public async Task Compute_ScalesWithProcessorCountAsync() {
    await Assert.That(WorkerThreadPoolFloor.Compute(16, 16, null))
      .IsGreaterThanOrEqualTo(16 * WorkerThreadPoolFloor.ThreadsPerProcessor)
      .Because("a larger CPU allowance implies more concurrent worker I/O in flight");
  }

  [Test]
  public async Task AddWhizbangWorkers_AppliesTheFloor_ProductionWiringGuardAsync() {
    // PRODUCTION-WIRING GUARD: the computation is worthless unless registering the worker
    // pipeline actually applies it. Remove the call from AddWhizbangWorkers and this fails.
    ThreadPool.GetMinThreads(out var originalWorker, out var originalIo);
    try {
      ThreadPool.SetMinThreads(2, 2);   // the minimum a CPU-limited container gets

      new ServiceCollection().AddWhizbangWorkers();

      ThreadPool.GetMinThreads(out var worker, out _);
      await Assert.That(worker).IsGreaterThanOrEqualTo(WorkerThreadPoolFloor.DefaultFloor)
        .Because("the reserve must exist before Whizbang's own workers start competing for threads");
    } finally {
      ThreadPool.SetMinThreads(originalWorker, originalIo);
    }
  }
}
