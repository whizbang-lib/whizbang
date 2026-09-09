using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Priority;

namespace Whizbang.Core.Tests.Priority;

/// <summary>
/// Priority is one integer, lower is more urgent, with named constants mid-band so a declaration can move
/// in either direction without changing bucket; everything that schedules works on the bucket the number
/// falls in. Zero is "not declared": the wire omits it and the receive side reads it as Standard, so
/// nothing a producer leaves unset can land in the urgent bucket by accident.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#the-number-and-the-bucket</docs>
[Category("Unit")]
public class WorkPriorityTests {

  [Test]
  public async Task Constants_SitMidBandAsync() {
    // The relationship that matters: each constant is inside its band with room on both sides, so a
    // declaration can move in either direction without changing bucket.
    var constants = new Dictionary<int, WorkBucket> {
      [WorkPriority.INTERACTIVE] = WorkBucket.Interactive,
      [WorkPriority.STANDARD] = WorkBucket.Standard,
      [WorkPriority.BACKGROUND] = WorkBucket.Background,
    };
    foreach (var (value, bucket) in constants) {
      await Assert.That(WorkPriority.Bucket(value)).IsEqualTo(bucket);
      await Assert.That(WorkPriority.Bucket(value - 40)).IsEqualTo(bucket)
        .Because("a declaration forty more urgent than the constant stays in the same bucket");
      await Assert.That(WorkPriority.Bucket(value + 40)).IsEqualTo(bucket)
        .Because("a declaration forty less urgent than the constant stays in the same bucket");
    }
    var undeclared = WorkPriority.UNDECLARED;
    await Assert.That(undeclared).IsEqualTo(0).Because("zero is omitted on the wire, so an undeclared priority costs nothing");
  }

  [Test]
  [Arguments(1, WorkBucket.Interactive)]
  [Arguments(50, WorkBucket.Interactive)]
  [Arguments(99, WorkBucket.Interactive)]
  [Arguments(100, WorkBucket.Standard)]
  [Arguments(150, WorkBucket.Standard)]
  [Arguments(199, WorkBucket.Standard)]
  [Arguments(200, WorkBucket.Background)]
  [Arguments(250, WorkBucket.Background)]
  [Arguments(10_000, WorkBucket.Background)]
  public async Task Bucket_MapsTheBandsAsync(int priority, WorkBucket expected) {
    await Assert.That(WorkPriority.Bucket(priority)).IsEqualTo(expected);
  }

  [Test]
  [Arguments(0)]
  [Arguments(-5)]
  public async Task Bucket_TreatsUndeclaredAsStandardAsync(int priority) {
    await Assert.That(WorkPriority.Bucket(priority)).IsEqualTo(WorkBucket.Standard)
      .Because("an undeclared priority must never read as urgent; the miss lands in the middle band");
  }

  [Test]
  public async Task Effective_ReplacesUndeclaredWithStandard_AndKeepsADeclaredNumberAsync() {
    await Assert.That(WorkPriority.Effective(WorkPriority.UNDECLARED)).IsEqualTo(WorkPriority.STANDARD);
    await Assert.That(WorkPriority.Effective(-1)).IsEqualTo(WorkPriority.STANDARD);
    await Assert.That(WorkPriority.Effective(37)).IsEqualTo(37);
    await Assert.That(WorkPriority.Effective(WorkPriority.BACKGROUND + 20)).IsEqualTo(270)
      .Because("a number anywhere in a band is valid; the constants are defaults, not the only values");
  }

  [Test]
  public async Task IsDeclared_IsTrueOnlyForAPositiveNumberAsync() {
    await Assert.That(WorkPriority.IsDeclared(0)).IsFalse();
    await Assert.That(WorkPriority.IsDeclared(-3)).IsFalse();
    await Assert.That(WorkPriority.IsDeclared(1)).IsTrue();
  }
}
