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

  /// <summary>
  /// Creates a subscription wrapping a standard (non-session) processor.
  /// </summary>
  public AzureServiceBusSubscription(ServiceBusProcessor processor, ILogger logger) {
    _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <summary>
  /// Creates a subscription wrapping a session processor for FIFO ordering.
  /// </summary>
  public AzureServiceBusSubscription(ServiceBusSessionProcessor sessionProcessor, ILogger logger) {
    _sessionProcessor = sessionProcessor ?? throw new ArgumentNullException(nameof(sessionProcessor));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  private bool _isProcessing => _processor?.IsProcessing ?? _sessionProcessor?.IsProcessing ?? false;

  /// <inheritdoc />
  /// <remarks>
  /// Azure Service Bus SDK handles reconnection internally. The OnDisconnected event
  /// is raised when the transport detects a connection-level error that requires
  /// subscription re-establishment.
  /// </remarks>
  public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

  /// <summary>
  /// Raises the OnDisconnected event. Called by the transport when connection errors are detected.
  /// </summary>
  internal void RaiseDisconnected(string reason, Exception? exception) {
    if (_isDisposed) {
      return;
    }

    _logger.LogWarning(
      "Azure Service Bus subscription disconnected: {Reason}",
      reason
    );

    OnDisconnected?.Invoke(this, new SubscriptionDisconnectedEventArgs {
      Reason = reason,
      Exception = exception,
      IsApplicationInitiated = false
    });
  }

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
      } else if (_sessionProcessor is not null) {
        _sessionProcessor.StopProcessingAsync().GetAwaiter().GetResult();
      }
    }

    if (_processor is not null) {
      _processor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    } else if (_sessionProcessor is not null) {
      _sessionProcessor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    _logger.LogInformation("Disposed Service Bus subscription");

    GC.SuppressFinalize(this);
  }
}
