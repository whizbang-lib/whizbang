// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// An archive batch is one stream, and either a prefix through a version or the whole stream (WS-3 S7). The two are
/// distinct requests on purpose: a prefix keeps the truncate's carry-forward guard, so it can never empty a stream by
/// accident; only an explicit whole-stream batch leaves a tombstone.
/// </summary>
/// <docs>fundamentals/events/archive-store#batches</docs>
public class ArchiveBatchTests {
  private static readonly Guid _stream = new("0192f1a0-0000-7000-8000-000000000001");

  [Test]
  public async Task Prefix_CarriesItsVersion_AndIsNotAWholeStreamBatchAsync() {
    var batch = ArchiveBatch.Prefix(_stream, 7);

    await Assert.That(batch.StreamId).IsEqualTo(_stream);
    await Assert.That(batch.ThroughVersion).IsEqualTo(7L);
    await Assert.That(batch.IsWholeStream).IsFalse();
  }

  [Test]
  public async Task WholeStream_HasNoVersion_AndSaysSoAsync() {
    var batch = ArchiveBatch.WholeStream(_stream);

    await Assert.That(batch.ThroughVersion).IsNull();
    await Assert.That(batch.IsWholeStream).IsTrue();
  }

  [Test]
  public async Task Prefix_AcceptsVersionZero_RefusesANegativeVersionAsync() {
    // A stream a direct append created before #1913 starts at version 0, so 0 is a real prefix; -1 names nothing.
    await Assert.That(ArchiveBatch.Prefix(_stream, 0).ThroughVersion).IsEqualTo(0L);
    await Assert.That(() => ArchiveBatch.Prefix(_stream, -1)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task TheEmptyStreamId_IsRefusedByBothFormsAsync() {
    await Assert.That(() => ArchiveBatch.Prefix(Guid.Empty, 1)).Throws<ArgumentException>()
      .Because("the empty id is the store's 'no stream' row; archiving it would sweep unrelated events");
    await Assert.That(() => ArchiveBatch.WholeStream(Guid.Empty)).Throws<ArgumentException>();
  }

  [Test]
  public async Task WriteResult_IsArchivedOnlyForTheArchivedStatusAsync() {
    await Assert.That(new ArchiveWriteResult(ArchiveStatus.ARCHIVED, 2, null, null, 1).Archived).IsTrue();
    await Assert.That(new ArchiveWriteResult(ArchiveStatus.BLOCKED, 0, null, null, 1).Archived).IsFalse();
  }
}
