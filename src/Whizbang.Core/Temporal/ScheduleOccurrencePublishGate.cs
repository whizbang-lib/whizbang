using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Temporal;

/// <summary>
/// The temporal <see cref="IOccurrencePublishGate"/> — the thing that actually runs the developer's
/// <see cref="IScheduleFireHook"/> <em>immediately before a scheduled occurrence executes</em>.
/// <para>
/// It claims only messages whose metadata marks them as schedule occurrences (stamped by
/// <c>_wh_spawn_occurrence</c>); everything else proceeds untouched. With no hook registered it also
/// proceeds, so the gate is inert until a developer opts in.
/// </para>
/// <para>
/// Why here and not at spawn time: occurrence creation is an atomic SQL claim+advance (that is what makes
/// it exactly-once), so C# cannot run inside it. Gating <em>execution</em> instead preserves exactly-once
/// creation while still giving the developer the last word before the job runs.
/// </para>
/// </summary>
/// <docs>fundamentals/temporal/pre-fire-hook</docs>
public sealed partial class ScheduleOccurrencePublishGate : IOccurrencePublishGate {
  private const short RUN_SKIPPED = 2;

  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ILogger<ScheduleOccurrencePublishGate> _logger;

  /// <summary>Constructor.</summary>
  public ScheduleOccurrencePublishGate(IServiceScopeFactory scopeFactory, ILogger<ScheduleOccurrencePublishGate> logger) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <inheritdoc />
  public async ValueTask<OccurrencePublishDecision> EvaluateAsync(
      OutboxWork work, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(work);

    // Cheap reject: not a schedule occurrence => nothing to gate.
    if (!TryReadOccurrence(work.MetadataJson, work.MessageId, work.MessageType, out var context)) {
      return OccurrencePublishDecision.Proceed;
    }

    using var scope = _scopeFactory.CreateScope();
    var hook = scope.ServiceProvider.GetService<IScheduleFireHook>();
    if (hook is null) {
      return OccurrencePublishDecision.Proceed;   // no hook registered => unchanged behaviour
    }

    FireDecision decision;
    try {
      decision = await hook.OnBeforeFireAsync(context, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) {
      // A throwing hook must not silently drop the job: fail open (run it) and make the fault loud.
      LogHookFailed(_logger, context.ScheduleId, ex);
      return OccurrencePublishDecision.Proceed;
    }

    var store = scope.ServiceProvider.GetService<IScheduleOccurrenceStore>();

    switch (decision.Action) {
      case FireAction.Skip:
        if (store is not null) {
          await store.LogRunAsync(context.ScheduleId, context.OccurrenceId, RUN_SKIPPED,
            "skipped by pre-fire hook", cancellationToken).ConfigureAwait(false);
        }
        LogSkipped(_logger, context.ScheduleId, context.OccurrenceId);
        return OccurrencePublishDecision.Drop;

      case FireAction.Cancel:
        var manager = scope.ServiceProvider.GetService<IScheduleManager>();
        if (manager is not null) {
          _ = await manager.CancelAsync(context.ScheduleId, expectedVersion: null, cancellationToken)
            .ConfigureAwait(false);
        }
        if (store is not null) {
          await store.LogRunAsync(context.ScheduleId, context.OccurrenceId, RUN_SKIPPED,
            "schedule canceled by pre-fire hook", cancellationToken).ConfigureAwait(false);
        }
        LogCanceled(_logger, context.ScheduleId, context.OccurrenceId);
        return OccurrencePublishDecision.Drop;

      case FireAction.Defer:
        if (store is null) {
          return OccurrencePublishDecision.Proceed;   // can't defer => run it rather than lose it
        }
        await store.DeferAsync(context.OccurrenceId, decision.DeferUntil ?? DateTimeOffset.UtcNow, cancellationToken)
          .ConfigureAwait(false);
        LogDeferred(_logger, context.ScheduleId, context.OccurrenceId);
        return OccurrencePublishDecision.Deferred;

      default:
        // Proceed — optionally writing back a re-resolved authority snapshot for subsequent fires.
        if (decision.RefreshedAuthorityClaimsJson is { } refreshed && store is not null) {
          await store.RefreshAuthorityClaimsAsync(context.ScheduleId, refreshed, cancellationToken)
            .ConfigureAwait(false);
        }
        return OccurrencePublishDecision.Proceed;
    }
  }

  /// <summary>
  /// Reads the occurrence context out of the raw outbox metadata. Returns false for any message that is
  /// not a schedule occurrence (no <c>scheduleId</c>), which is the overwhelmingly common case.
  /// </summary>
  internal static bool TryReadOccurrence(
      string? metadataJson, Guid messageId, string eventType, out ScheduleFireContext context) {
    context = default;
    if (string.IsNullOrWhiteSpace(metadataJson)) {
      return false;
    }

    try {
      using var doc = JsonDocument.Parse(metadataJson);
      var root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object
          || !root.TryGetProperty("scheduleId", out var sid)
          || !sid.TryGetGuid(out var scheduleId)) {
        return false;
      }

      var occurrenceNumber = root.TryGetProperty("occurrence", out var occ) && occ.TryGetInt64(out var n) ? n : 0L;
      var authority = root.TryGetProperty("authorityPrincipalId", out var ap) && ap.TryGetGuid(out var a)
        ? a : Guid.Empty;
      var claims = root.TryGetProperty("authorityClaims", out var ac) && ac.ValueKind is not JsonValueKind.Null
        ? ac.GetRawText() : null;

      context = new ScheduleFireContext(scheduleId, messageId, occurrenceNumber, authority, claims, eventType);
      return true;
    } catch (JsonException) {
      return false;   // unparseable metadata is not an occurrence as far as the gate is concerned
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Pre-fire hook threw for schedule {ScheduleId}; proceeding with the fire (fail open)")]
  private static partial void LogHookFailed(ILogger logger, Guid scheduleId, Exception ex);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "Pre-fire hook skipped occurrence {OccurrenceId} of schedule {ScheduleId}")]
  private static partial void LogSkipped(ILogger logger, Guid scheduleId, Guid occurrenceId);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "Pre-fire hook canceled schedule {ScheduleId} (occurrence {OccurrenceId} dropped)")]
  private static partial void LogCanceled(ILogger logger, Guid scheduleId, Guid occurrenceId);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information,
    Message = "Pre-fire hook deferred occurrence {OccurrenceId} of schedule {ScheduleId}")]
  private static partial void LogDeferred(ILogger logger, Guid scheduleId, Guid occurrenceId);
}
