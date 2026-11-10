// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The "every queued write has landed" signal finds the flush workers by itself (#1899): it takes every hosted service
/// that reports queued writes, through the stage wrapper each worker is hosted in. Nothing lists the flush workers, so
/// a new one is in the signal by being hosted, and a test fails when a worker that owns a flusher does not report it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/QueuedWritesLandedSignal.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
/// <docs>fundamentals/work-coordinator/batched-flushers#when-writes-have-landed</docs>
[Category("Workers")]
public sealed class QueuedWritesLandedRegistrationTests {
  /// <summary>Every type in the core library that owns a <see cref="BatchFlusher{T}"/> in a field.</summary>
  private static List<Type> _typesOwningAFlusher() =>
    [.. typeof(BatchFlusher<>).Assembly.GetTypes()
      .Where(t => !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
      .Where(t => t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Any(f => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(BatchFlusher<>)))];

  [Test]
  public async Task EveryTypeThatOwnsAFlusher_ReportsItsQueuedWritesAsync() {
    var owners = _typesOwningAFlusher();
    var silent = owners.Where(t => !typeof(IQueuedWriteFlusher).IsAssignableFrom(t)).Select(t => t.Name).ToList();

    await Assert.That(owners.Count).IsGreaterThanOrEqualTo(5)
      .Because("the five flush workers own a flusher; finding fewer means the search is broken, not that all is well");
    await Assert.That(silent).IsEmpty()
      .Because("a flusher whose owner does not report it holds writes the landed signal cannot see (#1899)");
  }

  [Test]
  public async Task TheWorkerPipeline_RegistersTheSignal_OverEveryHostedFlushWorkerAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var signal = provider.GetRequiredService<IQueuedWritesLandedSignal>();
    var reported = signal.Flushers.Select(f => f.GetType()).ToHashSet();
    var hostedOwners = _typesOwningAFlusher().Where(t => typeof(IHostedService).IsAssignableFrom(t)).ToList();

    await Assert.That(hostedOwners.Count).IsGreaterThanOrEqualTo(5);
    foreach (var owner in hostedOwners) {
      await Assert.That(reported).Contains(owner)
        .Because($"{owner.Name} is hosted by the worker pipeline and owns a flusher, so its writes count");
    }
    await Assert.That(signal.Flushers).Contains(provider.GetRequiredService<OutboxCompletionFlushWorker>())
      .Because("the signal watches the instance the host runs, not a second one");
    await Assert.That(signal.AllLanded).IsTrue();
  }
}
