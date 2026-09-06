using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.RabbitMQ;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Round-23 coverage additions for <see cref="RabbitMQChannelPool"/>: returning a channel to an
/// already-disposed pool, one channel's disposal failure not blocking cleanup of the others
/// during <c>Dispose()</c>/<c>Reset()</c>, and <c>Reset()</c> topping the semaphore back up to
/// full capacity when a rent was outstanding across a connection-recovery reset.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQChannelPool.cs</code-under-test>
public class RabbitMQChannelPoolCoverageTests {

  // Without the disposed-pool short-circuit, returning a channel after the pool itself was
  // already Dispose()'d would fall through to releasing the pool's OWN (already-disposed)
  // semaphore — turning an ordinary RAII "using" Dispose at request-scope-end into an
  // ObjectDisposedException instead of a silent no-op.
  [Test]
  public async Task Return_AfterPoolDisposed_DoesNotThrowOnTheAlreadyDisposedSemaphoreAsync() {
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var pooled = await pool.RentAsync(CancellationToken.None);

    pool.Dispose();

    await Assert.That(() => pooled.Dispose()).ThrowsNothing();
    await Assert.That(channel.IsDisposed).IsTrue();
  }

  // If one channel's Dispose() throwing wasn't caught inside the pool-wide Dispose() loop, that
  // one already-broken channel would abort cleanup of every OTHER pooled channel too — leaking
  // connections on every shutdown that raced a broker-side channel close.
  [Test]
  public async Task Dispose_OneChannelDisposeThrows_StillDisposesTheRemainingChannelsAsync() {
    var throwing = new ThrowingDisposeChannel();
    var healthy = new FakeChannel();
    IChannel[] channels = [throwing, healthy];
    var channelIndex = 0;
    var connection = new FakeConnection(() => Task.FromResult(channels[channelIndex++]));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var pooled1 = await pool.RentAsync(CancellationToken.None);
    var pooled2 = await pool.RentAsync(CancellationToken.None);
    pooled1.Dispose();
    pooled2.Dispose();

    await Assert.That(() => pool.Dispose()).ThrowsNothing();

    await Assert.That(healthy.IsDisposed).IsTrue()
      .Because("a throwing channel's Dispose() must not stop cleanup of the channels after it");
  }

  // Reset() runs right after connection recovery to purge stale channels; if one channel's
  // Dispose() throwing aborted the loop, every channel enumerated after it would leak instead of
  // being replaced by a fresh one on the recovered connection.
  [Test]
  public async Task Reset_OneChannelDisposeThrows_StillDisposesTheRemainingChannelsAsync() {
    var throwing = new ThrowingDisposeChannel();
    var healthy = new FakeChannel();
    IChannel[] channels = [throwing, healthy];
    var channelIndex = 0;
    var connection = new FakeConnection(() => Task.FromResult(channels[channelIndex++]));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var pooled1 = await pool.RentAsync(CancellationToken.None);
    var pooled2 = await pool.RentAsync(CancellationToken.None);
    pooled1.Dispose();
    pooled2.Dispose();

    await Assert.That(() => pool.Reset()).ThrowsNothing();

    await Assert.That(healthy.IsDisposed).IsTrue()
      .Because("a throwing channel's Dispose() must not stop Reset() from disposing the "
             + "channels after it");
  }

  // Reset() runs right after connection recovery, and a rent from BEFORE the recovery can still
  // be outstanding when it fires. If Reset() didn't top the semaphore back up to full capacity
  // for that outstanding permit, the pool would stay one permit short forever, eventually
  // starving every RentAsync caller even though the connection is healthy again.
  [Test]
  public async Task Reset_WithOutstandingRentedChannel_RestoresFullSemaphoreCapacityAsync() {
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 1);
    var outstanding = await pool.RentAsync(CancellationToken.None); // never returned before Reset()

    pool.Reset();

