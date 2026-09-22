using System.Collections.Concurrent;
using RabbitMQ.Client;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Thread-safe channel pool for RabbitMQ channels.
/// RabbitMQ channels are NOT thread-safe, so pooling is required for concurrent operations.
/// </summary>
/// <param name="connection">The RabbitMQ connection (should be a singleton).</param>
/// <param name="maxChannels">Maximum number of channels in the pool.</param>
/// <docs>messaging/transports/rabbitmq</docs>
public sealed class RabbitMQChannelPool(IConnection connection, int maxChannels) : IDisposable {
  private readonly IConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
  private readonly ConcurrentBag<IChannel> _availableChannels = [];
  private readonly SemaphoreSlim _semaphore = new(maxChannels, maxChannels);
  private readonly List<IChannel> _allChannels = [];
  private readonly Lock _lock = new();
  private bool _disposed;

  /// <summary>
  /// Bumped by <see cref="Reset"/>. A <see cref="PooledChannel"/> carries the value it was rented
  /// under, so <see cref="Return"/> can tell a live rental from one that outlived a reset.
  /// </summary>
  private int _generation;

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
      // Read AFTER the permit is held: a Reset that lands between here and Return must be
      // observed as a generation change, and taking the reading earlier would widen that window.
      var generation = Volatile.Read(ref _generation);

      // Try to get an existing channel from the pool
      if (_availableChannels.TryTake(out var channel)) {
        return new PooledChannel(channel, this, generation);
      }

      // No available channel, create a new one
      channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
      lock (_lock) {
        _allChannels.Add(channel);
      }

      return new PooledChannel(channel, this, generation);
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
  /// <param name="generation">The generation the channel was rented under.</param>
  internal void Return(IChannel channel, int generation) {
    if (_disposed) {
      channel?.Dispose();
      return;
    }

    // A rental that outlived a Reset. Reset already restored the semaphore to full capacity and
    // discarded every channel it knew about, so this one belongs to the connection that was torn
    // down: putting it back would hand a stale channel to the next caller, and releasing a permit
    // would push the semaphore past its maximum -- which throws SemaphoreFullException out of
    // Dispose, and therefore out of the caller's using block.
    //
    // That is not a hypothetical ordering. Reset exists to be called on connection recovery, and
    // recovery happens precisely when channels are in flight, so the throw would land on top of
    // whatever failure triggered the recovery and hide it.
    if (generation != Volatile.Read(ref _generation)) {
      if (channel != null) {
        lock (_lock) {
          _allChannels.Remove(channel);
        }
        try {
          channel.Dispose();
        } catch {
          // The connection this channel belonged to is already gone; disposal failing here is
          // expected and says nothing the caller can act on.
        }
      }
      return;
    }

    if (channel is { IsOpen: true }) {
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

  /// <summary>
  /// Clears all pooled channels, disposing stale ones.
  /// Call after connection recovery to prevent stale channels from causing CHANNEL_ERROR.
  /// New channels will be created on the recovered connection by subsequent RentAsync calls.
  /// </summary>
  public void Reset() {
    // Bump first, so any rental still outstanding is already recognizable as stale by the time
    // the channels it might hold are disposed below.
    Interlocked.Increment(ref _generation);

    lock (_lock) {
      foreach (var channel in _allChannels) {
        try {
          channel.Dispose();
        } catch {
          // Ignore disposal errors on stale channels
        }
      }
      _allChannels.Clear();
    }

    // Drain available bag (channels already disposed above).
    // The side effect is TryTake itself — body intentionally empty.
    while (_availableChannels.TryTake(out _)) {
      // Intentional no-op: each iteration removes a stale channel reference.
    }

    // Reset semaphore to full capacity so new channels can be created
    // Drain any existing permits, then release maxChannels
    while (_semaphore.CurrentCount < maxChannels) {
      _semaphore.Release();
    }
  }
}

/// <summary>
/// A pooled RabbitMQ channel that automatically returns to the pool when disposed.
/// Uses RAII pattern for automatic resource management.
/// </summary>
public readonly struct PooledChannel : IDisposable {
  private readonly RabbitMQChannelPool _pool;
  private readonly int _generation;

  /// <summary>
  /// Gets the underlying RabbitMQ channel.
  /// </summary>
  public IChannel Channel { get; }

  internal PooledChannel(IChannel channel, RabbitMQChannelPool pool, int generation) {
    Channel = channel;
    _pool = pool;
    _generation = generation;
  }

  /// <summary>
  /// Returns the channel to the pool.
  /// </summary>
  public void Dispose() {
    _pool.Return(Channel, _generation);
  }
}
