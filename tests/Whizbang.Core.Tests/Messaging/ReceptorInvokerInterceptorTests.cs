// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.Tests.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #1996: the receptor invoker, which runs every lifecycle-stage receptor including the ones inbox delivery runs, asks
/// the registered interceptors before each receptor. A denied receptor does not run and cascades nothing.
/// <see cref="ReceptorInvoker.EvaluateAdmissionAsync"/> answers the same question ahead of the stages, so the inbox
/// worker can dead-letter or retry a denied message before it completes the row.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
[Category("Security")]
public class ReceptorInvokerInterceptorTests {
  private sealed record ProbeMessage(Guid Id) : IMessage;
  private sealed record ProbeEvent(Guid Id) : IEvent;

  private sealed class DeniedReceptor;
  private sealed class AllowedReceptor;

  [RequirePermission("orders:approve")]
  private sealed class PermissionReceptor;

  [Test]
  public async Task InvokeAsync_DeniedReceptor_DoesNotRunAndCascadesNothingAsync() {
    var registry = new Registry();
    var ran = registry.Add<DeniedReceptor>(LifecycleStage.PostInboxInline, returns: new ProbeEvent(TrackedGuid.New()));
    var cascader = new CapturingCascader();
    var invoker = new ReceptorInvoker(registry, _provider(new Deny(typeof(DeniedReceptor), DeniedAction.DeadLetter)), cascader);

    var denied = await Assert.ThrowsAsync<ReceptorInvocationDeniedException>(
      async () => await invoker.InvokeAsync(_envelope(), LifecycleStage.PostInboxInline));

    await Assert.That(denied!.Action).IsEqualTo(DeniedAction.DeadLetter);
    await Assert.That(ran.Count).IsEqualTo(0);
    await Assert.That(cascader.Cascaded).IsEmpty()
      .Because("a receptor that did not run returned nothing to cascade");
  }

  [Test]
  public async Task InvokeAsync_DropQuiet_SkipsThatReceptorAndRunsTheNextAsync() {
    var registry = new Registry();
    var denied = registry.Add<DeniedReceptor>(LifecycleStage.PostInboxInline);
    var allowed = registry.Add<AllowedReceptor>(LifecycleStage.PostInboxInline);
    var invoker = new ReceptorInvoker(registry, _provider(new Deny(typeof(DeniedReceptor), DeniedAction.DropQuiet)));

    await invoker.InvokeAsync(_envelope(), LifecycleStage.PostInboxInline);

    await Assert.That(denied.Count).IsEqualTo(0);
    await Assert.That(allowed.Count).IsEqualTo(1);
  }

  [Test]
  public async Task InvokeAsync_RuntimeRegisteredReceptor_IsGatedByItsInstanceTypeAsync() {
    var registry = new Registry();
    var ran = registry.Add<DeniedReceptor>(LifecycleStage.PostInboxInline, runtimeInstance: new DeniedReceptor());
    var invoker = new ReceptorInvoker(registry, _provider(new Deny(typeof(DeniedReceptor), DeniedAction.Throw)));

    await Assert.ThrowsAsync<ReceptorInvocationDeniedException>(
      async () => await invoker.InvokeAsync(_envelope(), LifecycleStage.PostInboxInline));

    await Assert.That(ran.Count).IsEqualTo(0);
  }

  [Test]
  public async Task InvokeAsync_EntryWithoutAReceptorType_RunsAsync() {
    var registry = new Registry();
    var ran = registry.Add<DeniedReceptor>(LifecycleStage.PostInboxInline, withType: false);
    var invoker = new ReceptorInvoker(registry, _provider(new Deny(typeof(DeniedReceptor), DeniedAction.Throw)));

    await invoker.InvokeAsync(_envelope(), LifecycleStage.PostInboxInline);

    await Assert.That(ran.Count).IsEqualTo(1)
      .Because("a hand-built entry that names no receptor class has nothing an interceptor can be asked about");
  }

