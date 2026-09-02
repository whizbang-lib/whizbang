using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Pins the recount governor: detection cost on an unhealable gap must be bounded the same way
/// repair already is, without detection ever being switched off.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityRepairPolicy.cs</code-under-test>
public class IntegrityRecountGovernorTests {

  private static readonly Guid _origin = new("00000000-0000-0000-0000-000000000001");
  private static readonly DateTimeOffset _t0 = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

  private static IntegrityRepairPolicy _policy(int unchanged = 3, int cooldownMinutes = 10) =>
    new(new IntegrityRepairPolicy.Settings {
      RecountBackoffAfterUnchanged = unchanged,
      UnchangedRecountCooldown = TimeSpan.FromMinutes(cooldownMinutes),
    });

  private static IntegrityRepairPolicy.GapObservation _obs(int actual) => new(
    _origin, "Test.Event", "tenant-a",
    FromCommitSequence: 10, ToCommitSequence: 20,
    ExpectedCount: 5, ActualCount: actual,
    ServiceBacklogDepth: 0, ConsumerLag: TimeSpan.Zero, ActiveLeaseCount: 0);

  private static bool _shouldRecount(IntegrityRepairPolicy p, DateTimeOffset now) =>
    p.ShouldRecount(_origin, "Test.Event", "tenant-a", 10, 20, now);

  [Test]
  public async Task UnknownWindow_IsAlwaysRecountedAsync() {
    var p = _policy();

    await Assert.That(_shouldRecount(p, _t0)).IsTrue()
      .Because("a window with no history yet must be counted, or a fresh gap is never confirmed");
  }

  [Test]
  public async Task UnchangedAnswers_EnterCooldownAtTheThresholdAsync() {
    var p = _policy(unchanged: 3);

    var armed1 = p.RecordRecount(_obs(2), _t0);
    var armed2 = p.RecordRecount(_obs(2), _t0.AddMinutes(1));
    var armed3 = p.RecordRecount(_obs(2), _t0.AddMinutes(2));

    await Assert.That(armed1).IsFalse();
    await Assert.That(armed2).IsFalse();
    await Assert.That(armed3).IsTrue()
      .Because("the third identical answer is the configured threshold, and arming must be visible "
             + "exactly once so the caller can log it without spamming");
    await Assert.That(_shouldRecount(p, _t0.AddMinutes(3))).IsFalse()
      .Because("inside the cooldown the answer is already known; rescanning burns the event store "
             + "for a value that has not moved");
  }

  [Test]
  public async Task CooldownExpires_OneRecountRuns_AndRearmsIfStillUnchangedAsync() {
    var p = _policy(unchanged: 2, cooldownMinutes: 10);
    p.RecordRecount(_obs(2), _t0);
    p.RecordRecount(_obs(2), _t0.AddMinutes(1));   // arms

    await Assert.That(_shouldRecount(p, _t0.AddMinutes(12))).IsTrue()
      .Because("after the cooldown the governor allows one fresh look; detection never stops");

    var rearmed = p.RecordRecount(_obs(2), _t0.AddMinutes(12));
    await Assert.That(rearmed).IsTrue()
      .Because("still unchanged after the fresh look: re-arm immediately rather than paying the "
             + "full threshold again for an answer that has now been stable for four counts");
    await Assert.That(_shouldRecount(p, _t0.AddMinutes(13))).IsFalse();
  }

  [Test]
  public async Task Improvement_LiftsTheCooldownImmediatelyAsync() {
    var p = _policy(unchanged: 2, cooldownMinutes: 60);
    p.RecordRecount(_obs(2), _t0);
    p.RecordRecount(_obs(2), _t0.AddMinutes(1));   // arms for an hour

    p.RecordRecount(_obs(4), _t0.AddMinutes(2));   // events landed

    await Assert.That(_shouldRecount(p, _t0.AddMinutes(3))).IsTrue()
      .Because("a healing window is the one thing that must be watched CLOSELY; any improvement "
             + "clears the streak and the cooldown");
  }

  [Test]
  public async Task RecordHealed_ClearsTheGovernorStateAsync() {
    var p = _policy(unchanged: 2);
    p.RecordRecount(_obs(2), _t0);
    p.RecordRecount(_obs(2), _t0.AddMinutes(1));   // arms

    p.RecordHealed(_obs(5));

    await Assert.That(_shouldRecount(p, _t0.AddMinutes(2))).IsTrue()
      .Because("a healed window's history is finished; a later divergence is a brand-new incident");
  }

  [Test]
  public async Task DifferentWindows_AreGovernedIndependentlyAsync() {
    var p = _policy(unchanged: 1, cooldownMinutes: 60);
    p.RecordRecount(_obs(2), _t0);   // arms window 10..20

    await Assert.That(p.ShouldRecount(_origin, "Test.Event", "tenant-a", 20, 30, _t0.AddMinutes(1))).IsTrue()
      .Because("the cooldown is per window; a new window is a new question");
  }
}
