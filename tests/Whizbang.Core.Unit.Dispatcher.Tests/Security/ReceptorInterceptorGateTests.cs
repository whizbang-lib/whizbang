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
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// The shared decision every receptor invocation path takes on the interceptors' answer (#1996): allowed runs,
/// DropQuiet skips without an exception, every other denial throws <see cref="ReceptorInvocationDeniedException"/>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Security/ReceptorInterceptorGate.cs</code-under-test>
[Category("Security")]
public class ReceptorInterceptorGateTests {
  private sealed class StubReceptor;

  [Test]
  public async Task Active_NoInterceptorsRegistered_IsNullAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    var provider = services.BuildServiceProvider();

    await Assert.That(ReceptorInterceptorGate.Active(provider)).IsNull()
      .Because("an empty pipeline is dropped so no invocation path evaluates it");
    await Assert.That(provider.GetRequiredService<ReceptorInterceptorPipeline>().IsEmpty).IsTrue();
  }

  [Test]
  public async Task Active_InterceptorRegistered_ReturnsTheRegisteredPipelineAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IReceptorInterceptor, DefaultRequirePermissionInterceptor>();
    var provider = services.BuildServiceProvider();

    var active = ReceptorInterceptorGate.Active(provider);

    await Assert.That(active).IsSameReferenceAs(provider.GetRequiredService<ReceptorInterceptorPipeline>());
  }

  [Test]
  public async Task Active_InterceptorRegisteredWithoutTheDefaults_IsStillEnforcedAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorInterceptor>(new FixedInterceptor(InterceptorResult.Deny(DeniedAction.Throw)));
    var provider = services.BuildServiceProvider();

    var active = ReceptorInterceptorGate.Active(provider);

    await Assert.That(active).IsNotNull()
      .Because("an interceptor registered in a container that never ran the framework defaults must not be skipped");
    await Assert.That(active!.IsEmpty).IsFalse();
  }

  [Test]
  public async Task Active_ProviderWithoutEnumerables_IsNullAsync() {
    await Assert.That(ReceptorInterceptorGate.Active(new EmptyProvider())).IsNull();
  }

  [Test]
  public async Task Decide_Allowed_RunsAsync() {
    var run = ReceptorInterceptorGate.Decide(InterceptorResult.Allowed, typeof(StubReceptor), _envelope(), new EmptyProvider());
    await Assert.That(run).IsTrue();
  }

  [Test]
  public async Task Decide_DropQuiet_SkipsWithoutThrowingAndLogsAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    var provider = services.BuildServiceProvider();

    var run = ReceptorInterceptorGate.Decide(InterceptorResult.Deny(DeniedAction.DropQuiet, "best effort"),
      typeof(StubReceptor), _envelope(), provider);

    await Assert.That(run).IsFalse();
  }

  [Test]
  public async Task Decide_DropQuiet_WithoutLogger_SkipsAsync() {
    var run = ReceptorInterceptorGate.Decide(InterceptorResult.Deny(DeniedAction.DropQuiet),
      typeof(StubReceptor), _envelope(), new EmptyProvider());
    await Assert.That(run).IsFalse();
  }

  [Test]
  [Arguments(DeniedAction.DeadLetter)]
  [Arguments(DeniedAction.Quarantine)]
  [Arguments(DeniedAction.Throw)]
  public async Task Decide_LoudDenial_ThrowsWithTheActionAndReasonAsync(DeniedAction action) {
    var denied = await Assert.ThrowsAsync<ReceptorInvocationDeniedException>(() => {
      ReceptorInterceptorGate.Decide(InterceptorResult.Deny(action, "missing orders:approve"), typeof(StubReceptor), _envelope(), new EmptyProvider());
      return Task.CompletedTask;
    });

    await Assert.That(denied!.Action).IsEqualTo(action);
    await Assert.That(denied.ReceptorType).IsEqualTo(typeof(StubReceptor));
    await Assert.That(denied.Reason).IsEqualTo("missing orders:approve");
    await Assert.That(denied.Message).Contains("missing orders:approve").And.Contains($"DeniedAction.{action}");
    await Assert.That(denied).IsAssignableTo<UnauthorizedAccessException>();
  }

  [Test]
  public async Task Decide_DenialWithoutReason_SaysAnInterceptorDeniedItAsync() {
    var denied = await Assert.ThrowsAsync<ReceptorInvocationDeniedException>(() => {
      ReceptorInterceptorGate.Decide(InterceptorResult.Deny(DeniedAction.DeadLetter), typeof(StubReceptor), _envelope(), new EmptyProvider());
      return Task.CompletedTask;
    });
    await Assert.That(denied!.Message).Contains("a receptor interceptor denied the invocation");
  }

  [Test]
  public async Task AdmitAsync_PassesTheReceptorEnvelopeAndContextToTheInterceptorAsync() {
    var interceptor = new FixedInterceptor(InterceptorResult.Allowed);
    var envelope = _envelope();
    var context = ReceptorInterceptorDispatchTests._scopeWith("orders:read");

    var run = await ReceptorInterceptorGate.AdmitAsync(new ReceptorInterceptorPipeline([interceptor]), typeof(StubReceptor),
      envelope, context, new EmptyProvider(), CancellationToken.None);

    await Assert.That(run).IsTrue();
    await Assert.That(interceptor.LastReceptorType).IsEqualTo(typeof(StubReceptor));
    await Assert.That(interceptor.LastEnvelope).IsSameReferenceAs(envelope);
    await Assert.That(interceptor.LastContext).IsSameReferenceAs(context);
  }

  [Test]
  public async Task Admit_SynchronousInterceptor_DecidesInlineAsync() {
    var pipeline = new ReceptorInterceptorPipeline([new FixedInterceptor(InterceptorResult.Deny(DeniedAction.DropQuiet))]);

    var run = ReceptorInterceptorGate.Admit(pipeline, typeof(StubReceptor), _envelope(), null, new EmptyProvider());

    await Assert.That(run).IsFalse();
  }

  [Test]
  public async Task Admit_InterceptorThatDoesNotCompleteSynchronously_IsRefusedLoudlyAsync() {
    var pending = new TaskCompletionSource<InterceptorResult>();
    var pipeline = new ReceptorInterceptorPipeline([new PendingInterceptor(pending.Task)]);

    var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => {
      ReceptorInterceptorGate.Admit(pipeline, typeof(StubReceptor), _envelope(), null, new EmptyProvider());
      return Task.CompletedTask;
    });
    pending.SetResult(InterceptorResult.Allowed);

    await Assert.That(refused!.Message).Contains("synchronous receptor");
  }

  [Test]
  public async Task ReceptorScopeContext_ReadsTheRegisteredAccessorFirstAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    var provider = services.BuildServiceProvider();
    var context = ReceptorInterceptorDispatchTests._scopeWith("orders:read");
    ScopeContextAccessor.CurrentContext = context;

    await Assert.That(ReceptorInterceptorGate.ReceptorScopeContext(provider)).IsSameReferenceAs(context);
    await Assert.That(ReceptorInterceptorGate.ReceptorScopeContext(new EmptyProvider())).IsSameReferenceAs(context)
      .Because("without a registered accessor the ambient scope context is what the receptor reads");
  }

  [Test]
  public async Task Exception_GeneralPurposeConstructors_CarryNoReceptorAsync() {
    var inner = new InvalidOperationException("inner");
    await Assert.That(new ReceptorInvocationDeniedException().ReceptorType).IsNull();
    await Assert.That(new ReceptorInvocationDeniedException("denied").Message).IsEqualTo("denied");
    await Assert.That(new ReceptorInvocationDeniedException("denied", inner).InnerException).IsSameReferenceAs(inner);
  }

  private static MessageEnvelope<object> _envelope() => new() {
    MessageId = MessageId.New(),
    Payload = new object(),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private sealed class FixedInterceptor(InterceptorResult result) : IReceptorInterceptor {
    public Type? LastReceptorType { get; private set; }
    public IMessageEnvelope? LastEnvelope { get; private set; }
    public IScopeContext? LastContext { get; private set; }

    public ValueTask<InterceptorResult> CanInvokeAsync(Type receptorType, IMessageEnvelope envelope, IScopeContext? context,
        CancellationToken cancellationToken = default) {
      LastReceptorType = receptorType;
      LastEnvelope = envelope;
      LastContext = context;
      return new ValueTask<InterceptorResult>(result);
    }
  }

  private sealed class PendingInterceptor(Task<InterceptorResult> result) : IReceptorInterceptor {
    public ValueTask<InterceptorResult> CanInvokeAsync(Type receptorType, IMessageEnvelope envelope, IScopeContext? context,
        CancellationToken cancellationToken = default) => new(result);
  }

  private sealed class EmptyProvider : IServiceProvider {
    public object? GetService(Type serviceType) => null;
  }
}
