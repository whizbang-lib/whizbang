// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Azure Service Bus subscription implementation.
/// Controls a ServiceBusProcessor lifecycle and supports pause/resume operations.
/// </summary>
/// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_InitialState_IsActiveAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Pause_SetsIsActiveFalseAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Resume_SetsIsActiveTrueAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Dispose_UnsubscribesAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_DisposeMultipleTimes_DoesNotThrowAsync</tests>
/// <remarks>
/// Initializes a new instance of AzureServiceBusSubscription.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Subscription lifecycle logging - infrequent pause/resume/dispose operations")]
public sealed class AzureServiceBusSubscription : ISubscription {
  private readonly ServiceBusProcessor? _processor;
  private readonly ServiceBusSessionProcessor? _sessionProcessor;
  private readonly ILogger _logger;
  private bool _isDisposed;
  private SubscriptionDisconnectedEventArgs? _disconnection;

  /// <summary>
  /// Creates a subscription wrapping a standard (non-session) processor.
  /// </summary>
  public AzureServiceBusSubscription(ServiceBusProcessor processor, ILogger logger) {
    ArgumentNullException.ThrowIfNull(processor);
    _processor = processor;
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <summary>
  /// Creates a subscription wrapping a session processor for FIFO ordering.
  /// </summary>
  public AzureServiceBusSubscription(ServiceBusSessionProcessor sessionProcessor, ILogger logger) {
    ArgumentNullException.ThrowIfNull(sessionProcessor);
    _sessionProcessor = sessionProcessor;
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  // Each constructor sets exactly one of the two processors, so when there is no standard processor
  // there is a session processor.
  private bool _isProcessing => _processor?.IsProcessing ?? _sessionProcessor!.IsProcessing;

  /// <inheritdoc />
  /// <remarks>
  /// <para>
  /// The SDK's processor heals transient failures itself (lost connections, a busy or timed-out namespace), so those
  /// are not disconnects. This event is raised, at most once, when the processor hits a failure it will not get past
  /// by retrying: the topic or subscription is not found or is disabled, the identity is not authorized, or the
  /// processor was disposed underneath this subscription. The caller replaces the subscription, which provisions a
  /// deleted entity again when provisioning is on.
  /// </para>
  /// <para>
  /// It is never raised while the subscription is paused, after it is disposed or after its transport is disposed:
  /// those stops are the application's own. A failure in the message handler, a settlement or a lock renewal is not a
  /// disconnect either; the processor goes on receiving.
  /// </para>
  /// </remarks>
  /// <docs>messaging/transports/transports#subscription-disconnects</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusProcessorStopsReceivingTests.cs:ProcessorError_EntityGoneOrDisabled_ReportsAnUnexpectedDisconnectAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusProcessorStopsReceivingTests.cs:ProcessorError_AFailureTheProcessorRecoversFrom_IsNotADisconnectAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusProcessorStopsReceivingTests.cs:ProcessorError_AfterTheApplicationPausedTheSubscription_IsNotADisconnectAsync</tests>
  public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

  /// <summary>
  /// Records and raises the subscription's unexpected disconnect. Called by the transport's processor error handler
  /// for a failure the processor will not recover from on its own.
  /// </summary>
  /// <remarks>
  /// Only the first call records and raises: the processor reports every failed receive, but one lost subscription is
  /// replaced once. A call after the subscription is disposed does nothing.
  /// </remarks>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusSubscriptionTests.cs:RaiseDisconnected_Twice_NotifiesTheSubscriberOnceAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusProcessorStopsReceivingTests.cs:ProcessorError_RepeatedTerminalErrors_ReportOneDisconnectAsync</tests>
  internal void RaiseDisconnected(string reason, Exception? exception) {
    if (_isDisposed) {
      return;
    }

    // Recorded before it is raised, so a handler attached after the disconnect still finds it (#1831).
    var disconnection = new SubscriptionDisconnectedEventArgs {
      Reason = reason,
      Exception = exception,
      IsApplicationInitiated = false
    };
    if (Interlocked.CompareExchange(ref _disconnection, disconnection, null) is not null) {
      return;
    }

    _logger.LogWarning(
      "Azure Service Bus subscription disconnected: {Reason}",
      reason
    );
    OnDisconnected?.Invoke(this, disconnection);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusSubscriptionTests.cs:Disconnection_AfterRaiseDisconnected_HoldsTheFirstRaisedDisconnectAsync</tests>
  public SubscriptionDisconnectedEventArgs? Disconnection => Volatile.Read(ref _disconnection);

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_InitialState_IsActiveAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Pause_SetsIsActiveFalseAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Resume_SetsIsActiveTrueAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Dispose_UnsubscribesAsync</tests>
  public bool IsActive { get; private set; } = true;

  /// <inheritdoc />
  /// <remarks>
  /// Pausing stops the processor (#2406): a paused subscription takes no session and no message, so nothing is counted
  /// as a delivery and another instance takes the session at once. It used to keep the processor running and abandon
  /// each delivery it took, so a standing-down instance burned every message in a session it held toward the
  /// dead-letter limit. The stop is started, not awaited: the processor's stop waits for the handlers in flight, and a
  /// pause can be reached from one of them. A delivery that reaches the handler before the stop lands is handed back
  /// with the unsettled Warning.
  /// </remarks>
  /// <docs>messaging/transports/azure-service-bus#paused-subscriptions</docs>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Pause_SetsIsActiveFalseAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_PauseWhenPaused_DoesNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Component.Tests/AzureServiceBusPausedSubscriptionTests.cs:SessionBatch_PausedForAStandDown_StopsTakingCheckpointDeliveriesAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Component.Tests/AzureServiceBusPausedSubscriptionTests.cs:NonSessionBatch_Paused_StopsItsProcessorAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Component.Tests/AzureServiceBusPausedSubscriptionTests.cs:SessionBatch_PauseCannotStopTheProcessor_LogsAWarningAndStaysPausedAsync</tests>
  public Task PauseAsync() {
    if (!IsActive) {
      return Task.CompletedTask;
    }

    IsActive = false;
    _stopping = _stopReceivingAsync();
    _logger.LogInformation("Paused Service Bus subscription: its processor stops taking sessions and messages until it is resumed");
    return Task.CompletedTask;
  }

  // The processor's stop in progress, started by the last pause; a resume waits for it before starting the processor.
  private Task _stopping = Task.CompletedTask;

  private async Task _stopReceivingAsync() {
    try {
      await (_processor?.StopProcessingAsync() ?? _sessionProcessor!.StopProcessingAsync()).ConfigureAwait(false);
    } catch (Exception ex) {
      _logger.LogWarning(ex,
        "Paused Service Bus subscription could not stop its processor; it keeps receiving while paused, and each delivery "
        + "it takes is handed back to the broker unsettled");
    }
  }

  /// <inheritdoc />
  /// <remarks>Resuming starts the processor the pause stopped, once that stop has finished.</remarks>
  /// <docs>messaging/transports/azure-service-bus#paused-subscriptions</docs>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Resume_SetsIsActiveTrueAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_ResumeWhenActive_DoesNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Component.Tests/AzureServiceBusPausedSubscriptionTests.cs:SessionBatch_ResumedAfterAPause_StartsItsProcessorAndHandlesDeliveriesAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Component.Tests/AzureServiceBusPausedSubscriptionTests.cs:NonSessionBatch_ResumedAfterAPause_StartsItsProcessorAsync</tests>
  public async Task ResumeAsync() {
    if (IsActive) {
      return;
    }

    await _stopping.ConfigureAwait(false);
    IsActive = true;
    await (_processor?.StartProcessingAsync() ?? _sessionProcessor!.StartProcessingAsync()).ConfigureAwait(false);
    _logger.LogInformation("Resumed Service Bus subscription: its processor takes sessions and messages again");
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Dispose_UnsubscribesAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_DisposeMultipleTimes_DoesNotThrowAsync</tests>
  public void Dispose() {
    if (_isDisposed) {
      return;
    }

    _isDisposed = true;
    IsActive = false;

    // Stop processing and dispose
    if (_isProcessing) {
      if (_processor is not null) {
        _processor.StopProcessingAsync().GetAwaiter().GetResult();
      } else {
        _sessionProcessor!.StopProcessingAsync().GetAwaiter().GetResult();
      }
    }

    if (_processor is not null) {
      _processor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    } else {
      _sessionProcessor!.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    _logger.LogInformation("Disposed Service Bus subscription");

    GC.SuppressFinalize(this);
  }
}
