using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

public class SignalTargetTests {
  [Test]
  public async Task Default_IsBroadcastAsync() {
    var target = default(SignalTarget);

    await Assert.That(target.Kind).IsEqualTo(SignalTargetKind.Broadcast);
    await Assert.That(target.StreamIds.Count).IsEqualTo(0);
    await Assert.That(target.InstanceId).IsEqualTo(Guid.Empty);
  }

  [Test]
  public async Task Broadcast_HasBroadcastKindAsync() {
    var target = SignalTarget.Broadcast;

    await Assert.That(target.Kind).IsEqualTo(SignalTargetKind.Broadcast);
  }

  [Test]
  public async Task Streams_CarriesStreamIdsAsync() {
    var s1 = Guid.NewGuid();
    var s2 = Guid.NewGuid();

    var target = SignalTarget.Streams([s1, s2]);

    await Assert.That(target.Kind).IsEqualTo(SignalTargetKind.Streams);
    await Assert.That(target.StreamIds.Count).IsEqualTo(2);
    await Assert.That(target.StreamIds[0]).IsEqualTo(s1);
    await Assert.That(target.StreamIds[1]).IsEqualTo(s2);
  }

  [Test]
  public async Task Streams_NullList_ThrowsAsync() {
    await Assert.That(() => SignalTarget.Streams(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Streams_EmptyList_ThrowsAsync() {
    // An empty targeted publish would go nowhere — that's a caller bug, surface it loudly.
    await Assert.That(() => SignalTarget.Streams([])).Throws<ArgumentException>();
  }

  [Test]
  public async Task Instance_CarriesInstanceIdAsync() {
    var id = Guid.NewGuid();

    var target = SignalTarget.Instance(id);

    await Assert.That(target.Kind).IsEqualTo(SignalTargetKind.Instance);
    await Assert.That(target.InstanceId).IsEqualTo(id);
  }

  [Test]
  public async Task Instance_EmptyGuid_ThrowsAsync() {
    // Guid.Empty is never a live instance id — surface the mistake at the call site.
    await Assert.That(() => SignalTarget.Instance(Guid.Empty)).Throws<ArgumentException>();
  }
}
