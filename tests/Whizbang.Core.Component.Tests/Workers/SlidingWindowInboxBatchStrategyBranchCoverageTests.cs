// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A failed bulk flush on a <see cref="SlidingWindowInboxBatchStrategy"/> built without a logger.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowInboxBatchStrategy.cs</code-under-test>
[Category("Workers")]
public class SlidingWindowInboxBatchStrategyBranchCoverageTests {

  private static InboxMessage _makeMessage(Guid streamId) {
    var messageId = (Guid)TrackedGuid.New();
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "test",
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), JsonElement.Parse("{}"), []),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = streamId,
    };
  }

  [Test]
  [Timeout(30000)]
  public async Task FailedFlush_WithoutALogger_IsReportedAndTheStreamKeepsFlushingAsync(CancellationToken cancellationToken) {
    // If reporting the failure threw for want of a logger, the stream's drain loop would die on the
    // first failed flush and every later message for that stream would sit unflushed. The failure is
    // not a transient database failure, so it is reported at once rather than retried (#1885).
    var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var attempts = 0;
    var window = TimeSpan.FromMilliseconds(10);
    var clock = new BatchWindowClock(window);
    var reported = 0;
    await using var sut = new SlidingWindowInboxBatchStrategy(
      flush: (_, _) => {
        var n = Interlocked.Increment(ref attempts);
        (n == 1 ? firstAttempt : secondAttempt).TrySetResult();
        throw new InvalidOperationException("inbox store unavailable");
      },
      logger: null!,
      options: new SlidingWindowInboxOptions {
        SlidingWindow = window,
        MaxWait = TimeSpan.FromMilliseconds(50),
        MaxSize = 100,
      },
      timeProvider: clock,
      onFlushFailed: (_, _) => {
        Interlocked.Increment(ref reported);
        return Task.FromResult(true);
      },
      metrics: null);
    var streamId = Guid.CreateVersion7();

    // Each window is closed by the clock once its timer is armed, never by how fast the runner is
    await sut.AppendAsync(_makeMessage(streamId), cancellationToken);
    await clock.Armed(1).WaitAsync(cancellationToken);
    clock.Advance(window);
    await firstAttempt.Task.WaitAsync(cancellationToken);
    await sut.AppendAsync(_makeMessage(streamId), cancellationToken);
    await clock.Armed(2).WaitAsync(cancellationToken);
    clock.Advance(window);
    await secondAttempt.Task.WaitAsync(cancellationToken);

    await Assert.That(Volatile.Read(ref attempts)).IsGreaterThanOrEqualTo(2)
      .Because("the same stream flushed again after a failed flush with no logger to report it to");
    await Assert.That(Volatile.Read(ref reported)).IsGreaterThanOrEqualTo(1)
      .Because("the failed batch was handed to the failure callback, not dropped");
  }
}
