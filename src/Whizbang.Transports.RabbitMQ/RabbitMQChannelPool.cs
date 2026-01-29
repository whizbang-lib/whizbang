using System.Collections.Concurrent;
using RabbitMQ.Client;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Thread-safe channel pool for RabbitMQ channels.
/// RabbitMQ channels are NOT thread-safe, so pooling is required for concurrent operations.
/// </summary>
/// <docs>components/transports/rabbitmq</docs>
public sealed class RabbitMQChannelPool : IDisposable {
  private readonly IConnection _connection;
  private readonly int _maxChannels;
  private readonly ConcurrentBag<IChannel> _availableChannels = [];
  private readonly SemaphoreSlim _semaphore;
  private readonly List<IChannel> _allChannels = [];
  private readonly object _lock = new();
  private bool _disposed;

  /// <summary>
  /// Initializes a new instance of the <see cref="RabbitMQChannelPool"/> class.
  /// </summary>
  /// <param name="connection">The RabbitMQ connection (should be a singleton).</param>
  /// <param name="maxChannels">Maximum number of channels in the pool.</param>
  public RabbitMQChannelPool(IConnection connection, int maxChannels) {
    _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    _maxChannels = maxChannels;
    _semaphore = new SemaphoreSlim(maxChannels, maxChannels);
  }

  /// <summary>
  /// Rents a channel from the pool.
  /// If no channels are available and the pool is not exhausted, creates a new channel.
  /// If the pool is exhausted, blocks until a channel is returned.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A pooled channel that will be returned to the pool when disposed.</returns>
  public async ValueTask<PooledChannel> RentAsync(CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    // Wait for semaphore to ensure we don't exceed max channels
    await _semaphore.WaitAsync(cancellationToken);

    try {
      // Try to get an existing channel from the pool
      if (_availableChannels.TryTake(out var channel)) {
        return new PooledChannel(channel, this);
      }

      // No available channel, create a new one
      channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
      lock (_lock) {
        _allChannels.Add(channel);
      }

      return new PooledChannel(channel, this);
    } catch {
      // Release semaphore if we failed to get a channel
      _semaphore.Release();
      throw;
    }
  }

  /// <summary>
  /// Returns a channel to the pool.
  /// </summary>
  /// <param name="channel">The channel to return.</param>
  internal void Return(IChannel channel) {
    if (_disposed) {
      channel?.Dispose();
      return;
    }

    if (channel != null && channel.IsOpen) {
      _availableChannels.Add(channel);
    } else {
      // Channel is closed, don't return it to pool
      if (channel != null) {
        lock (_lock) {
          _allChannels.Remove(channel);
        }
        channel.Dispose();
      }
    }

    _semaphore.Release();
  }

  /// <summary>
  /// Disposes all channels in the pool.
  /// </summary>
  public void Dispose() {
    if (_disposed) {
      return;
    }

    _disposed = true;

    lock (_lock) {
      foreach (var channel in _allChannels) {
        try {
          channel.Dispose();
        } catch {
          // Ignore disposal errors
        }
      }
      _allChannels.Clear();
    }

    _availableChannels.Clear();
    _semaphore.Dispose();
  }
}

/// <summary>
/// A pooled RabbitMQ channel that automatically returns to the pool when disposed.
/// Uses RAII pattern for automatic resource management.
/// </summary>
public readonly struct PooledChannel : IDisposable {
  private readonly RabbitMQChannelPool _pool;

  /// <summary>
  /// Gets the underlying RabbitMQ channel.
  /// </summary>
  public IChannel Channel { get; }

  internal PooledChannel(IChannel channel, RabbitMQChannelPool pool) {
    Channel = channel;
    _pool = pool;
  }

  /// <summary>
  /// Returns the channel to the pool.
  /// </summary>
  public void Dispose() {
    _pool.Return(Channel);
  }
}

// namespace
