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
  public async Task PauseAsync() {
    if (!IsActive) {
      return;
    }

    IsActive = false;

    // Stop the processor (messages will remain in queue)
    if (_processor.IsProcessing) {
      await _processor.StopProcessingAsync();
      _logger.LogInformation("Paused Service Bus subscription");
    }
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_Resume_SetsIsActiveTrueAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ISubscriptionTests.cs:ISubscription_ResumeWhenActive_DoesNotThrowAsync</tests>
  public async Task ResumeAsync() {
    if (IsActive) {
      return;
    }

    IsActive = true;

    // Restart the processor
    if (!_processor.IsProcessing) {
      await _processor.StartProcessingAsync();
      _logger.LogInformation("Resumed Service Bus subscription");
    }
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
