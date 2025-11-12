using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Azure Service Bus subscription implementation.
/// Controls a ServiceBusProcessor lifecycle and supports pause/resume operations.
/// </summary>
public class AzureServiceBusSubscription : ISubscription {
  private readonly ServiceBusProcessor _processor;
  private readonly ILogger _logger;
  private bool _isDisposed;

  public AzureServiceBusSubscription(ServiceBusProcessor processor, ILogger logger) {
    _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <inheritdoc />
  public bool IsActive { get; private set; } = true;

  /// <inheritdoc />
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
  }
}
