// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// An ended tracking entry hands its in-flight detached stages to the coordinator so a drain can still
/// await them, and the coordinator lets go of each one once it finishes (#1797). Holding finished tasks
/// grew the coordinator by one task per message for the life of the process.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Lifecycle/LifecycleCoordinator.cs</code-under-test>
public class LifecycleCoordinatorDetachedRetentionTests {
  private sealed record RetentionEvent(Guid Id) : IEvent;

  /// <summary>Holds every detached stage until released; signals when one has started.</summary>
  private sealed class GatedInvoker : IReceptorInvoker {
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask InvokeAsync(
        IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      Started.TrySetResult();
      await Release.Task.ConfigureAwait(false);
    }
  }

  private static MessageEnvelope<RetentionEvent> _envelope(Guid id) => new() {
    MessageId = MessageId.From(id),
    Payload = new RetentionEvent(id),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private static ServiceProvider _provider(IReceptorInvoker invoker) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped(_ => invoker);
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task EndedTracking_ReleasesItsDetachedStage_OnceTheStageFinishesAsync(CancellationToken cancellationToken) {
    var invoker = new GatedInvoker();
    await using var provider = _provider(invoker);
    var coordinator = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance);
    Guid eventId = TrackedGuid.New();
    var tracking = coordinator.BeginTracking(eventId, _envelope(eventId), LifecycleStage.PostLifecycleDetached, MessageSource.Local);
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleDetached, provider, cancellationToken);
    await invoker.Started.Task.WaitAsync(cancellationToken);

    coordinator.AbandonTracking(eventId);
    await Assert.That(coordinator.RetainedDetachedTaskCount).IsEqualTo(1)
      .Because("a detached stage still running when its entry ends is kept so a drain can await it");

    invoker.Release.SetResult();
    await coordinator.DrainAllDetachedAsync();

    await Assert.That(coordinator.RetainedDetachedTaskCount).IsEqualTo(0)
      .Because("a finished detached stage is let go; keeping it grew the coordinator with every message");
  }

  [Test]
  public async Task EndedTracking_NeverRetainsADetachedStageThatAlreadyFinishedAsync(CancellationToken cancellationToken) {
    var invoker = new GatedInvoker();
    invoker.Release.SetResult();
    await using var provider = _provider(invoker);
    var coordinator = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance);
    Guid eventId = TrackedGuid.New();
    var tracking = coordinator.BeginTracking(eventId, _envelope(eventId), LifecycleStage.PostLifecycleDetached, MessageSource.Local);
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleDetached, provider, cancellationToken);
    await tracking.DrainDetachedAsync();

    coordinator.AbandonTracking(eventId);

    await Assert.That(coordinator.RetainedDetachedTaskCount).IsEqualTo(0)
      .Because("there is nothing left to drain for a stage that already finished");
  }
}
