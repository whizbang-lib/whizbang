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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Subscription lifecycle logging - infrequent pause/resume/dispose operations")]
public class AzureServiceBusSubscription(ServiceBusProcessor processor, ILogger logger) : ISubscription {
  private readonly ServiceBusProcessor _processor = processor ?? throw new ArgumentNullException(nameof(processor));
  private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private bool _isDisposed;

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
    if (_processor.IsProcessing) {
      _processor.StopProcessingAsync().GetAwaiter().GetResult();
    }

    _processor.DisposeAsync().AsTask().GetAwaiter().GetResult();

    _logger.LogInformation("Disposed Service Bus subscription");

    GC.SuppressFinalize(this);
  }
}
