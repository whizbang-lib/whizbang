// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.Temporal;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// #2353: one schedule per enabled opt-in check, the full-history compare (key <c>wh-integrity-sweep</c>, cron
/// <see cref="FullHistoryCompareOptions.Schedule"/>) and the sealed-history re-check (key <c>wh-seal-recheck</c>, cron
/// <see cref="SealRecheckOptions.Schedule"/>); each occurrence names its check. #2586: a check that is off, or on with an
/// empty schedule, has the schedule stored under its key canceled at every start, so one registered while the check was
/// on a cron stops firing (a removal that fails is logged, and the occurrence handler still ignores a check that is off).
/// #80-D: puts the full integrity sweep on the temporal engine's clock. Registers an
/// idempotent-by-key cron schedule whose occurrences are <see cref="ScheduledIntegritySweep"/>
/// commands, so the heaviest verification runs at a configured IDLE hour instead of wherever the
/// every-Nth-audit counter happens to land it — the counter observed live ran the full-store
/// recompute at peak load on a short audit interval. On success it flips
/// <see cref="IntegritySweepScheduleState.CronActive"/>, which stands the counter down; any
/// failure (or a host without the engine) leaves the counter fallback in charge — the sweep is
/// never silently lost.
/// </summary>
/// <remarks>
/// SPLAY: when the configured cron's minute field is <c>0</c> (the default), it is replaced with
/// a stable per-service minute (FNV-1a of the service name, mod 60) so a fleet sharing one
/// database server does not sweep in unison. Stability matters: a random splay would re-randomize
/// on every restart, re-creating the very collisions it exists to prevent. An explicit non-zero
/// minute is honored verbatim.
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegritySweepSchedulingTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegritySweepScheduleRemovalTests.cs</tests>
public sealed partial class IntegritySweepScheduler(
    IServiceProvider services,
    IOptions<StreamIntegrityOptions> options,
    ILogger<IntegritySweepScheduler> logger) : IHostedService, Whizbang.Core.Observability.IDbMechanismOwner {
  /// <inheritdoc />
  public Whizbang.Core.Observability.DbMechanism DbMechanism => Whizbang.Core.Observability.DbMechanism.IntegrityAudit;


  /// <summary>The idempotent create-or-update key of the full-history compare's schedule (the key the sweep always had).</summary>
  private const string SCHEDULE_KEY = "wh-integrity-sweep";

  /// <summary>The idempotent create-or-update key of the sealed-history re-check's schedule (#2353).</summary>
  private const string RECHECK_SCHEDULE_KEY = "wh-seal-recheck";

  /// <summary>Fixed control stream the sweep occurrences live in — a framework-owned stream,
  /// never a domain one.</summary>
  private static readonly Guid _sweepStreamId = Guid.Parse("0f80d000-5eeb-4a9e-8c01-000000000001");

  public async Task StartAsync(CancellationToken cancellationToken) {
    var compare = options.Value.FullHistoryCompare;
    var recheck = options.Value.SealRecheck;
    var wantCompare = compare.Enabled && !string.IsNullOrWhiteSpace(compare.Schedule);
    var wantRecheck = recheck.Enabled && !string.IsNullOrWhiteSpace(recheck.Schedule);
    var manager = services.GetService<IScheduleManager>();
    if (manager is null) {
      if (wantCompare || wantRecheck) {
        LogNoTemporalEngine(logger);
      }
      return;   // no engine: nothing stored to remove, counter fallback; the cron flags stay false by design.
    }

    // #2586: a check that wants no schedule (off, or on the every-Nth-audit counter) ends with none under its key.
    if (!wantCompare) {
      await _removeAsync(manager, SCHEDULE_KEY, IntegrityCheck.FullHistoryCompare, cancellationToken).ConfigureAwait(false);
    }
    if (!wantRecheck) {
      await _removeAsync(manager, RECHECK_SCHEDULE_KEY, IntegrityCheck.SealRecheck, cancellationToken).ConfigureAwait(false);
    }
    if (!wantCompare && !wantRecheck) {
      return;
    }
    var instanceProvider = services.GetService<Whizbang.Core.Observability.IServiceInstanceProvider>();
    if (instanceProvider is null) {
      LogNoTemporalEngine(logger);
      return;
    }

    // #1334: the schedule function arrives with the migrations. The host starts this scheduler at Accept commands
    // (#1636), so the schema is in place when it registers.
    var state = services.GetService<IntegritySweepScheduleState>();
    if (wantCompare && await _registerAsync(manager, instanceProvider, SCHEDULE_KEY, compare.Schedule!,
        IntegrityCheck.FullHistoryCompare, cancellationToken).ConfigureAwait(false) && state is not null) {
      state.CronActive = true;
    }
    if (wantRecheck && await _registerAsync(manager, instanceProvider, RECHECK_SCHEDULE_KEY, recheck.Schedule!,
        IntegrityCheck.SealRecheck, cancellationToken).ConfigureAwait(false) && state is not null) {
      state.SealRecheckCronActive = true;
    }
  }

  private async Task<bool> _registerAsync(
      IScheduleManager manager, Whizbang.Core.Observability.IServiceInstanceProvider instanceProvider,
      string key, string cron, IntegrityCheck check, CancellationToken cancellationToken) {
    var serviceName = instanceProvider.ServiceName;
    var splayed = SplayCron(cron, serviceName);
    try {
      var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
      var handle = await manager.CreateAsync(new ScheduleDefinition {
        Key = key,
        StreamId = _sweepStreamId,
        Kind = RecurrenceKind.Cron,
        Cron = splayed,
        EventType = TypeNameFormatter.Format(typeof(ScheduledIntegritySweep)),
        EventDataJson = JsonSerializer.Serialize(
          new ScheduledIntegritySweep { Check = check }, jsonOptions.GetTypeInfo(typeof(ScheduledIntegritySweep))),
        // The service is its own authority for its own maintenance — there is no interactive
        // principal at 3 AM, and the sweep touches nothing outside this service's database.
        AuthorityPrincipalId = instanceProvider.InstanceId,
      }, cancellationToken).ConfigureAwait(false);
      LogSweepScheduled(logger, check, splayed, handle.NextFireAt);
      return true;
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Never let a scheduling failure lose the check: its cron flag stays false, so the audit
      // worker's counter keeps running it on the fallback cadence.
      LogSweepScheduleFailed(logger, check, ex);
      return false;
    }
  }

  private async Task _removeAsync(IScheduleManager manager, string key, IntegrityCheck check, CancellationToken cancellationToken) {
    try {
      if (await manager.CancelByKeyAsync(key, cancellationToken).ConfigureAwait(false)) {
        LogScheduleRemoved(logger, check, key);
      }
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // The start goes on: the occurrence handler turns an occurrence of a check that is off into a no-op.
      LogScheduleRemoveFailed(logger, check, key, ex);
    }
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <summary>Replaces a default (<c>0</c>) minute field with the service's stable splay minute.</summary>
  internal static string SplayCron(string cron, string serviceName) {
    var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 5 || parts[0] != "0") {
      return cron;   // non-default minute (or a shape we do not own) — honored verbatim.
    }
    parts[0] = (_fnv1a(serviceName) % 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
    return string.Join(' ', parts);
  }

  /// <summary>FNV-1a — deterministic across processes (string.GetHashCode is randomized per run,
  /// which would re-randomize the splay on every restart).</summary>
  private static uint _fnv1a(string value) {
    var hash = 2166136261u;
    foreach (var c in value) {
      hash = (hash ^ c) * 16777619u;
    }
    return hash;
  }

  [LoggerMessage(EventId = 95, Level = LogLevel.Information,
    Message = "Integrity check {Check} scheduled on the temporal engine (cron '{Cron}', next fire {NextFireAt:O}); the every-Nth-audit counter stands down for it")]
  static partial void LogSweepScheduled(ILogger logger, IntegrityCheck check, string cron, DateTimeOffset nextFireAt);

  [LoggerMessage(EventId = 96, Level = LogLevel.Information,
    Message = "No temporal engine available — the integrity sweep stays on the every-Nth-audit counter fallback")]
  static partial void LogNoTemporalEngine(ILogger logger);

  [LoggerMessage(EventId = 97, Level = LogLevel.Warning,
    Message = "Failed to register the schedule of integrity check {Check}; the every-Nth-audit counter fallback keeps running it")]
  static partial void LogSweepScheduleFailed(ILogger logger, IntegrityCheck check, Exception ex);

  [LoggerMessage(EventId = 101, Level = LogLevel.Information,
    Message = "Integrity check {Check} wants no schedule (off, or on with no Schedule); its stored schedule '{Key}' is canceled")]
  static partial void LogScheduleRemoved(ILogger logger, IntegrityCheck check, string key);

  [LoggerMessage(EventId = 102, Level = LogLevel.Warning,
    Message = "Failed to cancel the stored schedule '{Key}' of integrity check {Check}; it may keep firing until a later start removes it")]
  static partial void LogScheduleRemoveFailed(ILogger logger, IntegrityCheck check, string key, Exception ex);
}

/// <summary>
/// #80-D: reacts to the scheduled sweep occurrence by running one full sweep. Runtime-registered
/// (framework receptors in a driver assembly are invisible to consumer-side source-generated
/// discovery); inert when no <see cref="IIntegritySweepRunner"/> is registered — schema-only
/// hosts still boot and dispatch.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegritySweepSchedulingTests.cs</tests>
public sealed partial class ScheduledIntegritySweepReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledIntegritySweepReceptor> logger) : IReceptor<ScheduledIntegritySweep> {

