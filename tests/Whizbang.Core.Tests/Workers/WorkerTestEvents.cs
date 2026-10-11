// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;

namespace Whizbang.Core.Tests.Workers;

// Event types shared by the unit and component projects (one source, compiled into both): the worker
// tests and their priority tests resolve the same types.

/// <summary>The event SubscriptionExpansionWorkerTests and SubscriptionExpansionWorkerPriorityTests expand.</summary>
public static class SubscriptionExpansionTestEvents {
  /// <summary>An event with a stream id, as a subscription expansion would carry.</summary>
  public sealed record ExpandedEvent : IEvent {
    [StreamId]
    public Guid Sid { get; init; }
  }
}

/// <summary>The event IntegrityAuditWorkerTests and IntegrityAuditWorkerPriorityTests audit.</summary>
public static class IntegrityAuditTestEvents {
  /// <summary>An event with a stream id, as an audited stream would carry.</summary>
  public sealed record AuditProbeEvent : IEvent {
    [StreamId]
    public Guid Sid { get; init; }
  }
}
