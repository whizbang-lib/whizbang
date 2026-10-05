// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Sagas.Services;

/// <summary>
/// Registers <see cref="SagaWatchdogTickRouter"/> at startup so a saga's watchdog ticks have a
/// receiver however the saga was declared.
/// </summary>
/// <remarks>
/// <para>
/// Registered on the receiving side only. A tick is armed for a future time: the dispatcher holds
/// the local receptor path until that time, but a receptor on the sending side of the pipeline would
/// run when the tick is armed and re-arm immediately — the tick cascade that once abandoned a saga
/// within milliseconds of starting it. On the receiving side the router runs when the scheduled tick
/// is actually delivered, which is the only moment it should.
/// </para>
/// <para>
/// The receiving stage is <see cref="LifecycleStage.PreInboxInline"/>, not the post-inbox one. The
/// receptor invoker skips post-inbox receptors for a message whose last hop is this same service,
/// because such a message already ran its receptors when it was published. A saga service normally
/// arms and receives its own ticks, and the router runs at no publishing stage, so at the post-inbox
/// stage every one of those ticks was stored, committed and handed to nobody. The pre-inbox stage
/// runs once for every row the inbox dispatches, whichever service published it.
/// </para>
/// <para>
/// Because the router is registered here, at startup, the compile-time discovery that derives a
/// host's transport subscriptions from its generated receptors never sees it. <c>AddWhizbangSagas</c>
/// therefore also declares the tick as a runtime event subscription, which is what subscribes the
/// host to the tick's topic; without it the router would wait on a topic nothing subscribes to.
/// </para>
/// <para>
/// A host with no receptors of its own resolves the registry to its null default, whose
/// registration throws by design. That is recognized and skipped, so such a host still starts.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration#hand-written-sagas</docs>
/// <tests>tests/Whizbang.Sagas.Tests/Services/SagaWatchdogTickRoutingTests.cs:Registrar_RegistersTheRouterOnTheReceivingSideOnlyAsync</tests>
/// <tests>tests/Whizbang.Sagas.Tests/Services/SagaWatchdogTickRoutingTests.cs:Registrar_WhenTheRegistryIsTheNullDefault_StartsWithoutThrowingAsync</tests>
/// <tests>tests/Whizbang.Sagas.Tests/Services/SagaWatchdogTickRoutingTests.cs:Registrar_WithNoRegistry_StartsWithoutThrowingAsync</tests>
/// <tests>tests/Whizbang.Sagas.Tests/SagaWatchdogTickSubscriptionIntegrationTests.cs:AddSagaServiceOnly_SubscribesToTheTicksTopic_AndAPublishedTickReachesTheSagaAsync</tests>
/// <tests>tests/Whizbang.Sagas.Tests/SagaWatchdogTickSubscriptionIntegrationTests.cs:SweepTickPublishedByTheSagasOwnService_IsReceivedKeptAndReachesTheSagaAsync</tests>
/// <tests>tests/Whizbang.Sagas.Tests/SagaWatchdogTickDeliveryIntegrationTests.cs:HandWrittenSagaTick_AfterTheInboxCommit_DoesNotReachTheSagaAgainAsync</tests>
public sealed class SagaWatchdogTickRouterRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory) : IHostedService {

  /// <inheritdoc/>
  public Task StartAsync(CancellationToken cancellationToken) {
    var registry = services.GetService<IReceptorRegistry>();
    if (registry is null or INullDefault) {
      return Task.CompletedTask;
    }
    registry.Register(new SagaWatchdogTickRouter(scopeFactory), LifecycleStage.PreInboxInline);
    return Task.CompletedTask;
  }

  /// <inheritdoc/>
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
