// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2366: the memory of each stream's last completed rewind. A request with the same trigger and the same last version
/// is a repeat and returns the last completion; anything else is new input. The memory is bounded.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/RewindRepeatGuard.cs</code-under-test>
/// <docs>fundamentals/perspectives/rewind#a-repeated-rewind-is-settled</docs>
public class RewindRepeatGuardTests {
  private const string PERSPECTIVE = "Guard.Perspective";

  private static PerspectiveCursorCompletion _completion(Guid streamId) => new() {
    StreamId = streamId,
    PerspectiveName = PERSPECTIVE,
    LastEventId = Guid.CreateVersion7(),
    Status = PerspectiveProcessingStatus.Completed,
  };

  [Test]
  public async Task TheSameTriggerAndVersion_IsARepeat_CountedEachTimeAsync() {
    var guard = new RewindRepeatGuard();
    var (stream, trigger) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    var completion = _completion(stream);
    guard.Remember(stream, PERSPECTIVE, trigger, 4, completion);

    var first = guard.Repeat(stream, PERSPECTIVE, trigger, 4);
    var second = guard.Repeat(stream, PERSPECTIVE, trigger, 4);

    await Assert.That(first!.Completion).IsSameReferenceAs(completion);
    await Assert.That(first.Repeats).IsEqualTo(1);
    await Assert.That(second!.Repeats).IsEqualTo(2);
  }

  [Test]
  public async Task AnotherTrigger_AnotherVersion_OrAStreamNeverRewound_IsNewInputAsync() {
    var guard = new RewindRepeatGuard();
    var (stream, trigger) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    guard.Remember(stream, PERSPECTIVE, trigger, 4, _completion(stream));

    await Assert.That(guard.Repeat(stream, PERSPECTIVE, Guid.CreateVersion7(), 4)).IsNull();
    await Assert.That(guard.Repeat(stream, PERSPECTIVE, trigger, 5)).IsNull();
    await Assert.That(guard.Repeat(Guid.CreateVersion7(), PERSPECTIVE, trigger, 4)).IsNull();
    await Assert.That(guard.Repeat(stream, "Guard.Other", trigger, 4)).IsNull();
  }

  [Test]
  public async Task PastItsCapacity_TheMemoryIsCleared_AndAStreamAlreadyHeldIsReplacedInPlaceAsync() {
    var guard = new RewindRepeatGuard(capacity: 2);
    var (a, b, c) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    var trigger = Guid.CreateVersion7();
    guard.Remember(a, PERSPECTIVE, trigger, 1, _completion(a));
    guard.Remember(b, PERSPECTIVE, trigger, 1, _completion(b));
    guard.Remember(b, PERSPECTIVE, trigger, 2, _completion(b));

    await Assert.That(guard.Repeat(a, PERSPECTIVE, trigger, 1)).IsNotNull()
      .Because("replacing a stream already held is not a new stream, so nothing is cleared");

    guard.Remember(c, PERSPECTIVE, trigger, 1, _completion(c));

    await Assert.That(guard.Capacity).IsEqualTo(2);
    await Assert.That(guard.Repeat(a, PERSPECTIVE, trigger, 1)).IsNull().Because("a new stream past capacity clears the memory");
    await Assert.That(guard.Repeat(c, PERSPECTIVE, trigger, 1)).IsNotNull();
  }
}
