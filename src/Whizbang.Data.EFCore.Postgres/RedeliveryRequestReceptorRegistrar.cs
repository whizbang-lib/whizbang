using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Registers <see cref="RedeliveryRequestReceptor"/> with <see cref="IReceptorRegistry"/> at startup.
/// <see cref="RequestRedeliveryCommand"/> is a framework command defined in <c>Whizbang.Core</c>, and
/// source-generated receptor discovery only sees the consumer's own syntax, so a built-in receptor needs
/// runtime registration to join the dispatch pipeline. Registered at the three default lifecycle stages a
/// receptor without <c>[FireAt]</c> fires at (<see cref="LifecycleStage.LocalImmediateInline"/> /
/// <see cref="LifecycleStage.PreOutboxInline"/> / <see cref="LifecycleStage.PostInboxInline"/>), so the
/// command reaches it whether dispatched in-process (operator-initiated) or arriving over the inbox from
/// a damaged consumer. No-ops if no registry is present (schema-only / diagnostic hosts still boot).
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryRequestReceptorTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S6672:Generic logger injection should match enclosing type", Justification = "The registrar never logs. It receives the logger for the receptor it constructs and hands it straight over, so the category names the type that actually writes the entries.")]
internal sealed class RedeliveryRequestReceptorRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<RedeliveryRequestReceptor> receptorLogger) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    var registry = services.GetService<IReceptorRegistry>();
    if (registry is null) {
      return Task.CompletedTask;
    }
    var receptor = new RedeliveryRequestReceptor(scopeFactory, receptorLogger);
    registry.Register<RequestRedeliveryCommand>(receptor, LifecycleStage.LocalImmediateInline);
    registry.Register<RequestRedeliveryCommand>(receptor, LifecycleStage.PreOutboxInline);
    registry.Register<RequestRedeliveryCommand>(receptor, LifecycleStage.PostInboxInline);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