  /// <inheritdoc />
  public async ValueTask HandleAsync(ScheduledIntegritySweep message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    await using var scope = scopeFactory.CreateAsyncScope();
    var runner = scope.ServiceProvider.GetService<IIntegritySweepRunner>();
    if (runner is null) {
      return;
    }
    // An occurrence scheduled before #2353 names no check: its schedule was the full sweep's.
    var check = message.Check ?? IntegrityCheck.FullHistoryCompare;
    LogSweepFiring(logger, check);
    await runner.RunCheckOnceAsync(check, cancellationToken).ConfigureAwait(false);
  }

  [LoggerMessage(EventId = 98, Level = LogLevel.Information,
    Message = "Scheduled integrity check {Check} firing")]
  static partial void LogSweepFiring(ILogger logger, IntegrityCheck check);
}

/// <summary>
/// Registers <see cref="ScheduledIntegritySweepReceptor"/> at the same three stages, for the same runtime-registration
/// rationale, as <see cref="ScheduledPrefixTruncateReceptorRegistrar"/>.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S6672:Generic logger injection should match enclosing type", Justification = "The registrar never logs. It receives the logger for the receptor it constructs and hands it straight over, so the category names the type that actually writes the entries.")]
internal sealed class ScheduledIntegritySweepReceptorRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledIntegritySweepReceptor> receptorLogger) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    var registry = services.GetService<IReceptorRegistry>();
    if (registry is null or INullDefault) {
      return Task.CompletedTask;
    }
    var receptor = new ScheduledIntegritySweepReceptor(scopeFactory, receptorLogger);
    registry.Register<ScheduledIntegritySweep>(receptor, LifecycleStage.LocalImmediateInline);
    registry.Register<ScheduledIntegritySweep>(receptor, LifecycleStage.PreOutboxInline);
    registry.Register<ScheduledIntegritySweep>(receptor, LifecycleStage.PostInboxInline);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
