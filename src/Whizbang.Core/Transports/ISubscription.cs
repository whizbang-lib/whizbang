// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;

namespace Whizbang.Core.Transports;

/// <summary>
/// Event args for subscription disconnection events.
/// </summary>
public class SubscriptionDisconnectedEventArgs : EventArgs {
  /// <summary>
  /// Gets the exception that caused the disconnection, if any.
  /// </summary>
  public Exception? Exception { get; init; }

  /// <summary>
  /// Gets the reason for disconnection.
  /// </summary>
  public string Reason { get; init; } = "Unknown";

  /// <summary>
  /// Gets whether the disconnection was initiated by the application (e.g., during shutdown).
  /// </summary>
  public bool IsApplicationInitiated { get; init; }
}

/// <summary>
/// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Dispose_UnsubscribesAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Pause_SetsIsActiveFalseAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Resume_SetsIsActiveTrueAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_InitialState_IsActiveAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_DisposeMultipleTimes_DoesNotThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_PauseWhenPaused_DoesNotThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_ResumeWhenActive_DoesNotThrowAsync</tests>
/// Represents an active subscription to a transport.
/// Allows controlling the subscription (pause, resume, dispose).
/// </summary>
/// <docs>messaging/transports/transports</docs>
public interface ISubscription : IDisposable {
  /// <summary>
  /// Event raised when the subscription is disconnected unexpectedly.
  /// Subscribers can use this to trigger immediate reconnection attempts.
  /// </summary>
  /// <remarks>
  /// A transport raises it for a loss it will not recover from by itself and records it in <see cref="Disconnection"/>
  /// first. A subscription that reported a disconnect is replaced, never reused: the subscription retry helper acts on
  /// the first report only, and detaches its handler and disposes the subscription, exactly once, before it subscribes
  /// again.
  /// </remarks>
  /// <docs>messaging/transports/transports#subscription-disconnects</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Resilience/SubscriptionRetryHelperDisconnectWindowTests.cs:SubscribeWithRetryAsync_ReconnectAfterADisconnect_DisposesTheReplacedSubscriptionOnceAndDetachesItsHandlerAsync</tests>
  event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

  /// <summary>
  /// Gets the unexpected disconnect this subscription has reported, or <see langword="null"/> while it has reported
  /// none. Once set it stays set: a disconnected subscription is replaced, never reused.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A handler attached to <see cref="OnDisconnected"/> hears only the disconnects raised after it is attached, and a
  /// transport can lose its connection between creating the subscription and the caller attaching its handler. A
  /// caller that attaches a handler and then reads this property misses no disconnect: either the handler hears it or
  /// this property already holds it, and the caller must treat the two as one disconnect.
  /// </para>
  /// <para>
  /// An implementation that raises <see cref="OnDisconnected"/> records the event arguments here before it raises the
  /// event, and only for a disconnect that is not <see cref="SubscriptionDisconnectedEventArgs.IsApplicationInitiated"/>.
  /// The default returns <see langword="null"/>, which is right for a subscription that never disconnects.
  /// </para>
  /// </remarks>
  /// <docs>messaging/transports/transports#subscription-disconnects</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Resilience/SubscriptionRetryHelperDisconnectWindowTests.cs:SubscribeWithRetryAsync_DisconnectBeforeTheHandlerIsAttached_EndsRecoveringAndReconnectsAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Component.Tests/RabbitMQSubscriptionTests.cs:Disconnection_AfterAnUnexpectedChannelShutdown_HoldsTheRaisedDisconnectAsync</tests>
  SubscriptionDisconnectedEventArgs? Disconnection => null;

  /// <summary>
  /// Gets whether the subscription is currently active.
  /// When paused, the subscription will not receive new messages.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_InitialState_IsActiveAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Pause_SetsIsActiveFalseAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Resume_SetsIsActiveTrueAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Dispose_UnsubscribesAsync</tests>
  bool IsActive { get; }

  /// <summary>
  /// Pauses the subscription.
  /// No new messages will be delivered until Resume is called.
  /// Messages may be buffered by the transport depending on implementation.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Pause_SetsIsActiveFalseAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_PauseWhenPaused_DoesNotThrowAsync</tests>
  Task PauseAsync();

  /// <summary>
  /// Resumes a paused subscription.
  /// Messages will begin being delivered again.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Resume_SetsIsActiveTrueAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_ResumeWhenActive_DoesNotThrowAsync</tests>
  Task ResumeAsync();
}
