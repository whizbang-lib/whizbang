// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Signals;

/// <summary>
/// A role assignment was released (#2046): a holder stopped, stood down or handed the role to a newer release. Every
/// instance that votes for the role re-votes at once instead of at its next renewal. A decision, so it is durable: an
/// instance that missed the notification is told by its tail.
/// </summary>
/// <param name="Role">The role that was released.</param>
/// <docs>fundamentals/signal-bus/signal-bus#coordination-signals</docs>
[WireName("role-released")]
public readonly record struct RoleReleasedSignal(string Role) : IPayloadSignal<RoleReleasedSignal> {
  /// <inheritdoc />
  public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
  /// <inheritdoc />
  public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  /// <inheritdoc />
  public string Payload => Role;
  /// <inheritdoc />
  public static RoleReleasedSignal FromPayload(string payload) => new(payload);
}

/// <summary>
/// The partition assigner published an assignment (#2046). Claimers mark their cached copy stale unless they already
/// hold this version. A decision, so it is durable: an instance that missed the notification is told by its tail.
/// </summary>
/// <param name="Version">The published version, <c>epoch:revision</c>.</param>
/// <docs>fundamentals/signal-bus/signal-bus#coordination-signals</docs>
[WireName("partition-assignment-published")]
public readonly record struct PartitionAssignmentPublishedSignal(string Version) : IPayloadSignal<PartitionAssignmentPublishedSignal> {
  /// <inheritdoc />
  public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
  /// <inheritdoc />
  public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  /// <inheritdoc />
  public string Payload => Version;
  /// <inheritdoc />
  public static PartitionAssignmentPublishedSignal FromPayload(string payload) => new(payload);
}

/// <summary>
/// Events were committed that the commit-order stamper has not stamped (#2046): the stamper's doorbell, rung once per
/// committing batch after commit. Best-effort: the stamper's poll is its backstop.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus#coordination-signals</docs>
[WireName("committed")]
public readonly record struct RowsCommittedSignal : ISignal {
  /// <inheritdoc />
  public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
  /// <inheritdoc />
  public static SignalTargeting Targeting => SignalTargeting.Broadcast;
}

/// <summary>
/// A multi-stream command run was recorded (#2046): each instance claims its share of the run's streams. Best-effort:
/// the multi-stream worker's poll is its backstop.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus#coordination-signals</docs>
[WireName("multi-stream-work")]
public readonly record struct MultiStreamWorkSignal : ISignal {
  /// <inheritdoc />
  public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
  /// <inheritdoc />
  public static SignalTargeting Targeting => SignalTargeting.Broadcast;
}
