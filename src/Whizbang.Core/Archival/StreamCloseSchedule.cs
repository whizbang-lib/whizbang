using System.Text.Json;
using Whizbang.Core.Serialization;
using Whizbang.Core.Temporal;

namespace Whizbang.Core.Archival;

/// <summary>
/// A1 — builds the <see cref="ScheduleDefinition"/> for a recurring "close the books" schedule whose
/// occurrences are <see cref="ScheduledStreamClose"/> events (handled by
/// <see cref="ScheduledStreamCloseReceptor"/>). Pass the result to
/// <see cref="Whizbang.Core.Temporal.IScheduleManager.CreateAsync"/>. A static builder — no DI — so the
/// domain owns the close-point (<see cref="ScheduledStreamClose.ThroughVersion"/>) and updates it per period.
/// </summary>
/// <remarks>
/// The <c>schedulerStreamId</c> (the stream the occurrences live in) should be a dedicated control stream,
/// distinct from <see cref="ScheduledStreamClose.StreamId"/> (the target being closed), so a close occurrence
/// never lands in the stream it is about to truncate.
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events</docs>
public static class StreamCloseSchedule {
  /// <summary>Serializes the close payload with the framework's combined (source-generated) JSON type info so
  /// the occurrence round-trips back to <see cref="ScheduledStreamClose"/> on dispatch — AOT-safe.</summary>
  private static string _payload(ScheduledStreamClose close) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return JsonSerializer.Serialize(close, options.GetTypeInfo(typeof(ScheduledStreamClose)));
  }

  private static readonly string _eventType = TypeNameFormatter.Format(typeof(ScheduledStreamClose));

  /// <summary>A recurring close on a fixed <paramref name="interval"/> (e.g. every 30 days).</summary>
  public static ScheduleDefinition Recurring(
      string key, Guid schedulerStreamId, ScheduledStreamClose close, TimeSpan interval, Guid authorityPrincipalId) =>
    new() {
      Key = key,
      StreamId = schedulerStreamId,
      Kind = RecurrenceKind.Interval,
      Interval = interval,
      EventType = _eventType,
      EventDataJson = _payload(close),
      AuthorityPrincipalId = authorityPrincipalId,
    };

  /// <summary>A recurring close on a cron schedule (e.g. <c>"0 0 1 * *"</c> — midnight on the 1st, "close the
  /// month"), evaluated in <paramref name="timeZone"/> (default UTC).</summary>
  public static ScheduleDefinition RecurringCron(
      string key, Guid schedulerStreamId, ScheduledStreamClose close, string cron, Guid authorityPrincipalId,
      string? timeZone = null) =>
    new() {
      Key = key,
      StreamId = schedulerStreamId,
      Kind = RecurrenceKind.Cron,
      Cron = cron,
      TimeZone = timeZone,
      EventType = _eventType,
      EventDataJson = _payload(close),
      AuthorityPrincipalId = authorityPrincipalId,
    };
}
