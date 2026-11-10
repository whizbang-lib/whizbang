// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;

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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S6672:Generic logger injection should match enclosing type", Justification = "The registrar never logs. It receives the logger for the receptor it constructs and hands it straight over, so the category names the type that actually writes the entries.")]
internal sealed class RebuildCommandReceptorRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<RebuildPerspectiveCommandReceptor> receptorLogger) : IHostedService {

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

    var receptor = new RebuildPerspectiveCommandReceptor(scopeFactory, receptorLogger);
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
/// The rebuild command's turnkey registration, shared by the Postgres data layers: the registrar that runtime-registers
/// <see cref="RebuildPerspectiveCommandReceptor"/>, and the background queue and worker a whole-perspective blue-green
/// rebuild is handed to (#1353).
/// </summary>
/// <docs>fundamentals/perspectives/rebuild</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupHostedServiceAuditTests.cs</tests>
internal static class RebuildCommandRegistration {
  /// <summary>
  /// Registers the rebuild command's handler and its background rebuild worker. Idempotent: a second call adds nothing.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  internal static IServiceCollection AddRebuildCommandHandling(this IServiceCollection services) {
    // Factories, not types: naming the constructors keeps activation free of reflection.
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RebuildCommandReceptorRegistrar>(sp =>
      new RebuildCommandReceptorRegistrar(
        sp,
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<RebuildPerspectiveCommandReceptor>>())));
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
