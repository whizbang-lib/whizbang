// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1676: which path fires a received event's PostAllPerspectives and PostLifecycle stages. The receive path
/// fires them only when nothing in the service applies the event later, so each fires once per event per service.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public class ReceivedEventPostPerspectiveStagesTests {
  private const string FOLDED_TYPE = "Test.FoldedEvent, Test";

  private sealed record ProbeCollective : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  [Test]
  public async Task CollectiveEvent_WithTheCollectiveSinkRegistered_IsLeftToTheSinkAsync() {
    var services = _services(withDispatcher: true, withRegistry: false);

    var fired = ReceivedEventPostPerspectiveStages.FiredOnReceipt(
      TypeNameFormatter.Format(typeof(ProbeCollective)), _collective(), services);

    await Assert.That(fired).IsFalse()
      .Because("the collective sink fires the stages once its apply commits");
  }

  [Test]
  public async Task CollectiveEvent_WithoutTheCollectiveSink_IsFiredOnReceiptAsync() {
    var services = _services(withDispatcher: false, withRegistry: true);

    var fired = ReceivedEventPostPerspectiveStages.FiredOnReceipt(
      TypeNameFormatter.Format(typeof(ProbeCollective)), _collective(), services);

    await Assert.That(fired).IsTrue()
      .Because("nothing in this service applies the event, so receipt is the only place the stages can fire");
  }

  [Test]
  public async Task OrdinaryEvent_ThatAPerspectiveFolds_IsLeftToThePerspectiveWorkerAsync() {
    var services = _services(withDispatcher: true, withRegistry: true);

    var fired = ReceivedEventPostPerspectiveStages.FiredOnReceipt(FOLDED_TYPE, new object(), services);

    await Assert.That(fired).IsFalse();
  }

  [Test]
  public async Task OrdinaryEvent_NoPerspectiveFolds_IsFiredOnReceiptAsync() {
    var services = _services(withDispatcher: true, withRegistry: true);

    var fired = ReceivedEventPostPerspectiveStages.FiredOnReceipt("Test.OtherEvent, Test", new object(), services);

    await Assert.That(fired).IsTrue();
  }

  [Test]
  public async Task NoPerspectiveRegistry_AndAPayloadThatCouldNotBeRead_IsFiredOnReceiptAsync() {
    var services = _services(withDispatcher: true, withRegistry: false);

    var fired = ReceivedEventPostPerspectiveStages.FiredOnReceipt(FOLDED_TYPE, payload: null, services);

    await Assert.That(fired).IsTrue()
      .Because("with no perspectives registered nothing applies the event later");
  }

  private static ProbeCollective _collective() => new() { Scope = new TenantCollectiveScope("t-1") };

  private static ServiceProvider _services(bool withDispatcher, bool withRegistry) {
    var services = new ServiceCollection();
    if (withDispatcher) {
      services.AddSingleton<ICollectiveDispatcher>(new UnusedDispatcher());
    }
    if (withRegistry) {
      services.AddSingleton<IPerspectiveRunnerRegistry>(new FoldingRegistry());
    }
    return services.BuildServiceProvider();
  }

  private sealed class FoldingRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [
      new PerspectiveRegistrationInfo("Test.Perspective", "global::Test.Perspective", "global::Test.Model", [FOLDED_TYPE])
    ];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class UnusedDispatcher : ICollectiveDispatcher {
    public Task<CollectiveDispatchResult> DispatchAsync(ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession,
        Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("never applied here");
  }
}
