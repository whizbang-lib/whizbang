// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// <see cref="StreamSealedRefusal"/> turns the store's sealed-stream refusal (SQLSTATE <c>WHS01</c>, migration 266) into
/// <see cref="StreamSealedException"/> and lets every other failure through unchanged. The store's wording is built in
/// SQL, so these tests feed the translator that wording and the shapes it must survive.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
public class StreamSealedRefusalTests {
  private static readonly Guid _streamId = Guid.Parse("0199f0a1-0000-7000-8000-000000000001");
  private static readonly DateTimeOffset _sealedAt = new DateTimeOffset(2026, 10, 29, 12, 10, 5, TimeSpan.Zero).AddTicks(1234560);

  private static string _storeMessage(Guid streamId, string sealedAt, string reason) =>
    $"Stream {streamId} was sealed at {sealedAt} and refuses appends. Reason: {reason}";

  private static PostgresException _refusal(string message) =>
    new(message, "ERROR", "ERROR", StreamSealedRefusal.SQL_STATE);

  [Test]
  public async Task RunAsync_StoreRefusesASealedStream_ThrowsStreamSealedException_WithTheStreamTimeAndReasonAsync() {
    var refusal = _refusal(_storeMessage(_streamId, "2026-10-29T12:10:05.123456Z", "Account closed. Reason: fraud"));

    var thrown = await Assert.That(async () => await StreamSealedRefusal.RunAsync(() => throw refusal))
      .Throws<StreamSealedException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(_streamId);
    await Assert.That(thrown.SealedAt).IsEqualTo(_sealedAt)
      .Because("the store writes the seal time to the microsecond, so the caller reads back exactly what the ledger holds");
    await Assert.That(thrown.Reason).IsEqualTo("Account closed. Reason: fraud")
      .Because("the reason is the last thing in the message, so a reason may contain any text, the marker included");
    await Assert.That(thrown.Message).IsEqualTo(refusal.MessageText)
      .Because("the caller reads the store's wording, the same text a dead-lettered refusal records");
    await Assert.That(thrown.InnerException).IsSameReferenceAs(refusal);
  }

  [Test]
  public async Task ToException_AMessageWithoutTheExpectedShape_KeepsTheMessage_AndLeavesTheFieldsEmptyAsync() {
    var refusal = _refusal("the stream is sealed");

    var thrown = StreamSealedRefusal.ToException(refusal);

    await Assert.That(thrown.StreamId).IsEqualTo(Guid.Empty)
      .Because("a store older or newer than this reader may word it differently; the message is kept either way");
    await Assert.That(thrown.SealedAt).IsNull();
    await Assert.That(thrown.Reason).IsNull();
    await Assert.That(thrown.Message).IsEqualTo("the stream is sealed");
  }

  [Test]
  public async Task ToException_AnUnreadableIdOrTime_LeavesThatFieldEmpty_AndReadsTheRestAsync() {
    var refusal = _refusal(_storeMessage(_streamId, "not-a-time", "Closed").Replace(_streamId.ToString(), "not-a-guid-0000-0000-0000-000000000000", StringComparison.Ordinal));

    var thrown = StreamSealedRefusal.ToException(refusal);

    await Assert.That(thrown.StreamId).IsEqualTo(Guid.Empty);
    await Assert.That(thrown.SealedAt).IsNull();
    await Assert.That(thrown.Reason).IsEqualTo("Closed");
  }

  [Test]
  public async Task ToException_TheSealTimeWithoutTheReason_LeavesTheFieldsEmptyAsync() {
    var thrown = StreamSealedRefusal.ToException(_refusal($"Stream {_streamId} was sealed at 2026-10-29T12:10:05.123456Z"));

    await Assert.That(thrown.StreamId).IsEqualTo(Guid.Empty);
    await Assert.That(thrown.SealedAt).IsNull();
    await Assert.That(thrown.Reason).IsNull();
  }

  [Test]
  public async Task ToException_AMessageNotStartingWithTheStream_ReadsNoStream_AndTheRestAsync() {
    var thrown = StreamSealedRefusal.ToException(_refusal(
      $"Event stream {_streamId} was sealed at 2026-10-29T12:10:05.123456Z and refuses appends. Reason: Closed"));

    await Assert.That(thrown.StreamId).IsEqualTo(Guid.Empty)
      .Because("the id is read only where the store writes it, at the start of the message");
    await Assert.That(thrown.SealedAt).IsEqualTo(_sealedAt);
    await Assert.That(thrown.Reason).IsEqualTo("Closed");
  }

  [Test]
  public async Task RunAsync_AnyOtherStoreFailure_PropagatesUnchangedAsync() {
    var other = new PostgresException("duplicate key value", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation);

    var thrown = await Assert.That(async () => await StreamSealedRefusal.RunAsync(() => throw other))
      .Throws<PostgresException>();

    await Assert.That(thrown).IsSameReferenceAs(other);
  }

  [Test]
  public async Task RunAsync_StoreSucceeds_CompletesAsync() {
    var ran = false;

    await StreamSealedRefusal.RunAsync(() => {
      ran = true;
      return Task.CompletedTask;
    });

    await Assert.That(ran).IsTrue();
  }
}
