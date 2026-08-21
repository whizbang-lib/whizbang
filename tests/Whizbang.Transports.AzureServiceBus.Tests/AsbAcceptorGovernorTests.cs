#pragma warning disable CA1707 // Test method names can contain underscores

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The adaptive session-acceptor policy: concurrency starts at the floor, DOUBLES (capped at
/// the ceiling) when active sessions hold at or above 80% of current concurrency for one full
/// evaluation window, and HALVES (floored) after a full window with active sessions below 25%
/// of current. All time flows through FakeTimeProvider — the governor owns no threads and every
/// test is deterministic.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AsbAcceptorGovernor.cs</code-under-test>
public class AsbAcceptorGovernorTests {
  private static readonly TimeSpan _window = TimeSpan.FromSeconds(30);

  private static AsbAcceptorGovernor _governor(FakeTimeProvider time, int floor = 4, int ceiling = 200) =>
    new(floor, ceiling, _window, time);

  private static void _openSessions(AsbAcceptorGovernor governor, int count) {
    for (var i = 0; i < count; i++) {
      governor.OnSessionInitializing();
    }
  }

  private static void _closeSessions(AsbAcceptorGovernor governor, int count) {
    for (var i = 0; i < count; i++) {
      governor.OnSessionClosing();
    }
  }

  /// <summary>Drives the governor from the floor to 8 slots via one sustained pressure window.</summary>
  private static void _growToEight(AsbAcceptorGovernor governor, FakeTimeProvider time) {
    _openSessions(governor, 4);
    _ = governor.Evaluate();
    time.Advance(_window);
    _ = governor.Evaluate();
  }

  [Test]
  public async Task CurrentConcurrency_StartsAtTheFloorAsync() {
    var governor = _governor(new FakeTimeProvider());

    await Assert.That(governor.CurrentConcurrency).IsEqualTo(4)
      .Because("the whole point of adaptive acceptors is that the idle machinery starts small — the floor, not a standing army");
  }

  [Test]
  public async Task Floor_AboveTheCeiling_ClampsToTheCeilingAsync() {
    var governor = _governor(new FakeTimeProvider(), floor: 8, ceiling: 2);

    await Assert.That(governor.CurrentConcurrency).IsEqualTo(2)
      .Because("MaxConcurrentSessions is the hard ceiling — a floor above it can never be honored");
    await Assert.That(governor.Floor).IsEqualTo(2);
  }

  [Test]
  public async Task Evaluate_PressureSustainedForOneWindow_DoublesConcurrencyAsync() {
    var time = new FakeTimeProvider();
    var governor = _governor(time);
    _openSessions(governor, 4); // 4 ≥ 80% of 4 — pressure

    var atStamp = governor.Evaluate();
    time.Advance(_window);
    var afterWindow = governor.Evaluate();

    await Assert.That(atStamp).IsFalse()
      .Because("pressure must be SUSTAINED for a full window — a momentary spike is not demand");
    await Assert.That(afterWindow).IsTrue();
    await Assert.That(governor.CurrentConcurrency).IsEqualTo(8)
      .Because("sustained pressure doubles the acceptor pool so waiting sessions get slots");
  }

  [Test]
  public async Task Evaluate_PressureBrokenMidWindow_RestartsTheWindowAsync() {
    var time = new FakeTimeProvider();
    var governor = _governor(time);
    _openSessions(governor, 4);
    _ = governor.Evaluate();

    time.Advance(TimeSpan.FromSeconds(15));
    _closeSessions(governor, 3); // 1 active < 80% of 4 — pressure broken
    _ = governor.Evaluate();
    _openSessions(governor, 3); // back to 4 active
    _ = governor.Evaluate();

    time.Advance(TimeSpan.FromSeconds(15));
    var grew = governor.Evaluate();

    await Assert.That(grew).IsFalse()
      .Because("the pressure clock restarted when occupancy dipped — only 15s of the fresh window has elapsed");
    await Assert.That(governor.CurrentConcurrency).IsEqualTo(4);
  }

  [Test]
  public async Task Evaluate_Growth_CapsAtTheCeilingAsync() {
    var time = new FakeTimeProvider();
    var governor = _governor(time, floor: 4, ceiling: 6);
    _openSessions(governor, 4);
    _ = governor.Evaluate();
    time.Advance(_window);

    _ = governor.Evaluate();

    await Assert.That(governor.CurrentConcurrency).IsEqualTo(6)
      .Because("doubling would give 8, but MaxConcurrentSessions stays the hard ceiling");
  }

  [Test]
  public async Task Evaluate_QuietForAFullWindow_HalvesConcurrencyAsync() {
    var time = new FakeTimeProvider();
    var governor = _governor(time);
    _growToEight(governor, time);

    _closeSessions(governor, 4); // 0 active < 25% of 8 — quiet
    _ = governor.Evaluate();
    time.Advance(_window);
    var decayed = governor.Evaluate();

    await Assert.That(decayed).IsTrue();
    await Assert.That(governor.CurrentConcurrency).IsEqualTo(4)
      .Because("a full quiet window means the extra acceptors are pure idle spend — halve back toward the floor");
  }

  [Test]
  public async Task Evaluate_Decay_NeverGoesBelowTheFloorAsync() {
    var time = new FakeTimeProvider();
    var governor = _governor(time); // at the floor, zero active
    _ = governor.Evaluate();
    time.Advance(_window);

    var changed = governor.Evaluate();

    await Assert.That(changed).IsFalse();
    await Assert.That(governor.CurrentConcurrency).IsEqualTo(4)
      .Because("the floor guarantees a quiet service can still accept work the instant it arrives");
  }

  [Test]
  public async Task Evaluate_MidBandOccupancy_HoldsCurrentConcurrencyAsync() {
    var time = new FakeTimeProvider();
    var governor = _governor(time);
    _growToEight(governor, time);

    // 4 active on 8 slots: 50% — neither ≥ 80% (pressure) nor < 25% (quiet).
    time.Advance(_window);
    var changed = governor.Evaluate();

    await Assert.That(changed).IsFalse()
      .Because("mid-band occupancy is a correctly-sized pool — neither growth nor decay applies");
    await Assert.That(governor.CurrentConcurrency).IsEqualTo(8);
  }

  [Test]
  public async Task ActiveSessions_TracksInitializingAndClosingAsync() {
    var governor = _governor(new FakeTimeProvider());

    _openSessions(governor, 3);
    _closeSessions(governor, 1);

    await Assert.That(governor.ActiveSessions).IsEqualTo(2);
  }

  [Test]
  public async Task Constructor_NonPositiveCeiling_ThrowsAsync() {
    await Assert.That(() => new AsbAcceptorGovernor(4, 0, _window, new FakeTimeProvider()))
      .Throws<ArgumentOutOfRangeException>()
      .Because("a zero-session processor can never accept anything — fail fast at subscribe, matching the SDK's own option validation");
  }

  [Test]
  public async Task Constructor_NonPositiveWindow_ThrowsAsync() {
    await Assert.That(() => new AsbAcceptorGovernor(4, 200, TimeSpan.Zero, new FakeTimeProvider()))
      .Throws<ArgumentOutOfRangeException>()
      .Because("a zero window would grow/decay on every evaluation — thrash, not adaptation");
  }
}
