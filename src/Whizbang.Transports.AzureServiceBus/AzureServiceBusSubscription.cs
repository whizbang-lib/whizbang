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
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Pause_SetsIsActiveFalseAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_PauseWhenPaused_DoesNotThrowAsync</tests>
  public Task PauseAsync() {
    if (!IsActive) {
      return Task.CompletedTask;
    }

    IsActive = false;

    // IMPORTANT: Do NOT stop the processor - just set IsActive = false
    // The transport's message handler checks IsActive and abandons messages when paused
    // Stopping and restarting the processor causes message handler re-registration issues
    _logger.LogInformation("Paused Service Bus subscription (handler will abandon messages)");
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Resume_SetsIsActiveTrueAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_ResumeWhenActive_DoesNotThrowAsync</tests>
  public Task ResumeAsync() {
    if (IsActive) {
      return Task.CompletedTask;
    }

    IsActive = true;

    // IMPORTANT: Do NOT restart the processor - just set IsActive = true
    // The transport's message handler checks IsActive and processes messages when active
    _logger.LogInformation("Resumed Service Bus subscription (handler will process messages)");
    return Task.CompletedTask;
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