    // If the semaphore weren't restored to full capacity, this would block forever — bound the
    // wait so a regression fails loudly instead of hanging the suite.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var rentedAfterReset = await pool.RentAsync(cts.Token);

    await Assert.That(rentedAfterReset.Channel).IsNotNull()
      .Because("a pool that cannot hand out a channel after recovery starves every publisher "
             + "even though the connection is healthy again");

    // The rental that spanned the reset must retire quietly. Reset already restored the semaphore
    // to full capacity, so a Return that released another permit would push it past its maximum
    // and throw SemaphoreFullException -- out of Dispose, and so out of the caller's using block,
    // landing on top of whatever failure triggered the recovery in the first place.
    var outstandingDisposal = _record(outstanding.Dispose);
    await Assert.That(outstandingDisposal).IsNull()
      .Because("Reset is called on connection recovery, which is exactly when channels are in "
             + "flight, so a rental outliving a reset is the normal case and must not throw");

    // And the pool must still be usable afterwards rather than permanently over-credited.
    rentedAfterReset.Dispose();
    using var afterCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var rentedAgain = await pool.RentAsync(afterCts.Token);
    await Assert.That(rentedAgain.Channel).IsNotNull()
      .Because("the stale return must leave the permit count intact, not inflate it");
    rentedAgain.Dispose();
  }

  // A pool is held by DI and disposed at shutdown, where nothing guarantees exactly one Dispose:
  // a container disposal racing an explicit teardown calls it twice routinely. Without the
  // _disposed short-circuit the second call re-enters SemaphoreSlim.Dispose and re-walks a
  // cleared channel list, so shutdown would end in an ObjectDisposedException that arrives after
  // the application has already decided it stopped cleanly.
  [Test]
  public async Task Dispose_CalledTwice_SecondCallIsANoOpAsync() {
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 1);
    using (await pool.RentAsync(CancellationToken.None)) { }

    pool.Dispose();

    await Assert.That(_record(pool.Dispose)).IsNull()
      .Because("a second disposal must be inert, not an exception thrown after shutdown already "
             + "reported success");
  }

  // The permit is taken BEFORE the channel is created, so a broker that refuses the create must
  // give it back. If this catch stopped releasing, every failed create would shrink the pool by
  // one permanently -- and a broker that refuses maxChannels times in a row would leave RentAsync
  // blocked forever on a pool that owns no channels at all, with nothing to return one.
  [Test]
  public async Task RentAsync_ChannelCreationFails_ReleasesThePermitSoThePoolIsNotStarvedAsync() {
    var fail = true;
    var healthy = new FakeChannel();
    var connection = new FakeConnection(() => fail
      ? Task.FromException<IChannel>(new InvalidOperationException("broker refused the channel"))
      : Task.FromResult<IChannel>(healthy));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 1);

    await Assert.That(async () => await pool.RentAsync(CancellationToken.None))
      .Throws<InvalidOperationException>()
      .Because("the caller must learn the channel could not be created");

    // The pool's only permit must be back. Bound the wait so a leaked permit fails loudly here
    // rather than hanging the suite.
    fail = false;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var rented = await pool.RentAsync(cts.Token);

    await Assert.That(rented.Channel).IsSameReferenceAs(healthy)
      .Because("a permit leaked on the failure path would block this rent forever, turning one "
             + "transient broker refusal into a permanently smaller pool");
    pool.Dispose();
  }

  /// <summary>Runs an action and hands back whatever it threw, or null.</summary>
  private static Exception? _record(Action action) {
    try {
      action();
      return null;
    } catch (Exception ex) {
      return ex;
    }
  }

  /// <summary>A channel whose Dispose() always throws — exercises the disposal-error catch
  /// blocks in <see cref="RabbitMQChannelPool.Dispose"/> and <see cref="RabbitMQChannelPool.Reset"/>.</summary>
  private sealed class ThrowingDisposeChannel : FakeChannel, IChannel {
    public new void Dispose() => throw new InvalidOperationException("simulated channel dispose failure");
  }
}
