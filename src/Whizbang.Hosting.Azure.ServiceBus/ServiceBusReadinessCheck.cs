using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Transports;

namespace Whizbang.Hosting.Azure.ServiceBus;

/// <summary>
/// <tests>tests/Whizbang.Hosting.Azure.ServiceBus.Tests/ServiceBusReadinessCheckTests.cs:IsReadyAsync_WithValidClient_ReturnsTrueAsync</tests>
/// <tests>tests/Whizbang.Hosting.Azure.ServiceBus.Tests/ServiceBusReadinessCheckTests.cs:IsReadyAsync_WithClosedClient_ReturnsFalseAsync</tests>
/// <tests>tests/Whizbang.Hosting.Azure.ServiceBus.Tests/ServiceBusReadinessCheckTests.cs:IsReadyAsync_RespectsCancellationTokenAsync</tests>
/// <tests>tests/Whizbang.Hosting.Azure.ServiceBus.Tests/ServiceBusReadinessCheckTests.cs:IsReadyAsync_CachesResult_ForSuccessfulChecksAsync</tests>
/// <tests>tests/Whizbang.Hosting.Azure.ServiceBus.Tests/ServiceBusReadinessCheckTests.cs:IsReadyAsync_CacheExpires_AfterDurationAsync</tests>
/// Checks if Azure Service Bus is ready to accept messages.
/// Leverages transport initialization state for accurate readiness tracking.
/// Implements caching to avoid excessive health checks.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Simple health check logging - LoggerMessage delegates would be overkill for infrequent health checks")]
public class ServiceBusReadinessCheck : ITransportReadinessCheck, IDisposable {
  private readonly ITransport _transport;
  private readonly ServiceBusClient _client;
  private readonly ILogger<ServiceBusReadinessCheck> _logger;
  private readonly TimeSpan _cacheDuration;
  private DateTimeOffset? _lastSuccessfulCheck;
  private readonly SemaphoreSlim _lock = new(1, 1);
  private bool _disposed;

  public ServiceBusReadinessCheck(
    ITransport transport,
    ServiceBusClient client,
    ILogger<ServiceBusReadinessCheck> logger,
    TimeSpan? cacheDuration = null) {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _client = client ?? throw new ArgumentNullException(nameof(client));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _cacheDuration = cacheDuration ?? TimeSpan.FromSeconds(30);
  }

  public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    // CRITICAL: Check if transport is initialized first
    // Transport.InitializeAsync() verifies actual connectivity to Service Bus
    if (!_transport.IsInitialized) {
      _logger.LogDebug("Service Bus readiness check: Transport not initialized");
      return false;
    }

    // Check cache first (only for successful checks)
    if (_lastSuccessfulCheck.HasValue &&
        DateTimeOffset.UtcNow - _lastSuccessfulCheck.Value < _cacheDuration) {
      _logger.LogDebug("Service Bus readiness check: Using cached result (ready)");
      return true;
    }

    await _lock.WaitAsync(cancellationToken);
    try {
      // Double-check transport initialization after acquiring lock
      if (!_transport.IsInitialized) {
        _logger.LogDebug("Service Bus readiness check: Transport not initialized");
        return false;
      }

      // Double-check cache after acquiring lock
      if (_lastSuccessfulCheck.HasValue &&
          DateTimeOffset.UtcNow - _lastSuccessfulCheck.Value < _cacheDuration) {
        _logger.LogDebug("Service Bus readiness check: Using cached result (ready)");
        return true;
      }

      // Check if client is closed (transport could become disconnected after initialization)
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

  public void Dispose() {
    if (_disposed) {
      return;
    }

    _lock.Dispose();
    _disposed = true;
    GC.SuppressFinalize(this);
  }
}
