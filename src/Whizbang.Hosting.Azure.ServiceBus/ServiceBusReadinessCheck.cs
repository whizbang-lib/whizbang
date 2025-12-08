using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Transports;

namespace Whizbang.Hosting.Azure.ServiceBus;

/// <summary>
/// Checks if Azure Service Bus is ready to accept messages.
/// Implements caching to avoid excessive health checks.
/// </summary>
public class ServiceBusReadinessCheck : ITransportReadinessCheck {
  private readonly ServiceBusClient _client;
  private readonly ILogger<ServiceBusReadinessCheck> _logger;
  private readonly TimeSpan _cacheDuration;
  private DateTimeOffset? _lastSuccessfulCheck;
  private readonly SemaphoreSlim _lock = new(1, 1);

  public ServiceBusReadinessCheck(
    ServiceBusClient client,
    ILogger<ServiceBusReadinessCheck> logger,
    TimeSpan? cacheDuration = null) {
    _client = client ?? throw new ArgumentNullException(nameof(client));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _cacheDuration = cacheDuration ?? TimeSpan.FromSeconds(30);
  }

  public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    // Check cache first (only for successful checks)
    if (_lastSuccessfulCheck.HasValue &&
        DateTimeOffset.UtcNow - _lastSuccessfulCheck.Value < _cacheDuration) {
      _logger.LogDebug("Service Bus readiness check: Using cached result (ready)");
      return true;
    }

    await _lock.WaitAsync(cancellationToken);
    try {
      // Double-check cache after acquiring lock
      if (_lastSuccessfulCheck.HasValue &&
          DateTimeOffset.UtcNow - _lastSuccessfulCheck.Value < _cacheDuration) {
        _logger.LogDebug("Service Bus readiness check: Using cached result (ready)");
        return true;
      }

      // Check if client is closed
      if (_client.IsClosed) {
        _logger.LogWarning("Service Bus readiness check: Client is closed");
        return false;
      }

      // Cache successful check
      _lastSuccessfulCheck = DateTimeOffset.UtcNow;
      _logger.LogDebug("Service Bus readiness check: Client is open and ready");
      return true;

    } finally {
      _lock.Release();
    }
  }
}
