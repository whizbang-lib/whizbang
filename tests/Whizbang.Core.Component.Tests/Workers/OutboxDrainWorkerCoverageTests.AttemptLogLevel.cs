// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A failed publish attempt is retried: the row goes to the failure channel, its attempts are counted, and only past
/// the bound is it dead-lettered, which logs the dead-letter record (EventId 1901) at the configured level. Each
/// attempt therefore logs at Warning with its exception, so an alert on Error fires for the dead letter and not for
/// every attempt before it (#1976).
/// </summary>
public partial class OutboxDrainWorkerCoverageTests {

  private const int DESERIALIZE_FAILED_EVENT_ID = 6;
  private const int PUBLISH_FAILED_EVENT_ID = 7;

  /// <summary>A bulk-capable strategy whose batch publish throws.</summary>
  private sealed class ThrowingBulkStrategy : IMessagePublishStrategy {
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("PublishAsync must not be called on a bulk-capable strategy");
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("broker refused the batch");
  }

  [Test]
  public async Task PublishOneAsync_PublishThrows_LogsTheAttemptAtWarningWithItsExceptionAsync() {
    var failure = new FailureChannel();
    var logger = new DeadLetterRecordLogger<OutboxDrainWorker>();
    var worker = _buildDirectCallWorker(failure, publish: new ThrowIfCalledPublishStrategy(), logger: logger);
    var row = _row((Guid)TrackedGuid.New(), (Guid)TrackedGuid.New());

    await worker.PublishOneAsync(row, CancellationToken.None);

    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("the failed attempt is recorded, so the attempts bound governs it and the row is retried");
    await _assertAttemptLoggedAtWarningAsync(logger, PUBLISH_FAILED_EVENT_ID);
  }

  [Test]
  public async Task PublishBulkAsync_BatchPublishThrows_LogsEachAttemptAtWarningWithItsExceptionAsync() {
    var failure = new FailureChannel();
    var logger = new DeadLetterRecordLogger<OutboxDrainWorker>();
    var worker = _buildDirectCallWorker(failure, publish: new ThrowingBulkStrategy(), logger: logger);
    var row = _row((Guid)TrackedGuid.New(), (Guid)TrackedGuid.New());

    await worker.PublishBulkAsync([row], CancellationToken.None);

    await Assert.That(failure.All.Count).IsEqualTo(1);
    await _assertAttemptLoggedAtWarningAsync(logger, PUBLISH_FAILED_EVENT_ID);
  }

  [Test]
  public async Task PublishOneAsync_EnvelopeDoesNotDeserialize_LogsTheAttemptAtWarningWithItsExceptionAsync() {
    var failure = new FailureChannel();
    var logger = new DeadLetterRecordLogger<OutboxDrainWorker>();
    var worker = _buildDirectCallWorker(failure, publish: new ThrowIfCalledPublishStrategy(), logger: logger);
    var row = _row((Guid)TrackedGuid.New(), (Guid)TrackedGuid.New()) with { EventData = "{ not an envelope" };

    await worker.PublishOneAsync(row, CancellationToken.None);

    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("the row is failed, so its attempts are counted and the pre-publish gate dead-letters it at the bound");
    await _assertAttemptLoggedAtWarningAsync(logger, DESERIALIZE_FAILED_EVENT_ID);
  }

  private static async Task _assertAttemptLoggedAtWarningAsync(DeadLetterRecordLogger<OutboxDrainWorker> logger, int eventId) {
    var lines = logger.Entries.Where(e => e.EventId == eventId).ToList();
    await Assert.That(lines.Count).IsEqualTo(1);
    await Assert.That(lines[0].Level).IsEqualTo(LogLevel.Warning)
      .Because("a failed attempt that will be retried is a Warning; Error is kept for the dead-letter record");
    await Assert.That(lines[0].Exception).IsNotNull()
      .Because("the attempt's exception is attached, so the Warning says what failed");
    await Assert.That(logger.Entries.Any(e => e.Level >= LogLevel.Error)).IsFalse()
      .Because("nothing was dead-lettered, so nothing logs at Error");
  }
}
