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
/// A host with no receptors of its own resolves the registry to its null default, whose
/// registration throws by design. That is recognized and skipped, so such a host still starts.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration#hand-written-sagas</docs>
/// <tests>tests/Whizbang.Sagas.Tests/Services/SagaWatchdogTickRoutingTests.cs:Registrar_RegistersTheRouterOnTheReceivingSideOnlyAsync</tests>
/// <tests>tests/Whizbang.Sagas.Tests/Services/SagaWatchdogTickRoutingTests.cs:Registrar_WhenTheRegistryIsTheNullDefault_StartsWithoutThrowingAsync</tests>
/// <tests>tests/Whizbang.Sagas.Tests/Services/SagaWatchdogTickRoutingTests.cs:Registrar_WithNoRegistry_StartsWithoutThrowingAsync</tests>
public sealed class SagaWatchdogTickRouterRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory) : IHostedService {

  /// <inheritdoc/>
  public Task StartAsync(CancellationToken cancellationToken) {
    var registry = services.GetService<IReceptorRegistry>();
    if (registry is null or INullDefault) {
      return Task.CompletedTask;
    }
    registry.Register(new SagaWatchdogTickRouter(scopeFactory), LifecycleStage.PostInboxInline);
    return Task.CompletedTask;
  }

  /// <inheritdoc/>
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
