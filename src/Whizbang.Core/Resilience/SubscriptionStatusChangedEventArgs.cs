// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Transports;

namespace Whizbang.Core.Resilience;

/// <summary>
/// Describes one change of a destination's subscription status, raised by
/// <see cref="Workers.TransportConsumerWorker.SubscriptionStatusChanged"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PreviousStatus"/> and <see cref="Status"/> are the two values of this one change. <see cref="State"/>
/// is the live state, which keeps changing after the event is raised; read <see cref="Status"/> for the value this
/// change set, and <see cref="State"/> for the fields recorded with it (<see cref="SubscriptionState.LastError"/>,
/// <see cref="SubscriptionState.AttemptCount"/>, <see cref="SubscriptionState.Subscription"/>).
/// </para>
/// </remarks>
/// <param name="state">The state whose status changed.</param>
/// <param name="previousStatus">The status before the change.</param>
/// <param name="status">The status the change set.</param>
/// <docs>messaging/transports/transport-consumer#subscription-status-changes</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWorkerSubscriptionStatusChangedTests.cs:SubscriptionStatusChanged_EveryDestinationSubscribes_RaisedPendingToHealthyAfterTheStateIsRecordedAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWorkerSubscriptionStatusChangedTests.cs:SubscriptionStatusChanged_StatusWrittenDirectly_RaisedOnlyWhenTheValueChangesAsync</tests>
public sealed class SubscriptionStatusChangedEventArgs(
  SubscriptionState state,
  SubscriptionStatus previousStatus,
  SubscriptionStatus status) : EventArgs {

  /// <summary>The state whose status changed.</summary>
  public SubscriptionState State { get; } = state;

  /// <summary>The destination whose subscription status changed.</summary>
  public TransportDestination Destination => State.Destination;

  /// <summary>The status before the change.</summary>
  public SubscriptionStatus PreviousStatus { get; } = previousStatus;

  /// <summary>The status the change set.</summary>
  public SubscriptionStatus Status { get; } = status;
}
