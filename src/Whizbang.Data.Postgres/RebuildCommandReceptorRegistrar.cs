// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Hosted service that registers <see cref="RebuildPerspectiveCommandReceptor"/> with
/// <see cref="IReceptorRegistry"/> at application startup. The command is a system-level
/// command defined in <c>Whizbang.Core</c>; source-generated receptor discovery only sees
/// the consumer's own syntax, so a built-in receptor shipped from this shared Postgres assembly
/// needs runtime registration to participate in the dispatch pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Registered at <see cref="LifecycleStage.LocalImmediateInline"/>, where local dispatch finds a runtime
/// handler; <see cref="LifecycleStage.PreOutboxInline"/>, where the sending service handles an outbox copy
/// that nothing dispatched locally; and <see cref="LifecycleStage.PostInboxInline"/>, for a copy arriving
/// from another service.
/// </para>
/// <para>
/// <see cref="RebuildPerspectiveCommand"/> is a framework system command, so
/// <c>IDispatcher.SendAsync</c> broadcasts it to every service. The sending service rebuilds in-process
/// before the send returns, and its outbox copy is flagged as locally dispatched, so the invoker skips
/// this receptor at PreOutbox. Every other service rebuilds at PostInbox; the invoker's same-service
/// filter stops the sender's own copy coming back. Each service therefore rebuilds exactly once.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild</docs>
internal sealed class RebuildCommandReceptorRegistrar(IServiceProvider services) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    // Receptor registry is optional — hosts that wire the Postgres driver without a dispatcher
    // (minimal diagnostic hosts, schema-only tools) should still boot.
    var registry = services.GetService<IReceptorRegistry>();
    // INullDefault, not null: every framework dependency resolves now, so "no registry" arrives as
    // the turnkey default rather than as an absent service. Registering into it throws by design,
    // which would take the whole host down on a service that simply declares no receptors.
    if (registry is null or INullDefault) {
      return Task.CompletedTask;
    }

    // The same handling the integrity audit runs a repair through (#2344): one instance, one behavior. Resolved only
    // once there is a registry to register into, so a host without receptors needs none of the rebuild services.
    var receptor = new RebuildPerspectiveCommandReceptor(services.GetRequiredService<PerspectiveRebuildRequestHandler>());
    // LocalImmediateInline: the sender's own copy, run by local dispatch. PreOutboxInline: an
    // outbox row nothing dispatched locally. PostInboxInline: every other service's copy.
    registry.Register<RebuildPerspectiveCommand>(receptor, LifecycleStage.LocalImmediateInline);
    registry.Register<RebuildPerspectiveCommand>(receptor, LifecycleStage.PreOutboxInline);
    registry.Register<RebuildPerspectiveCommand>(receptor, LifecycleStage.PostInboxInline);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The rebuild command's turnkey registration, shared by the Postgres data layers so the command is handled the same way
/// whichever one a host runs (#1959): the registrar that runtime-registers
/// <see cref="RebuildPerspectiveCommandReceptor"/>, and the background queue and worker a whole-perspective blue-green
/// rebuild is handed to (#1353).
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#on-every-data-layer</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/StartupParityTests.cs:EachDriver_HandlesTheRebuildCommand_AndRunsItsBackgroundRebuildsAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperRebuildCommandTests.cs:AddWhizbangPostgres_RegistersTheRebuildCommandHandling_ThroughBothOverloadsAsync</tests>
internal static class RebuildCommandRegistration {
  /// <summary>
  /// Registers the rebuild command's handler and its background rebuild worker. Idempotent: a second call adds nothing.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  internal static IServiceCollection AddRebuildCommandHandling(this IServiceCollection services) {
    // Factories, not types: naming the constructors keeps activation free of reflection.
    // #2344: one rebuild handling, run by the receptor for a command that reached this service and by the integrity
    // audit, in-process, for a coverage gap in this service's own perspective. Its entries log under the receptor's
    // category, where they always have.
    services.TryAddSingleton(sp => new PerspectiveRebuildRequestHandler(
      sp.GetRequiredService<IServiceScopeFactory>(),
      sp.GetRequiredService<ILogger<RebuildPerspectiveCommandReceptor>>()));
    services.TryAddSingleton<IPerspectiveRebuildRequestHandler>(sp => sp.GetRequiredService<PerspectiveRebuildRequestHandler>());
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RebuildCommandReceptorRegistrar>(sp =>
      new RebuildCommandReceptorRegistrar(sp)));
    // #1353: a whole-perspective blue-green rebuild runs on this worker, off the inbox handler's budget.
    services.TryAddSingleton(_ => new RebuildBackgroundQueue());
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RebuildBackgroundWorker>(sp =>
      new RebuildBackgroundWorker(
        sp.GetRequiredService<RebuildBackgroundQueue>(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<RebuildBackgroundWorker>>())));
    return services;
  }
}
