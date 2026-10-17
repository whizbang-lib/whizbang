// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Dispatch.Policy;

/// <summary>
/// What a dispatch policy knows about the message type, computed once per type without the payload.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#situation</docs>
[Flags]
public enum MessageTraits : byte {
  /// <summary>No trait known.</summary>
  None = 0,

  /// <summary>The message is a command: one handler is expected to own it.</summary>
  Command = 1,

  /// <summary>The message is an event: any number of services may handle it.</summary>
  Event = 2,

  /// <summary>A framework system command, broadcast to every service.</summary>
  SystemCommand = 4,

  /// <summary>A composite event whose parts are also dispatched.</summary>
  Composite = 8,

  /// <summary>The message type is in one of this service's owned namespaces.</summary>
  Owned = 16,
}

/// <summary>
/// The local handlers this service has for a message type.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#situation</docs>
[Flags]
public enum LocalHandlers : byte {
  /// <summary>This service has no local handler for the message.</summary>
  None = 0,

  /// <summary>A source-generated receptor, in this assembly or another one.</summary>
  Generated = 1,

  /// <summary>A receptor registered at runtime at a local stage.</summary>
  Runtime = 2,

  /// <summary>A receptor declared <c>[FireAt]</c> a local stage, which runs alongside the handler.</summary>
  ExplicitLocalStageHook = 4,
}

/// <summary>
/// The facts of one dispatch that a policy decides on. It carries no payload, so a decision depends
/// only on the message type, the local handlers, the route and the schedule.
/// </summary>
/// <param name="MessageType">The message type being dispatched.</param>
/// <param name="Traits">What is known about the message type.</param>
/// <param name="Handlers">The local handlers this service has for it.</param>
/// <param name="Route">The route the caller chose, through a <c>Route</c> wrapper or <c>[DefaultRouting]</c>; null when none was chosen.</param>
/// <param name="ScheduledFor">The time the caller asked the message to be delivered at, or null.</param>
/// <param name="IsScheduledInFuture">True when <paramref name="ScheduledFor"/> is later than the dispatcher's clock.</param>
/// <param name="OutboxAvailable">True when this service can write to an outbox.</param>
/// <docs>fundamentals/dispatcher/dispatch-policy#situation</docs>
public readonly record struct DispatchSituation(
  Type MessageType,
  MessageTraits Traits,
  LocalHandlers Handlers,
  DispatchModes? Route,
  DateTimeOffset? ScheduledFor,
  bool IsScheduledInFuture,
  bool OutboxAvailable) {

  /// <summary>True when the message is a command (including a system command).</summary>
  public bool IsCommand => (Traits & (MessageTraits.Command | MessageTraits.SystemCommand)) != 0;

  /// <summary>True when the message type is in one of this service's owned namespaces.</summary>
  public bool IsOwned => (Traits & MessageTraits.Owned) != 0;

  /// <summary>True when the message is a framework system command.</summary>
  public bool IsSystemCommand => (Traits & MessageTraits.SystemCommand) != 0;

  /// <summary>True when this service has any local handler for the message.</summary>
  public bool HasLocalHandler => (Handlers & (LocalHandlers.Generated | LocalHandlers.Runtime)) != 0;
}
