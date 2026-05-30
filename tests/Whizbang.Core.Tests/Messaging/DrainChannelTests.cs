using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Direct tests for <see cref="OutboxDrainChannel"/> and
/// <see cref="InboxDrainChannel"/> — the per-stream drain channels that carry
/// stream_ids from <c>ClaimWorker</c> to the drain workers. The two classes
/// are structural mirrors (same Channel{Guid}, same in-flight set, same five
/// methods) so the test bodies are shared via a typed harness.
///
/// Locked invariants:
///   1. Write paths (async + sync) round-trip the stream_id through the reader.
///   2. The in-flight set is empty by default.
///   3. MarkDraining flips IsInFlight to true; MarkDrained clears it.
///   4. MarkDrained on an unknown stream_id is a safe no-op (TryRemove pattern).
///   5. The default interface methods on the bare interface return false / no-op,
///      so custom IOutboxDrainChannel implementations don't have to override them.
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public class DrainChannelTests {

  [Test]
  public async Task OutboxDrainChannel_WriteAsync_RoundTripsThroughReaderAsync() {
    var ch = new OutboxDrainChannel();
    var sid = Guid.NewGuid();

    await ch.WriteAsync(sid);
    var read = await ch.Reader.ReadAsync();

    await Assert.That(read).IsEqualTo(sid);
  }

  [Test]
  public async Task OutboxDrainChannel_TryWrite_AcceptsValueAsync() {
    var ch = new OutboxDrainChannel();
    var sid = Guid.NewGuid();

    var accepted = ch.TryWrite(sid);

    await Assert.That(accepted).IsTrue();
    var read = await ch.Reader.ReadAsync();
    await Assert.That(read).IsEqualTo(sid);
  }

  [Test]
  public async Task OutboxDrainChannel_InFlightSet_StartsEmpty_FlipsOnMarkAsync() {
    var ch = new OutboxDrainChannel();
    var sid = Guid.NewGuid();

    await Assert.That(ch.IsInFlight(sid)).IsFalse();

    ch.MarkDraining(sid);
    await Assert.That(ch.IsInFlight(sid)).IsTrue();

    ch.MarkDrained(sid);
    await Assert.That(ch.IsInFlight(sid)).IsFalse();
  }

  [Test]
  public async Task OutboxDrainChannel_MarkDrained_OnUnknownId_IsSafeAsync() {
    var ch = new OutboxDrainChannel();

    // TryRemove pattern — no exception, no change to state.
    ch.MarkDrained(Guid.NewGuid());

    await Assert.That(ch.IsInFlight(Guid.NewGuid())).IsFalse();
  }

  [Test]
  public async Task InboxDrainChannel_WriteAsync_RoundTripsThroughReaderAsync() {
    var ch = new InboxDrainChannel();
    var sid = Guid.NewGuid();

    await ch.WriteAsync(sid);
    var read = await ch.Reader.ReadAsync();

    await Assert.That(read).IsEqualTo(sid);
  }

  [Test]
  public async Task InboxDrainChannel_TryWrite_AcceptsValueAsync() {
    var ch = new InboxDrainChannel();
    var sid = Guid.NewGuid();

    var accepted = ch.TryWrite(sid);

    await Assert.That(accepted).IsTrue();
    var read = await ch.Reader.ReadAsync();
    await Assert.That(read).IsEqualTo(sid);
  }

  [Test]
  public async Task InboxDrainChannel_InFlightSet_StartsEmpty_FlipsOnMarkAsync() {
    var ch = new InboxDrainChannel();
    var sid = Guid.NewGuid();

    await Assert.That(ch.IsInFlight(sid)).IsFalse();

    ch.MarkDraining(sid);
    await Assert.That(ch.IsInFlight(sid)).IsTrue();

    ch.MarkDrained(sid);
    await Assert.That(ch.IsInFlight(sid)).IsFalse();
  }

  [Test]
  public async Task IOutboxDrainChannel_DefaultMethods_ReturnFalseAndNoOpAsync() {
    // A bare implementation that DOESN'T override IsInFlight / MarkDraining /
    // MarkDrained falls through to the interface defaults — false + no-op.
    // Default interface methods can only be reached via the interface reference.
    IOutboxDrainChannel ch = new _MinimalOutboxChannel();
    var sid = Guid.NewGuid();

    await Assert.That(ch.IsInFlight(sid)).IsFalse();
    ch.MarkDraining(sid);
    // After MarkDraining, IsInFlight is STILL false because the default does nothing.
    await Assert.That(ch.IsInFlight(sid)).IsFalse();
    ch.MarkDrained(sid);
    await Assert.That(ch.IsInFlight(sid)).IsFalse();
  }

  [Test]
  public async Task IInboxDrainChannel_DefaultMethods_ReturnFalseAndNoOpAsync() {
    IInboxDrainChannel ch = new _MinimalInboxChannel();
    var sid = Guid.NewGuid();

    await Assert.That(ch.IsInFlight(sid)).IsFalse();
    ch.MarkDraining(sid);
    await Assert.That(ch.IsInFlight(sid)).IsFalse();
    ch.MarkDrained(sid);
    await Assert.That(ch.IsInFlight(sid)).IsFalse();
  }

  private sealed class _MinimalOutboxChannel : IOutboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default)
      => _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }

  private sealed class _MinimalInboxChannel : IInboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default)
      => _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }
}
