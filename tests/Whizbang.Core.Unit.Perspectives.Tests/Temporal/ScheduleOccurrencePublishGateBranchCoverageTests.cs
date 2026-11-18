// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Temporal;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Branch coverage for <see cref="ScheduleOccurrencePublishGate.TryReadOccurrence"/>'s occurrence-number
/// read. Every existing case stamps an integral <c>occurrence</c>; these pin the two fallbacks: the
/// property missing entirely, and present but not representable as a <see cref="long"/>. Either way
/// the message is still a schedule occurrence (it carries a <c>scheduleId</c>), so the gate must
/// still hand it to the hook, with occurrence number 0, rather than treat it as a plain message.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Temporal/ScheduleOccurrencePublishGate.cs</code-under-test>
public class ScheduleOccurrencePublishGateBranchCoverageTests {
  private static readonly Guid _schedule = Guid.Parse("44444444-4444-4444-4444-444444444444");

  [Test]
  public async Task TryReadOccurrence_NoOccurrenceProperty_StillAnOccurrenceNumberedZeroAsync() {
    var messageId = TrackedGuid.New().Value;

    var read = ScheduleOccurrencePublishGate.TryReadOccurrence(
      $$"""{"scheduleId":"{{_schedule}}"}""", messageId, "ProbeOccurrence", out var context);

    await Assert.That(read).IsTrue()
      .Because("a scheduleId alone marks the message as a schedule occurrence");
    await Assert.That(context.ScheduleId).IsEqualTo(_schedule);
    await Assert.That(context.OccurrenceId).IsEqualTo(messageId);
    await Assert.That(context.OccurrenceNumber).IsEqualTo(0L)
      .Because("an occurrence with no stamped number falls back to 0 rather than failing the read");
  }

  [Test]
  public async Task TryReadOccurrence_NonIntegralOccurrence_StillAnOccurrenceNumberedZeroAsync() {
    var read = ScheduleOccurrencePublishGate.TryReadOccurrence(
      $$"""{"scheduleId":"{{_schedule}}","occurrence":7.5}""", TrackedGuid.New().Value, "ProbeOccurrence", out var context);

    await Assert.That(read).IsTrue()
      .Because("an unreadable occurrence number must not demote a schedule occurrence to a plain message");
    await Assert.That(context.ScheduleId).IsEqualTo(_schedule);
    await Assert.That(context.OccurrenceNumber).IsEqualTo(0L)
      .Because("a number that does not fit a long falls back to 0, never a truncated 7");
  }
}
