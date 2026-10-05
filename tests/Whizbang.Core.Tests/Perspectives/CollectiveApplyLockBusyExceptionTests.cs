// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The shape of a busy apply lock as a caller receives it: what the driver raises names the table and
/// the wait it gave up after, and the standard constructors keep the type usable everywhere an
/// exception is expected to behave like one.
/// </summary>
/// <remarks>
/// The type is the whole point of the distinction — a caller that cannot tell a contended lock from a
/// broken apply counts an attempt, moves the work toward dead-lettering and loses the lease, none of
/// which a busy lock warrants. The details are asserted here rather than only through a driver's
/// contention test, which can prove the type is thrown but not what it carries.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/CollectiveApplyLockBusyException.cs</code-under-test>
public class CollectiveApplyLockBusyExceptionTests {

  [Test]
  public async Task Busy_NamesTheTableTheWaitAndWhatDidNotHappenAsync() {
    var refusal = new InvalidOperationException("55P03");

    var busy = new CollectiveApplyLockBusyException("wh_per_job", 30, refusal);

    await Assert.That(busy.Table).IsEqualTo("wh_per_job")
      .Because("where the contention is, is the first thing an operator asks");
    await Assert.That(busy.WaitedSeconds).IsEqualTo(30);
    await Assert.That(busy.InnerException).IsSameReferenceAs(refusal)
      .Because("the refusal PostgreSQL raised stays reachable for anyone reading the SQLSTATE");
    await Assert.That(busy.Message).Contains("wh_per_job", StringComparison.Ordinal);
    await Assert.That(busy.Message).Contains("30s", StringComparison.Ordinal);
    await Assert.That(busy.Message).Contains("nothing was applied and nothing failed", StringComparison.Ordinal)
      .Because("the message has to settle the question the type raises, for a reader who only sees a log line");
  }

  [Test]
  public async Task Busy_WithoutACause_IsStillAWholeExceptionAsync() {
    // The driver supplies a cause; a caller re-raising or a test constructing one need not.
    var busy = new CollectiveApplyLockBusyException("wh_per_job", 5);

    await Assert.That(busy.InnerException).IsNull();
    await Assert.That(busy.Table).IsEqualTo("wh_per_job");
  }

  [Test]
  public async Task Busy_StandardConstructors_KeepMessageAndInnerAsync() {
    // The three the framework's exceptions all carry, so the type works where any exception does —
    // serialized, re-raised from a message, or wrapped. None of them knows a table, so it reads as
    // empty rather than as a table named "".
    var inner = new InvalidOperationException("inner");

    await Assert.That(new CollectiveApplyLockBusyException().Message).IsNotEmpty();
    await Assert.That(new CollectiveApplyLockBusyException().Table).IsEmpty();
    await Assert.That(new CollectiveApplyLockBusyException("m").Message).IsEqualTo("m");
    await Assert.That(new CollectiveApplyLockBusyException("m").Table).IsEmpty();

    var wrapped = new CollectiveApplyLockBusyException("m", inner);
    await Assert.That(wrapped.Message).IsEqualTo("m");
    await Assert.That(wrapped.InnerException).IsSameReferenceAs(inner);
    await Assert.That(wrapped.Table).IsEmpty();
    await Assert.That(wrapped.WaitedSeconds).IsEqualTo(0)
      .Because("no wait was reported, and 0 reads as that rather than as a wait that elapsed");
  }

  [Test]
  public async Task CollectiveApplyOptions_WaitsAgainAfterABusyLock_FiveTimesByDefaultAsync() {
    await Assert.That(CollectiveApplyOptions.Default.LockWaitRenewals).IsEqualTo(5)
      .Because("a batch behind another keeps its lease and waits up to three minutes at the default thirty-second wait");
  }
}
