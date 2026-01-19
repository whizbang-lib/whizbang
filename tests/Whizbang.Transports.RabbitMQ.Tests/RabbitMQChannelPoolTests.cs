using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.RabbitMQ;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Tests for RabbitMQChannelPool thread-safe channel pooling.
/// RabbitMQ channels are NOT thread-safe, so pooling is required.
/// </summary>
public class RabbitMQChannelPoolTests {
  [Test]
  public async Task RentAsync_ReturnsChannel_FromPoolAsync() {
    // Arrange - Create test doubles
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));

    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);

    // Act
    using var pooledChannel = await pool.RentAsync(CancellationToken.None);

    // Assert
    await Assert.That(pooledChannel.Channel).IsNotNull();
  }

  [Test]
  public async Task Return_AddsChannelBackToPool_ForReuseAsync() {
    // Arrange - Create test doubles
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));

    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);

    // Act - Rent and return a channel
    IChannel? firstChannel;
    using (var pooledChannel = await pool.RentAsync(CancellationToken.None)) {
      firstChannel = pooledChannel.Channel;
    } // Dispose returns to pool

    // Rent again
    using var pooledChannel2 = await pool.RentAsync(CancellationToken.None);

    // Assert - Should get the same channel back
    await Assert.That(pooledChannel2.Channel).IsEqualTo(firstChannel);
  }

  [Test]
  public async Task RentAsync_BlocksWhenPoolExhausted_UntilReturnAsync() {
    // Arrange - Create test doubles (connection will create new channels on demand)
    var channelCount = 0;
    var fakeConnection = new FakeConnection(() => {
      channelCount++;
      return Task.FromResult<IChannel>(new FakeChannel());
    });

    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 2);

    // Act - Rent all channels (no using - we need manual disposal control)
    var pooled1 = await pool.RentAsync(CancellationToken.None);
    var pooled2 = await pool.RentAsync(CancellationToken.None);

    // Try to rent a third (should block)
    var rentTask = pool.RentAsync(CancellationToken.None).AsTask();

    // Wait a bit - should NOT complete
    await Task.Delay(100);
    await Assert.That(rentTask.IsCompleted).IsFalse();

    // Return one channel
    pooled1.Dispose();

    // Now the rent should complete
    var pooled3 = await rentTask;
    await Assert.That(pooled3.Channel).IsNotNull();

    // Cleanup
    pooled2.Dispose();
    pooled3.Dispose();
    pool.Dispose();
  }

  [Test]
  public async Task Dispose_DisposesAllChannels_InPoolAsync() {
    // Arrange - Create test doubles that track disposal
    var channel1 = new FakeChannel();
    var channel2 = new FakeChannel();
    var channels = new[] { channel1, channel2 };
    var channelIndex = 0;

    var fakeConnection = new FakeConnection(() => {
      var channel = channels[channelIndex];
      channelIndex++;
      return Task.FromResult<IChannel>(channel);
    });

    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);

    // Rent both channels at once (so both are created), then return them
    var pooled1 = await pool.RentAsync(CancellationToken.None);
    var pooled2 = await pool.RentAsync(CancellationToken.None);
    pooled1.Dispose(); // Return to pool
    pooled2.Dispose(); // Return to pool

    // Act - Dispose pool
    pool.Dispose();

    // Assert - All channels should be disposed
    await Assert.That(channel1.IsDisposed).IsTrue();
    await Assert.That(channel2.IsDisposed).IsTrue();
  }
}
