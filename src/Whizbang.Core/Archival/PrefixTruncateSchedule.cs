// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Whizbang.Core.Serialization;
using Whizbang.Core.Temporal;

namespace Whizbang.Core.Archival;

/// <summary>
/// Builds the <see cref="ScheduleDefinition"/> of a recurring prefix truncate, whose occurrences are
/// <see cref="ScheduledPrefixTruncate"/> commands handled by the built-in <c>ScheduledPrefixTruncateReceptor</c>. Pass the
/// result to <see cref="Whizbang.Core.Temporal.IScheduleManager.CreateAsync"/>. A static builder with no DI, so the domain
/// owns the cut (<see cref="ScheduledPrefixTruncate.ThroughVersion"/>) and advances it each period. Named
/// <c>StreamCloseSchedule</c> until #1760.
/// </summary>
/// <remarks>
/// The <c>schedulerStreamId</c> (the stream the occurrences live in) should be a dedicated control stream, distinct from
/// <see cref="ScheduledPrefixTruncate.StreamId"/> (the stream truncated), so an occurrence never lands in the stream it is
/// about to truncate.
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#scheduling-a-prefix-truncate</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/PrefixTruncateScheduleTests.cs</tests>
public static class PrefixTruncateSchedule {
  /// <summary>Serializes the payload with the framework's combined (source-generated) JSON type info, so the occurrence
  /// round-trips back to <see cref="ScheduledPrefixTruncate"/> on dispatch without reflection.</summary>
  private static string _payload(ScheduledPrefixTruncate close) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return JsonSerializer.Serialize(close, options.GetTypeInfo(typeof(ScheduledPrefixTruncate)));
  }

  private static readonly string _eventType = TypeNameFormatter.Format(typeof(ScheduledPrefixTruncate));

  /// <summary>A recurring prefix truncate on a fixed <paramref name="interval"/> (for example every 30 days).</summary>
  public static ScheduleDefinition Recurring(
      string key, Guid schedulerStreamId, ScheduledPrefixTruncate close, TimeSpan interval, Guid authorityPrincipalId) =>
    new() {
      Key = key,
      StreamId = schedulerStreamId,
      Kind = RecurrenceKind.Interval,
      Interval = interval,
      EventType = _eventType,
      EventDataJson = _payload(close),
      AuthorityPrincipalId = authorityPrincipalId,
    };

  /// <summary>A recurring prefix truncate on a cron schedule (for example <c>"0 0 1 * *"</c>, midnight on the first of
  /// each month), evaluated in <paramref name="timeZone"/> (default UTC).</summary>
  public static ScheduleDefinition RecurringCron(
      string key, Guid schedulerStreamId, ScheduledPrefixTruncate close, string cron, Guid authorityPrincipalId,
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