  [Test]
  public async Task InvokeAsync_NoInterceptors_RunsAndHoldsNoPipelineAsync() {
    var registry = new Registry();
    var ran = registry.Add<DeniedReceptor>(LifecycleStage.PostInboxInline);
    var invoker = new ReceptorInvoker(registry, _provider());

    await invoker.InvokeAsync(_envelope(), LifecycleStage.PostInboxInline);

    await Assert.That(ran.Count).IsEqualTo(1);
    await Assert.That(invoker.HasReceptorInterceptors).IsFalse();
  }

  [Test]
  public async Task InvokeAsync_InterceptorSeesTheScopeContextTheReceptorSeesAsync() {
    var registry = new Registry();
    var interceptor = new Deny(typeof(DeniedReceptor), DeniedAction.DeadLetter);
    var provider = _provider(interceptor);
    var ran = registry.Add<AllowedReceptor>(LifecycleStage.PostInboxInline,
      onRun: sp => sp.GetRequiredService<IScopeContextAccessor>().Current);
    var invoker = new ReceptorInvoker(registry, provider);
    ScopeContextAccessor.CurrentContext = ReceptorInterceptorDispatchTests._scopeWith("orders:read");

    await invoker.InvokeAsync(_envelope(), LifecycleStage.PostInboxInline);

    await Assert.That(ran.Seen.Single()).IsNotNull();
    await Assert.That(ReferenceEquals(interceptor.Contexts.Single(), ran.Seen.Single())).IsTrue();
  }

  [Test]
  public async Task EvaluateAdmissionAsync_LoudDenialAtAnyStage_IsReturnedAsync() {
    var registry = new Registry();
    registry.Add<AllowedReceptor>(LifecycleStage.PreInboxInline);
    var ran = registry.Add<DeniedReceptor>(LifecycleStage.PostInboxDetached);
    var invoker = new ReceptorInvoker(registry, _provider(new Deny(typeof(DeniedReceptor), DeniedAction.Quarantine)));

    var result = await invoker.EvaluateAdmissionAsync(_envelope(),
      [LifecycleStage.PreInboxInline, LifecycleStage.PostInboxDetached], CancellationToken.None);

    await Assert.That(result.Allow).IsFalse();
    await Assert.That(result.OnDenied).IsEqualTo(DeniedAction.Quarantine);
    await Assert.That(ran.Count).IsEqualTo(0).Because("evaluating admission runs no receptor");
  }

  [Test]
  public async Task EvaluateAdmissionAsync_DropQuietOnly_IsAllowedAsync() {
    var registry = new Registry();
    registry.Add<DeniedReceptor>(LifecycleStage.PostInboxDetached);
    var invoker = new ReceptorInvoker(registry, _provider(new Deny(typeof(DeniedReceptor), DeniedAction.DropQuiet)));

    var result = await invoker.EvaluateAdmissionAsync(_envelope(), [LifecycleStage.PostInboxDetached], CancellationToken.None);

    await Assert.That(result.Allow).IsTrue()
      .Because("a quiet drop skips that receptor when it would run; the message itself is still completed");
  }

  [Test]
  public async Task EvaluateAdmissionAsync_NoInterceptors_IsAllowedWithoutLookingAtReceptorsAsync() {
    var registry = new Registry();
    var invoker = new ReceptorInvoker(registry, _provider());

    var result = await invoker.EvaluateAdmissionAsync(_envelope(), [LifecycleStage.PostInboxDetached], CancellationToken.None);

    await Assert.That(result.Allow).IsTrue();
    await Assert.That(registry.Lookups).IsEqualTo(0);
  }

