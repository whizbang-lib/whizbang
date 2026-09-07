using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Covers the default arm of <see cref="SignalBus"/>'s targeting/target-kind pairing check — the
/// arm reached when a signal type declares a <see cref="SignalTargeting"/> value the running bus
/// does not recognize.
/// </summary>
/// <remarks>
/// The pairing check exists to catch programmer errors (a broadcast signal published at a specific
/// instance, or vice versa) and it throws. The default arm decides what happens for a targeting
/// value from outside the enum's current members — the shape a rolling upgrade produces when one
/// deployment's control plane publishes a signal type built against a newer enum. The bus treats
/// that as "not a mismatch" and lets the publish through; if the arm ever flipped to
/// <c>true</c>, every such publish would throw at the call site instead of reaching the transport,
/// and a mixed-version cluster would lose the whole signal type rather than one deployment.
/// </remarks>
public class SignalBusUnknownTargetingTests {

  private readonly record struct UnknownTargetingSignal(int Value) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    // Deliberately outside the enum's declared members: neither Targeted (0) nor Broadcast (1).
    public static SignalTargeting Targeting => (SignalTargeting)99;
  }

  private sealed class RecordingTransport : ISignalTransport {
    public List<SignalTargetKind> PublishedKinds { get; } = [];

    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      PublishedKinds.Add(target.Kind);
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  public async Task PublishAsync_UnrecognizedTargetingWithBroadcastTarget_ReachesTheTransportAsync() {
    var transport = new RecordingTransport();
    SignalBus bus = new([transport]);

    await bus.PublishAsync(new UnknownTargetingSignal(1), SignalTarget.Broadcast, CancellationToken.None);

    await Assert.That(transport.PublishedKinds).IsEquivalentTo([SignalTargetKind.Broadcast])
      .Because("a targeting value the bus does not recognize must not be reported as a mismatch — "
             + "the publish has to reach the transport, not throw at the call site");
  }

  [Test]
  public async Task PublishAsync_UnrecognizedTargetingWithInstanceTarget_ReachesTheTransportAsync() {
    var transport = new RecordingTransport();
    SignalBus bus = new([transport]);
    var instanceId = Guid.NewGuid();

    await bus.PublishAsync(new UnknownTargetingSignal(2), SignalTarget.Instance(instanceId), CancellationToken.None);

    await Assert.That(transport.PublishedKinds).IsEquivalentTo([SignalTargetKind.Instance])
      .Because("the unrecognized-targeting arm must be permissive for EVERY target kind — a "
             + "declared-vs-call-site check that cannot read the declaration has nothing to "
             + "compare, so it must not reject the directed target either");
  }

  [Test]
  public async Task PublishAsync_KnownTargetingMismatched_StillThrowsAsync() {
    // The permissive default arm must not have softened the check it sits beside: a signal that
    // DOES declare a targeting the bus understands still has to be validated against the target.
    var transport = new RecordingTransport();
    SignalBus bus = new([transport]);

    await Assert.That(async () =>
        await bus.PublishAsync(new BroadcastOnlySignal(3), SignalTarget.Instance(Guid.NewGuid()), CancellationToken.None))
      .Throws<ArgumentException>()
      .Because("a recognized Broadcast declaration paired with a directed target is the "
             + "programmer error the check exists for, and must still be rejected");
    await Assert.That(transport.PublishedKinds).IsEmpty()
      .Because("a rejected publish must never have reached the transport");
  }

  private readonly record struct BroadcastOnlySignal(int Value) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }
}
