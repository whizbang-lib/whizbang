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
    await Assert.That(WorkPriority.IsDeclared(WorkPriority.UNDECLARED)).IsFalse()
      .Because("zero means nobody said; it is omitted on the wire, so an undeclared priority costs nothing");
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

  /// <summary>
  /// The fold helpers the framework and a host share: most urgent (the claim's rule for a stream and the default
  /// for a minted composite), least urgent, and the average, all ignoring undeclared members.
  /// </summary>
  [Test]
  public async Task Folds_IgnoreUndeclaredMembers_AndAgreeOnTheBandsAsync() {
    int[] numbers = [WorkPriority.UNDECLARED, WorkPriority.BACKGROUND, WorkPriority.INTERACTIVE, WorkPriority.STANDARD];

    await Assert.That(WorkPriority.MostUrgent(numbers)).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("the lowest declared number is the most urgent, the same rule the claim folds a stream with");
    await Assert.That(WorkPriority.LeastUrgent(numbers)).IsEqualTo(WorkPriority.BACKGROUND);
    await Assert.That(WorkPriority.Average(numbers)).IsEqualTo(150)
      .Because("(250 + 50 + 150) / 3, the undeclared member left out");
  }

  [Test]
  public async Task Folds_OverNothingDeclared_StayUndeclaredAsync() {
    int[] none = [WorkPriority.UNDECLARED, WorkPriority.UNDECLARED];

    await Assert.That(WorkPriority.MostUrgent(none)).IsEqualTo(WorkPriority.UNDECLARED)
      .Because("nobody said; the consumer's rules or the default decide, not a fold");
    await Assert.That(WorkPriority.LeastUrgent(Array.Empty<int>())).IsEqualTo(WorkPriority.UNDECLARED);
    await Assert.That(WorkPriority.Average(none)).IsEqualTo(WorkPriority.UNDECLARED);
  }

  [Test]
  public async Task FirstDeclared_PrefersTheFirstNumberSomebodySetAsync() {
    await Assert.That(WorkPriority.FirstDeclared(WorkPriority.UNDECLARED, WorkPriority.INTERACTIVE)).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("a row fetched before the column existed falls back to the number stored inside its envelope");
    await Assert.That(WorkPriority.FirstDeclared(WorkPriority.BACKGROUND, WorkPriority.INTERACTIVE)).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the row's number is authoritative once it exists");
    await Assert.That(WorkPriority.FirstDeclared(WorkPriority.UNDECLARED, WorkPriority.UNDECLARED)).IsEqualTo(WorkPriority.UNDECLARED);
  }

  [Test]
  public async Task Folds_AcceptAnythingPrioritized_NotOnlyNumbersAsync() {
    IPrioritized[] items = [new _at(WorkPriority.BACKGROUND), new _at(WorkPriority.UNDECLARED), new _at(WorkPriority.STANDARD)];

    await Assert.That(WorkPriority.MostUrgent(items)).IsEqualTo(WorkPriority.STANDARD)
      .Because("rows, envelopes and work items all expose the number through one interface, so a caller folds them without projecting");
    await Assert.That(WorkPriority.LeastUrgent(items)).IsEqualTo(WorkPriority.BACKGROUND);
    await Assert.That(WorkPriority.Average(items)).IsEqualTo(200);
  }

  private sealed record _at(int Priority) : IPrioritized;
}
