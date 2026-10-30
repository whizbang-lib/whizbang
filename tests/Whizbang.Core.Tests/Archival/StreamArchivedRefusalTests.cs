// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// <see cref="StreamArchivedRefusal"/> turns the store's archived-stream refusal (SQLSTATE <c>WHA01</c> or <c>WHA02</c>,
/// migration 271) into <see cref="StreamArchivedException"/> and lets every other failure through unchanged. The policy is
/// read from the SQLSTATE alone; the stream and the version are read from the store's wording, which these tests feed it.
/// </summary>
/// <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
public class StreamArchivedRefusalTests {
  private static readonly Guid _streamId = Guid.Parse("0199f0a1-0000-7000-8000-000000000002");

  private static string _refuseMessage(Guid streamId, string version) =>
    $"Stream {streamId} is archived through version {version} and refuses appends (AppendToArchived Refuse).";

  private static string _autoRestoreMessage(Guid streamId, string version) =>
    $"Stream {streamId} is archived through version {version}; the append waits for its restore (AppendToArchived AutoRestore).";

  private static PostgresException _refusal(string message, string sqlState) => new(message, "ERROR", "ERROR", sqlState);

  [Test]
  public async Task RunAsync_WHA01_ThrowsStreamArchivedException_WithTheStreamTheVersionAndRefuseAsync() {
    var refusal = _refusal(_refuseMessage(_streamId, "41"), StreamArchivedRefusal.REFUSE_SQL_STATE);

    var thrown = await Assert.That(async () => await StreamArchivedRefusal.RunAsync(() => throw refusal))
      .Throws<StreamArchivedException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(_streamId);
    await Assert.That(thrown.ArchivedThroughVersion).IsEqualTo(41L);
    await Assert.That(thrown.Policy).IsEqualTo(AppendToArchivedPolicy.Refuse);
    await Assert.That(thrown.Message).IsEqualTo(refusal.MessageText)
      .Because("the caller reads the store's wording, the same text a dead-lettered refusal records");
    await Assert.That(thrown.InnerException).IsSameReferenceAs(refusal);
  }

  [Test]
  public async Task ToException_WHA02_IsAutoRestore_AndReadsTheVersionBeforeTheSemicolonAsync() {
    var thrown = StreamArchivedRefusal.ToException(
      _refusal(_autoRestoreMessage(_streamId, "7"), StreamArchivedRefusal.AUTO_RESTORE_SQL_STATE));

    await Assert.That(thrown.Policy).IsEqualTo(AppendToArchivedPolicy.AutoRestore);
    await Assert.That(thrown.StreamId).IsEqualTo(_streamId);
    await Assert.That(thrown.ArchivedThroughVersion).IsEqualTo(7L);
  }

  [Test]
  public async Task ToException_ThePolicyComesFromTheSqlState_NeverFromTheWordingAsync() {
    // The AutoRestore wording under the Refuse state: the state decides, so a reworded message can never flip the policy.
    var thrown = StreamArchivedRefusal.ToException(
      _refusal(_autoRestoreMessage(_streamId, "7"), StreamArchivedRefusal.REFUSE_SQL_STATE));

    await Assert.That(thrown.Policy).IsEqualTo(AppendToArchivedPolicy.Refuse);
  }

  [Test]
  public async Task ToException_AMessageWithoutTheExpectedShape_KeepsTheMessage_AndLeavesTheFieldsEmptyAsync() {
    var thrown = StreamArchivedRefusal.ToException(_refusal("the stream is archived", StreamArchivedRefusal.REFUSE_SQL_STATE));

    await Assert.That(thrown.StreamId).IsEqualTo(Guid.Empty)
      .Because("a store older or newer than this reader may word it differently; the message is kept either way");
    await Assert.That(thrown.ArchivedThroughVersion).IsNull();
    await Assert.That(thrown.Message).IsEqualTo("the stream is archived");
  }

  [Test]
  public async Task ToException_AnUnreadableIdOrVersion_LeavesThatFieldEmptyAsync() {
    var thrown = StreamArchivedRefusal.ToException(_refusal(
      _refuseMessage(_streamId, "many").Replace(_streamId.ToString(), "not-a-guid-0000-0000-0000-000000000000", StringComparison.Ordinal),
      StreamArchivedRefusal.REFUSE_SQL_STATE));

    await Assert.That(thrown.StreamId).IsEqualTo(Guid.Empty);
    await Assert.That(thrown.ArchivedThroughVersion).IsNull();
  }

  [Test]
  public async Task ToException_TheVersionAtTheEndOfTheMessage_IsReadAsync() {
    var thrown = StreamArchivedRefusal.ToException(
      _refusal($"Stream {_streamId} is archived through version 12", StreamArchivedRefusal.REFUSE_SQL_STATE));

    await Assert.That(thrown.StreamId).IsEqualTo(_streamId);
    await Assert.That(thrown.ArchivedThroughVersion).IsEqualTo(12L);
  }

  [Test]
  public async Task RunAsync_AnyOtherStoreFailure_PropagatesUnchangedAsync() {
    var other = new PostgresException("duplicate key value", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation);

    var thrown = await Assert.That(async () => await StreamArchivedRefusal.RunAsync(() => throw other))
      .Throws<PostgresException>();

    await Assert.That(thrown).IsSameReferenceAs(other);
  }

  [Test]
  public async Task RunAsync_StoreSucceeds_CompletesAsync() {
    var ran = false;

    await StreamArchivedRefusal.RunAsync(() => {
      ran = true;
      return Task.CompletedTask;
    });

    await Assert.That(ran).IsTrue();
  }
}