  [Test]
  public async Task EvaluateAdmissionAsync_RoutedNonePayload_IsAllowedAsync() {
    var registry = new Registry();
    var invoker = new ReceptorInvoker(registry, _provider(new Deny(typeof(DeniedReceptor), DeniedAction.DeadLetter)));
    var envelope = new MessageEnvelope<object> {
      MessageId = MessageId.New(),
      Payload = Route.None(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
    };

    var result = await invoker.EvaluateAdmissionAsync(envelope, [LifecycleStage.PostInboxDetached], CancellationToken.None);

    await Assert.That(result.Allow).IsTrue();
  }

  [Test]
  public async Task EvaluateAdmissionAsync_RequirePermissionWithoutThePermission_DeadLettersAsync() {
    var registry = new Registry();
    registry.Add<PermissionReceptor>(LifecycleStage.PostInboxDetached);
    var invoker = new ReceptorInvoker(registry, _provider(new DefaultRequirePermissionInterceptor()));
    ScopeContextAccessor.CurrentContext = ReceptorInterceptorDispatchTests._scopeWith("orders:read");

    var result = await invoker.EvaluateAdmissionAsync(_envelope(), [LifecycleStage.PostInboxDetached], CancellationToken.None);

    await Assert.That(result.Allow).IsFalse();
    await Assert.That(result.OnDenied).IsEqualTo(DeniedAction.DeadLetter);
    await Assert.That(result.Reason).Contains("orders:approve");
  }

  [Test]
  public async Task EvaluateAdmissionAsync_DefaultInterfaceImplementation_AllowsAsync() {
    IReceptorInvoker invoker = new NoAdmissionInvoker();

    var result = await invoker.EvaluateAdmissionAsync(_envelope(), [LifecycleStage.PostInboxDetached], CancellationToken.None);

    await Assert.That(result.Allow).IsTrue();
  }

  private static MessageEnvelope<ProbeMessage> _envelope() => new() {
    MessageId = MessageId.New(),
    Payload = new ProbeMessage(TrackedGuid.New()),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
  };

  private static ServiceProvider _provider(params IReceptorInterceptor[] interceptors) {
    var services = new ServiceCollection();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    foreach (var interceptor in interceptors) {
      services.AddSingleton(interceptor);
    }
    return services.BuildServiceProvider();
  }

  private sealed class Deny(Type denied, DeniedAction action) : IReceptorInterceptor {
    public List<IScopeContext?> Contexts { get; } = [];

    public ValueTask<InterceptorResult> CanInvokeAsync(Type receptorType, IMessageEnvelope envelope, IScopeContext? context,
        CancellationToken cancellationToken = default) {
      Contexts.Add(context);
      return new ValueTask<InterceptorResult>(receptorType == denied ? InterceptorResult.Deny(action) : InterceptorResult.Allowed);
    }
  }

  private sealed class Runs {
    public int Count { get; set; }
    public List<object?> Seen { get; } = [];
  }

  private sealed class CapturingCascader : IEventCascader {
    public List<object> Cascaded { get; } = [];

    public Task CascadeFromResultAsync(object result, IMessageEnvelope? sourceEnvelope, DispatchModes? receptorDefault = null,
        CancellationToken cancellationToken = default) {
      Cascaded.Add(result);
      return Task.CompletedTask;
    }
  }

  private sealed class NoAdmissionInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class Registry : IReceptorRegistry {
    private readonly Dictionary<LifecycleStage, List<ReceptorInfo>> _byStage = [];
    public int Lookups { get; private set; }

    public Runs Add<TReceptor>(LifecycleStage stage, object? returns = null, object? runtimeInstance = null, bool withType = true,
        Func<IServiceProvider, object?>? onRun = null) {
      var runs = new Runs();
      if (!_byStage.TryGetValue(stage, out var list)) {
        list = [];
        _byStage[stage] = list;
      }
      list.Add(new ReceptorInfo(
        MessageType: typeof(ProbeMessage),
        ReceptorId: typeof(TReceptor).Name + "_" + stage,
        InvokeAsync: (sp, _, _, _, _) => {
          runs.Count++;
          if (onRun is not null) {
            runs.Seen.Add(onRun(sp));
          }
          return ValueTask.FromResult(returns);
        },
        RuntimeInstance: runtimeInstance,
        ReceptorType: withType && runtimeInstance is null ? typeof(TReceptor) : null));
      return runs;
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      Lookups++;
      return messageType == typeof(ProbeMessage) && _byStage.TryGetValue(stage, out var list) ? list : [];
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }
}
