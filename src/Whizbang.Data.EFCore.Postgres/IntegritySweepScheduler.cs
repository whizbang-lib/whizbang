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
public sealed partial class IntegritySweepScheduler(
    IServiceProvider services,
    IOptions<StreamIntegrityOptions> options,
    ILogger<IntegritySweepScheduler> logger) : IHostedService {

  /// <summary>The idempotent create-or-update key — one sweep schedule per service.</summary>
  private const string SCHEDULE_KEY = "wh-integrity-sweep";

  /// <summary>Fixed control stream the sweep occurrences live in — a framework-owned stream,
  /// never a domain one.</summary>
  private static readonly Guid _sweepStreamId = Guid.Parse("0f80d000-5eeb-4a9e-8c01-000000000001");

  public async Task StartAsync(CancellationToken cancellationToken) {
    var cron = options.Value.FullSweepCron;
    if (string.IsNullOrWhiteSpace(cron)) {
      return;   // cron disabled — the every-Nth-audit counter stays in charge.
    }
    var manager = services.GetService<IScheduleManager>();
    var instanceProvider = services.GetService<Whizbang.Core.Observability.IServiceInstanceProvider>();
    if (manager is null || instanceProvider is null) {
      LogNoTemporalEngine(logger);
      return;   // no engine — counter fallback; CronActive stays false by design.
    }

    var serviceName = instanceProvider.ServiceName;
    var splayed = SplayCron(cron, serviceName);
    try {
      var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
      var handle = await manager.CreateAsync(new ScheduleDefinition {
        Key = SCHEDULE_KEY,
        StreamId = _sweepStreamId,
        Kind = RecurrenceKind.Cron,
        Cron = splayed,
        EventType = TypeNameFormatter.Format(typeof(ScheduledIntegritySweep)),
        EventDataJson = JsonSerializer.Serialize(
          new ScheduledIntegritySweep(), jsonOptions.GetTypeInfo(typeof(ScheduledIntegritySweep))),
        // The service is its own authority for its own maintenance — there is no interactive
        // principal at 3 AM, and the sweep touches nothing outside this service's database.
        AuthorityPrincipalId = instanceProvider.InstanceId,
      }, cancellationToken).ConfigureAwait(false);

      var state = services.GetService<IntegritySweepScheduleState>();
      if (state is not null) {
        state.CronActive = true;
      }
      LogSweepScheduled(logger, splayed, handle.NextFireAt);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Never let a scheduling failure lose the sweep: CronActive stays false, so the audit
      // worker's counter keeps sweeping on the fallback cadence.
      LogSweepScheduleFailed(logger, ex);
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
    Message = "Integrity sweep scheduled on the temporal engine (cron '{Cron}', next fire {NextFireAt:O}) — the every-Nth-audit counter stands down")]
  static partial void LogSweepScheduled(ILogger logger, string cron, DateTimeOffset nextFireAt);

  [LoggerMessage(EventId = 96, Level = LogLevel.Information,
    Message = "No temporal engine available — the integrity sweep stays on the every-Nth-audit counter fallback")]
  static partial void LogNoTemporalEngine(ILogger logger);

  [LoggerMessage(EventId = 97, Level = LogLevel.Warning,
    Message = "Failed to register the integrity sweep schedule — the every-Nth-audit counter fallback keeps sweeping")]
  static partial void LogSweepScheduleFailed(ILogger logger, Exception ex);
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
    await using var scope = scopeFactory.CreateAsyncScope();
    var runner = scope.ServiceProvider.GetService<IIntegritySweepRunner>();
    if (runner is null) {
      return;
    }
    LogSweepFiring(logger);
    await runner.RunSweepOnceAsync(cancellationToken).ConfigureAwait(false);
  }

  [LoggerMessage(EventId = 98, Level = LogLevel.Information,
    Message = "Scheduled integrity sweep firing — running the full trust-but-verify pass")]
  static partial void LogSweepFiring(ILogger logger);
}

/// <summary>
/// Registers <see cref="ScheduledIntegritySweepReceptor"/> at the three default lifecycle stages —
/// the same runtime-registration rationale as <see cref="ScheduledStreamCloseReceptorRegistrar"/>.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
internal sealed class ScheduledIntegritySweepReceptorRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledIntegritySweepReceptor> receptorLogger) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    var registry = services.GetService<IReceptorRegistry>();
    if (registry is null) {
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
