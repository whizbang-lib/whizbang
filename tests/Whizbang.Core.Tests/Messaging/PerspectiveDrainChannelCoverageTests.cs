using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage for <see cref="IPerspectiveDrainChannel"/>'s default interface methods and
/// <see cref="PerspectiveDrainChannel"/>'s own overrides — the same invariants
/// <see cref="DrainChannelTests"/> already locks for its structural mirrors,
/// <c>IOutboxDrainChannel</c> and <c>IInboxDrainChannel</c>, but never for the perspective drain
/// channel itself. <c>ClaimWorker</c>'s defense-in-depth against re-queuing an in-flight stream_id
/// depends on <see cref="PerspectiveDrainChannel.TryWrite"/> and
/// <see cref="PerspectiveDrainChannel.IsInFlight"/> actually working, and a custom
/// implementation that doesn't override the interface's optional in-flight tracking must still
/// fall through to a safe (always-false, no-op) default rather than throw.
/// </summary>
public class PerspectiveDrainChannelCoverageTests {

  [Test]
  public async Task PerspectiveDrainChannel_TryWrite_AcceptsValueAsync() {
    var ch = new PerspectiveDrainChannel();
    var sid = Guid.NewGuid();

    var accepted = ch.TryWrite(sid);

    await Assert.That(accepted).IsTrue();
    var read = await ch.Reader.ReadAsync();
    await Assert.That(read).IsEqualTo(sid);
  }

  [Test]
  public async Task PerspectiveDrainChannel_InFlightSet_StartsEmpty_FlipsOnMarkAsync() {
    var ch = new PerspectiveDrainChannel();
    var sid = Guid.NewGuid();

    await Assert.That(ch.IsInFlight(sid)).IsFalse()
      .Because("ClaimWorker's defense-in-depth against re-queuing an in-flight stream_id starts from a clean slate");

    ch.MarkDraining(sid);
    await Assert.That(ch.IsInFlight(sid)).IsTrue();

    ch.MarkDrained(sid);
    await Assert.That(ch.IsInFlight(sid)).IsFalse();
  }

  [Test]
  public async Task IPerspectiveDrainChannel_DefaultMethods_ReturnFalseAndNoOpAsync() {
    // A bare implementation that DOESN'T override IsInFlight / MarkDraining / MarkDrained falls
    // through to the interface defaults — false + no-op. Default interface methods can only be
    // reached via the interface reference.
    IPerspectiveDrainChannel ch = new _minimalChannel();
    var sid = Guid.NewGuid();

    await Assert.That(ch.IsInFlight(sid)).IsFalse();
    ch.MarkDraining(sid);
    // After MarkDraining, IsInFlight is STILL false because the default does nothing.
    await Assert.That(ch.IsInFlight(sid)).IsFalse();
    ch.MarkDrained(sid);
    await Assert.That(ch.IsInFlight(sid)).IsFalse();
  }

  private sealed class _minimalChannel : IPerspectiveDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default)
      => _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }
}
