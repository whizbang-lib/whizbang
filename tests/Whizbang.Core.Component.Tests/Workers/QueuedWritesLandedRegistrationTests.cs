// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The "every queued write has landed" signal finds the flush workers by itself (#1899): hosting a worker with its start
/// point registers it with the signal when it reports queued writes. Nothing lists the flush workers, so a new one is in
/// the signal by being hosted, and a test fails when a worker that owns a flusher does not report it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/QueuedWritesLandedSignal.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageGatedService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/WhizbangWorkerServiceCollectionExtensions.cs</code-under-test>
/// <docs>fundamentals/work-coordinator/batched-flushers#when-writes-have-landed</docs>
[Category("Workers")]
public sealed class QueuedWritesLandedRegistrationTests {
  /// <summary>Every type in the core library that owns a <see cref="BatchFlusher{T}"/> in a field.</summary>
  /// <remarks>Reads private fields: a worker keeps its flusher private, and the guard has to see it there.</remarks>
#pragma warning disable S3011 // the guard inspects private fields by design; it reads their types and nothing else
  private static List<Type> _typesOwningAFlusher() =>
    [.. typeof(BatchFlusher<>).Assembly.GetTypes()
      .Where(t => !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false)
        // The flusher's own nested types point back at it; they are part of it, not owners of one.
        && !(t.DeclaringType is { IsGenericType: true } declaring && declaring.GetGenericTypeDefinition() == typeof(BatchFlusher<>))
        && t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
          .Any(f => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(BatchFlusher<>)))];
#pragma warning restore S3011

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
    services.TryAddSingleton<IPinnedConnectionPool>(NoOpPinnedConnectionPool.Instance);
    services.TryAddSingleton<IInboxChannelWriter, InboxChannelWriter>();
    await using var provider = services.BuildServiceProvider();

    var signal = provider.GetRequiredService<IQueuedWritesLandedSignal>();
    // Compared by name: an equivalence check walks a Type's properties, and some of them throw.
    var reported = signal.Flushers.Select(f => f.GetType().FullName!).Order(StringComparer.Ordinal).ToList();
    var hostedOwners = _typesOwningAFlusher().Where(t => typeof(IHostedService).IsAssignableFrom(t))
      .Select(t => t.FullName!).Order(StringComparer.Ordinal).ToList();

    await Assert.That(hostedOwners.Count).IsGreaterThanOrEqualTo(5);
    await Assert.That(reported).IsEquivalentTo(hostedOwners)
      .Because("every worker the pipeline hosts that owns a flusher is watched, each once, and nothing else is");
    await Assert.That(signal.Flushers).Contains(provider.GetRequiredService<OutboxCompletionFlushWorker>())
      .Because("the signal watches the instance the host runs, not a second one");
    await Assert.That(signal.AllLanded).IsTrue();
  }

  /// <summary>A worker of the application's own that queues writes, hosted when the schema is ready.</summary>
  private sealed class ApplicationFlushWorker : BackgroundService, IQueuedWriteFlusher {
    public QueuedWriteActivity Activity => new(Version: 0, Pending: 0);
    public bool IsIdle => true;
    public event WorkProcessingIdleHandler? OnWorkProcessingIdle {
      add => _ = value;
      remove => _ = value;
    }
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
  }

  [Test]
  public async Task AWorkerHostedAtSchemaReadyThatQueuesWrites_IsWatchedToo_OnceHoweverOftenItIsHostedAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangWorkerAtSchemaReady<ApplicationFlushWorker>();
    services.AddWhizbangWorkerAtSchemaReady<ApplicationFlushWorker>();
    services.TryAddSingleton<IQueuedWritesLandedSignal>(sp => new QueuedWritesLandedSignal(QueuedWritesLandedSignal.RegisteredIn(sp)));
    await using var provider = services.BuildServiceProvider();

    var flushers = provider.GetRequiredService<IQueuedWritesLandedSignal>().Flushers;

    await Assert.That(flushers.Count).IsEqualTo(1);
    await Assert.That(flushers[0]).IsSameReferenceAs(provider.GetRequiredService<ApplicationFlushWorker>());
  }
}
