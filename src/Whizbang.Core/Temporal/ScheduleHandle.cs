namespace Whizbang.Core.Temporal;

/// <summary>
/// The result of creating (or idempotently updating) a schedule: its id, the computed next fire time,
/// and whether this call created a new schedule (<c>false</c> when an existing keyed schedule was updated).
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public readonly record struct ScheduleHandle(Guid ScheduleId, DateTimeOffset NextFireAt, bool WasCreated);
