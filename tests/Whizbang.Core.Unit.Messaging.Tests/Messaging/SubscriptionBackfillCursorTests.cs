// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2092: a running backfill estimates its remaining events from the density of what has arrived over the span still
/// ahead of it, so the remaining-events gauge falls as the backfill progresses.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/SubscriptionBackfill.cs</code-under-test>
public class SubscriptionBackfillCursorTests {
  private static readonly DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task EstimateRemaining_ScalesWhatArrivedOverTheSpanAheadAsync() {
    // 100 events between sequences 101 and 300 (half the sequences are this type); 400 sequences remain to the head.
    var cursor = _cursor() with { FirstCommitSequence = 101, CursorCommitSequence = 300, HeadCommitSequence = 700, EventsReceived = 100 };

    await Assert.That(cursor.EstimateRemaining()).IsEqualTo(200L);
  }

  [Test]
  public async Task EstimateRemaining_AnEndedBackfill_HasNothingLeftAsync() {
    var cursor = _cursor() with { CompletedAt = _now, Outcome = SubscriptionBackfillOutcome.Complete };

    await Assert.That(cursor.EstimateRemaining()).IsEqualTo(0L);
  }

  [Test]
  public async Task EstimateRemaining_AtOrPastTheHead_IsZeroAsync() {
    var cursor = _cursor() with { FirstCommitSequence = 1, CursorCommitSequence = 50, HeadCommitSequence = 50, EventsReceived = 10 };

    await Assert.That(cursor.EstimateRemaining()).IsEqualTo(0L);
  }

  [Test]
  public async Task EstimateRemaining_BeforeAnyEvent_IsUnknownAsync() {
    await Assert.That(_cursor().EstimateRemaining()).IsNull();
  }

  [Test]
  public async Task EstimateRemaining_WithoutAFirstSequence_IsUnknownAsync() {
    var cursor = _cursor() with { CursorCommitSequence = 50, HeadCommitSequence = 90 };

    await Assert.That(cursor.EstimateRemaining()).IsNull();
  }

  [Test]
  public async Task EstimateRemaining_WithoutAHead_IsUnknownAsync() {
    var cursor = _cursor() with { FirstCommitSequence = 1, CursorCommitSequence = 50 };

    await Assert.That(cursor.EstimateRemaining()).IsNull();
  }

  private static SubscriptionBackfillCursor _cursor() => new() {
    BackfillId = Guid.Parse("0b000000-0000-0000-0000-00000000000b"),
    OwnerServiceId = Guid.Parse("0a000000-0000-0000-0000-00000000000a"),
    OwnerServiceName = "orders",
    EventType = "App.Contracts.Orders.Placed, App.Contracts",
  };
}
