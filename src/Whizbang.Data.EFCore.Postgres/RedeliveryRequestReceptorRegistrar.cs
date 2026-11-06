// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Registers <see cref="RedeliveryRequestReceptor"/> with <see cref="IReceptorRegistry"/> at startup.
/// <see cref="RequestRedeliveryCommand"/> is a framework command defined in <c>Whizbang.Core</c>, and
/// source-generated receptor discovery only sees the consumer's own syntax, so a built-in receptor needs
/// runtime registration to join the dispatch pipeline. Registered at the three stages a framework receptor needs: <see cref="LifecycleStage.LocalImmediateInline"/>, where local
/// dispatch finds a runtime handler; <see cref="LifecycleStage.PreOutboxInline"/>, where the sending service handles an
/// outbox copy that nothing dispatched locally; and <see cref="LifecycleStage.PostInboxInline"/>, for a copy arriving
/// from another service. So the
/// command reaches it whether dispatched in-process (operator-initiated) or arriving over the inbox from
/// a damaged consumer. Registered deferred (<see cref="IntegrityDeliveryRegistration"/>, #2001): every stage records the message for the maintainer's integrity delivery drain. No-ops if no registry is present (schema-only / diagnostic hosts still boot).
/// <see cref="AuditRebuildRequestReceptor"/> joins the same three stages for the same reasons: an operator dispatches
/// <see cref="Whizbang.Core.Commands.System.RequestAuditRebuildCommand"/> on the origin, or the audit service sends it
/// there (#1724).
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryRequestReceptorTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S6672:Generic logger injection should match enclosing type", Justification = "The registrar never logs. It receives the loggers for the receptors it constructs and hands them straight over, so each category names the type that actually writes the entries.")]
internal sealed class RedeliveryRequestReceptorRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<RedeliveryRequestReceptor> receptorLogger,
    ILogger<AuditRebuildRequestReceptor> auditRebuildLogger,
    ILogger<BackfillWindowAnswerReceptor> backfillAnswerLogger) : IHostedService {
#pragma warning disable CA1822, S2325, RCS1163, S1144, IDE0052, IDE0060, RCS1213, CS9113 // RED stub (#2092)
  private readonly ILogger<BackfillWindowAnswerReceptor> _backfillAnswerLogger = backfillAnswerLogger;
#pragma warning restore CA1822, S2325, RCS1163, S1144, IDE0052, IDE0060, RCS1213, CS9113

  public Task StartAsync(CancellationToken cancellationToken) {
    var registry = services.GetService<IReceptorRegistry>();
    // INullDefault, not null: every framework dependency resolves now, so "no registry" arrives as
    // the turnkey default rather than as an absent service. Registering into it throws by design,
    // which would take the whole host down on a service that simply declares no receptors.
    if (registry is null or INullDefault) {
      return Task.CompletedTask;
    }
    // Deferred at every stage: building a redelivery runs on the maintainer's integrity delivery drain (#2001).
    registry.RegisterIntegrityReceptor(new RedeliveryRequestReceptor(scopeFactory, receptorLogger), services, scopeFactory);
    var auditRebuild = new AuditRebuildRequestReceptor(scopeFactory, auditRebuildLogger);
    registry.Register<Whizbang.Core.Commands.System.RequestAuditRebuildCommand>(auditRebuild, LifecycleStage.LocalImmediateInline);
    registry.Register<Whizbang.Core.Commands.System.RequestAuditRebuildCommand>(auditRebuild, LifecycleStage.PreOutboxInline);
    registry.Register<Whizbang.Core.Commands.System.RequestAuditRebuildCommand>(auditRebuild, LifecycleStage.PostInboxInline);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
