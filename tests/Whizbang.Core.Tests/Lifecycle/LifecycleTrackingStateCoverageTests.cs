using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Covers the "no receptor invoker in the detached scope" branch inside
/// <see cref="LifecycleTrackingState.AdvanceToAsync"/>'s fire-and-forget detached-stage task. That
/// task deliberately re-resolves <c>IReceptorInvoker</c> from a FRESH DI scope (its own
/// <c>IServiceScopeFactory.CreateAsyncScope()</c>) rather than reusing the invoker already resolved
/// from the caller's scope — so a host whose scope factory produces a scope without the invoker
/// registered (a misconfigured / narrowly-scoped registration) must skip the detached stage
/// cleanly rather than throwing a <see cref="NullReferenceException"/> on the caller's background
/// task, which would be swallowed by nothing and never surface.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Lifecycle/LifecycleTrackingState.cs</code-under-test>
[Category("Core")]
[Category("Lifecycle")]
public class LifecycleTrackingStateCoverageTests {
  private sealed record _probeEvent(string Data) : IEvent;

  /// <summary>Records every stage it is asked to invoke; must NEVER be called in this scenario.</summary>
  private sealed class _recordingInvoker : IReceptorInvoker {
    public List<LifecycleStage> Stages { get; } = [];

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      Stages.Add(stage);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// The OUTER scope: resolves the invoker directly (so <c>AdvanceToAsync</c> proceeds past its
  /// initial null-check into the detached branch) and hands out a scope factory whose scopes are
  /// deliberately empty.
  /// </summary>
  private sealed class _outerProvider(IReceptorInvoker invoker, IServiceScopeFactory scopeFactory) : IServiceProvider {
    public object? GetService(Type serviceType) {
      if (serviceType == typeof(IReceptorInvoker)) {
        return invoker;
      }
      if (serviceType == typeof(IServiceScopeFactory)) {
        return scopeFactory;
      }
      return null;
    }
  }

  private sealed class _gapScopeFactory : IServiceScopeFactory {
    public IServiceScope CreateScope() => new _gapScope();
  }

  /// <summary>A freshly-created scope whose provider resolves nothing — the detached-scope gap.</summary>
  private sealed class _gapScope : IServiceScope {
    public IServiceProvider ServiceProvider { get; } = new _emptyProvider();
    public void Dispose() { }
  }

  private sealed class _emptyProvider : IServiceProvider {
    public object? GetService(Type serviceType) => null;
  }

  [Test]
  public async Task DetachedStage_ScopeFactoryProducesAScopeWithNoInvoker_SkipsWithoutInvokingAsync() {
    var outerInvoker = new _recordingInvoker();
    var provider = new _outerProvider(outerInvoker, new _gapScopeFactory());
    var envelope = new MessageEnvelope<IMessage>(MessageId.New(), new _probeEvent("payload"), []);
    var tracking = new LifecycleTrackingState(
      eventId: Guid.NewGuid(),
      envelope: envelope,
      entryStage: LifecycleStage.LocalImmediateInline,
      source: MessageSource.Local,
      streamId: null,
      perspectiveType: null,
      logger: null);

    await tracking.AdvanceToAsync(LifecycleStage.PreDistributeDetached, provider, CancellationToken.None);
    await tracking.DrainDetachedAsync();

    await Assert.That(outerInvoker.Stages).IsEmpty()
      .Because("the detached task must re-resolve IReceptorInvoker from its OWN fresh scope; when that " +
               "resolution comes back null it must skip the stage silently rather than invoking the " +
               "caller-scope invoker (which would be the wrong scope's services) or throwing unobserved " +
               "on a fire-and-forget background task.");
  }
}
