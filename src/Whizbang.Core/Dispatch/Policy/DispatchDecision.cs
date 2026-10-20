// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Dispatch.Policy;

/// <summary>When this service runs its local handlers for a dispatched message.</summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision</docs>
public enum LocalRun : byte {
  /// <summary>The local handlers do not run.</summary>
  None = 0,

  /// <summary>The local handlers run during the dispatch call.</summary>
  Now = 1,

  /// <summary>The local handlers run when the scheduled copy comes back from the outbox.</summary>
  AtScheduledTime = 2,
}

/// <summary>Why a dispatch is refused. Each value maps to the exception the caller has always seen.</summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision</docs>
public enum DispatchRejection : byte {
  /// <summary>The dispatch goes ahead.</summary>
  None = 0,

  /// <summary>A handler is required and this service has none.</summary>
  NoHandler = 1,

  /// <summary>A command was routed to the event store only; commands are not stored.</summary>
  CommandCannotBeStoreOnly = 2,

  /// <summary>The message has to reach the outbox and this service has none.</summary>
  NoOutbox = 3,

  /// <summary>A future schedule was asked for, but the message never leaves this service, so nothing can deliver it later.</summary>
  CannotScheduleInProcess = 4,

  /// <summary>The route sends the message nowhere (<see cref="DispatchModes.None"/>).</summary>
  NoRoute = 5,

  /// <summary>The caller asked both to fail and to continue when there is no local handler.</summary>
  ConflictingBehaviors = 6,

  /// <summary>The caller needs the handler's result, but the handler does not run during the call.</summary>
  ResultUnavailable = 7,

  /// <summary>A message that is not an event was routed to the event store only; only events are stored.</summary>
  NotAnEventCannotBeStoreOnly = 8,
}

/// <summary>The rule that last shaped a decision, recorded for logs and traces.</summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision</docs>
public enum DispatchReason : byte {
  /// <summary>The baseline: local handlers when present, plus the outbox.</summary>
  Default = 0,

  /// <summary>The caller's route decided.</summary>
  RouteDecides = 1,

  /// <summary>A local call never writes the outbox.</summary>
  LocalCallNeverSendsOut = 2,

  /// <summary>An owned command stays in this service.</summary>
  OwnedCommandStaysLocal = 3,

  /// <summary>A system command reaches every service.</summary>
  SystemCommandBroadcasts = 4,

  /// <summary>A command cannot be routed to the event store only.</summary>
  CommandCannotBeStoreOnly = 5,

  /// <summary>A handler is required.</summary>
  HandlerRequired = 6,

  /// <summary>The message is scheduled for later.</summary>
  Scheduled = 7,

  /// <summary>No outbox is configured.</summary>
  NoOutbox = 8,

  /// <summary>A developer hook changed the decision.</summary>
  Hook = 9,

  /// <summary>A message that is not an event cannot be routed to the event store only.</summary>
  NotAnEventCannotBeStoreOnly = 10,
}

/// <summary>
/// What the dispatcher does with one message. The executor carries it out exactly.
/// </summary>
/// <param name="Local">When the local handlers run.</param>
/// <param name="WriteOutbox">True to write the message to the outbox for other services.</param>
/// <param name="WriteEventStoreOnly">True to store an event without sending it anywhere.</param>
/// <param name="DeferUntil">The time the outbox copy is delivered at, or null for now.</param>
/// <param name="Reject">Why the dispatch is refused, or <see cref="DispatchRejection.None"/>.</param>
/// <param name="Reason">The rule that last shaped the decision.</param>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision</docs>
public readonly record struct DispatchDecision(
  LocalRun Local,
  bool WriteOutbox,
  bool WriteEventStoreOnly,
  DateTimeOffset? DeferUntil,
  DispatchRejection Reject,
  DispatchReason Reason) {

  /// <summary>
  /// True when this service handles the message during the call and also writes it to the outbox, so
  /// the outbox copy is marked as already handled here and this service does not handle it again. A
  /// scheduled copy is not marked: this service handles it when it comes back.
  /// </summary>
  public bool MarkHandledLocally => Local == LocalRun.Now && WriteOutbox;

  /// <summary>True when the dispatch is refused.</summary>
  public bool IsRejected => Reject != DispatchRejection.None;

  /// <summary>A refusal with the given cause.</summary>
  public static DispatchDecision Rejected(DispatchRejection reject, DispatchReason reason) =>
    new(LocalRun.None, WriteOutbox: false, WriteEventStoreOnly: false, DeferUntil: null, reject, reason);
}
