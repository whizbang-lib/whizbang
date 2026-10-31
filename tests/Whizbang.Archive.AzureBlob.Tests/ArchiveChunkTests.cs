// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;

namespace Whizbang.Archive.AzureBlob.Tests;

/// <summary>
/// The archive object format (format 1): JSON lines in version order, compressed by the codec the locator records, named
/// by stream, version range and content hash, and read back exactly.
/// </summary>
/// <docs>fundamentals/events/archive-store#azure-blob-object-format</docs>
public class ArchiveChunkTests {
  private static readonly Guid _stream = Guid.Parse("0199a1b2-0000-7000-8000-000000000001");
  private static readonly DateTimeOffset _at = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

  private static ArchiveChunkLine _full(long version) => new(
    Guid.Parse($"0199a1b2-0000-7000-8000-{version:D12}"), _stream, version, "Orders.LineAdded, Orders",
    """{"Note": "zßé \"quoted\" a\nb", "Lines": [3, 1, 2]}""", """{"a": "two", "z": 1}""", """{"t": "tenant-1"}""",
    _stream, "Order", _at.AddTicks(version * 10), Flags: 32, CommitSequence: 100 + version,
    OriginServiceId: Guid.Parse("0199a1b2-0000-7000-8000-0000000000ff"), OriginCommitSequence: 7, InsertOrder: 1000 + version);

  // A row archived before the store kept every column: everything nullable is null.
  private static ArchiveChunkLine _sparse(long version) => new(
    Guid.Parse($"0199a1b2-0000-7000-9000-{version:D12}"), _stream, version, "Orders.LineAdded, Orders",
    null, null, null, null, null, null, null, null, null, null, null);

  private static ArchiveChunkLine[] _lines(int count) => [.. Enumerable.Range(1, count).Select(v => _full(v))];

  [Test]
  public async Task Brotli_RoundTrip_ReturnsEveryLineExactly_NullsAndEscapesIncludedAsync() {
    ArchiveChunkLine[] lines = [_full(1), _sparse(2), _full(3)];

    var bytes = ArchiveChunk.Encode(lines, ArchiveCompression.Brotli, quality: 4);
    var read = ArchiveChunk.Decode(bytes, ArchiveCompression.Brotli);

    await Assert.That(read).IsEquivalentTo(lines, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a read must return each event exactly as it was archived, in version order");
    await Assert.That(read[0].EventData).IsEqualTo(lines[0].EventData)
      .Because("the body is the jsonb text itself, quotes, newlines and non-ASCII included");
    await Assert.That(bytes[0]).IsNotEqualTo((byte)'{').Because("the stored bytes are compressed, not the JSON lines");
  }

  [Test]
  public async Task None_RoundTrip_IsPlainJsonLines_OnePerEventAsync() {
    ArchiveChunkLine[] lines = [_full(1), _sparse(2)];

    var bytes = ArchiveChunk.Encode(lines, ArchiveCompression.None, quality: 4);
    var text = Encoding.UTF8.GetString(bytes);

    await Assert.That(text.Split('\n')).Count().IsEqualTo(3).Because("one line per event, each ending in a newline");
    await Assert.That(text).StartsWith("{\"event_id\":\"0199a1b2-0000-7000-8000-000000000001\",\"stream_id\":");
    await Assert.That(text).Contains("\"event_data\":null").Because("a missing column is written as null, never left out");
    await Assert.That(ArchiveChunk.Decode(bytes, ArchiveCompression.None))
      .IsEquivalentTo(lines, TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Encode_IsDeterministic_SoARetryWritesTheSameObjectName_AndOtherEventsAnotherAsync() {
    var first = ArchiveChunk.Encode(_lines(5), ArchiveCompression.Brotli, quality: 4);
    var again = ArchiveChunk.Encode(_lines(5), ArchiveCompression.Brotli, quality: 4);
    var other = ArchiveChunk.Encode([.. _lines(4), _sparse(5)], ArchiveCompression.Brotli, quality: 4);

    await Assert.That(again).IsEquivalentTo(first, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a retried write must produce the same bytes, so it lands on the same name and never a second object");
    await Assert.That(ArchiveChunk.Sha256(other)).IsNotEqualTo(ArchiveChunk.Sha256(first))
      .Because("the control: different events hash to a different name");
  }

  [Test]
  public async Task Quality_IsPassedToTheEncoder_HigherQualityWritesFewerBytesAsync() {
    var lines = _lines(200);

    var fastest = ArchiveChunk.Encode(lines, ArchiveCompression.Brotli, quality: 0);
    var smallest = ArchiveChunk.Encode(lines, ArchiveCompression.Brotli, quality: 11);
    var plain = ArchiveChunk.Encode(lines, ArchiveCompression.None, quality: 0);

    await Assert.That(smallest.Length).IsLessThan(fastest.Length).Because("the configured quality must reach the encoder");
    await Assert.That(fastest.Length).IsLessThan(plain.Length).Because("even the fastest quality compresses");
  }

  [Test]
  public async Task Decode_WithTheWrongCodec_FailsRatherThanReturningEventsAsync() {
    var brotli = ArchiveChunk.Encode(_lines(2), ArchiveCompression.Brotli, quality: 4);

    await Assert.That(() => ArchiveChunk.Decode(brotli, ArchiveCompression.None)).Throws<JsonException>()
      .Because("the codec is read from each object's own locator; decoding by any other is an error, not data");
  }

  [Test]
  public async Task Decode_ALastLineWithoutANewLine_IsStillReadAsync() {
    var bytes = ArchiveChunk.Encode([_sparse(1)], ArchiveCompression.None, quality: 0);

    var read = ArchiveChunk.Decode(bytes[..^1], ArchiveCompression.None);

    await Assert.That(read).IsEquivalentTo([_sparse(1)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Decode_ANullLine_IsRefusedAsync() {
    await Assert.That(() => ArchiveChunk.Decode(Encoding.UTF8.GetBytes("null\n"), ArchiveCompression.None))
      .Throws<InvalidDataException>();
  }

  [Test]
  public async Task AnUndefinedCodec_IsRefusedEverywhereAsync() {
    const ArchiveCompression undefined = (ArchiveCompression)99;

    await Assert.That(() => ArchiveChunk.Encode(_lines(1), undefined, 4)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => ArchiveChunk.Decode([1], undefined)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => ArchiveChunk.ObjectName(_stream, 1, 2, "ab", undefined)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task ObjectName_IsStreamThenZeroPaddedRangeThenHash_WithTheCodecsExtensionAsync() {
    await Assert.That(ArchiveChunk.ObjectName(_stream, 1, 10_000, "abc123", ArchiveCompression.Brotli))
      .IsEqualTo("0199a1b2-0000-7000-8000-000000000001/0000000001-0000010000-abc123.jsonl.br")
      .Because("a stream's objects share its prefix and list in version order");
    await Assert.That(ArchiveChunk.ObjectName(_stream, 0, 0, "abc123", ArchiveCompression.None))
      .IsEqualTo("0199a1b2-0000-7000-8000-000000000001/0000000000-0000000000-abc123.jsonl");
  }

  [Test]
  public async Task Sha256_IsLowercaseHexOfTheStoredBytesAsync() {
    await Assert.That(ArchiveChunk.Sha256(Encoding.ASCII.GetBytes("abc")))
      .IsEqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  }

  [Test]
  public async Task TryReadObjectLocator_ReadsContainerNameAndCodec_FromALocatorThisStoreWroteAsync() {
    var read = ArchiveChunk.TryReadObjectLocator(
      "azure-blob://archive-a/0199a1b2-0000-7000-8000-000000000001/0000000001-0000000002-ab.jsonl.br?codec=brotli&format=1",
      out var container, out var name, out var compression);

    await Assert.That(read).IsTrue();
    await Assert.That(container).IsEqualTo("archive-a");
    await Assert.That(name).IsEqualTo("0199a1b2-0000-7000-8000-000000000001/0000000001-0000000002-ab.jsonl.br")
      .Because("only the first slash separates the container; the name keeps the stream prefix");
    await Assert.That(compression).IsEqualTo(ArchiveCompression.Brotli);
  }

  [Test]
  [Arguments("not a locator")]
  [Arguments("postgres-table://archive-a/x?codec=none&format=1")]
  [Arguments("azure-blob://archive-a/x?codec=brotli&format=2")]
  [Arguments("azure-blob://archive-a?codec=brotli&format=1")]
  [Arguments("azure-blob:///x?codec=brotli&format=1")]
  [Arguments("azure-blob://archive-a/?codec=brotli&format=1")]
  public async Task TryReadObjectLocator_RefusesWhatThisStoreDidNotWrite_RatherThanGuessingAsync(string text) {
    var read = ArchiveChunk.TryReadObjectLocator(text, out _, out _, out _);

    await Assert.That(read).IsFalse()
      .Because("another store, a format this release does not write, or an address without a container and a name");
  }

  [Test]
  public async Task ToArchivedEvent_CarriesEveryColumnTheReadReturnsAsync() {
    var line = _full(3);

    var archived = line.ToArchivedEvent();

    await Assert.That(archived.EventId).IsEqualTo(line.EventId);
    await Assert.That(archived.StreamId).IsEqualTo(_stream);
    await Assert.That(archived.Version).IsEqualTo(3L);
    await Assert.That(archived.EventType).IsEqualTo(line.EventType);
    await Assert.That(archived.EventDataJson).IsEqualTo(line.EventData);
    await Assert.That(archived.MetadataJson).IsEqualTo(line.Metadata);
    await Assert.That(archived.ScopeJson).IsEqualTo(line.Scope);
    await Assert.That(archived.AggregateId).IsEqualTo(line.AggregateId);
    await Assert.That(archived.AggregateType).IsEqualTo("Order");
    await Assert.That(archived.CreatedAt).IsEqualTo(line.CreatedAt);
    await Assert.That(archived.Flags).IsEqualTo(32);
    // #1738: the commit positions, so a read through the archive orders the event with the hot ones as it was ordered
    // before it was archived.
    await Assert.That(archived.CommitSequence).IsEqualTo(line.CommitSequence);
    await Assert.That(archived.OriginServiceId).IsEqualTo(line.OriginServiceId);
    await Assert.That(archived.OriginCommitSequence).IsEqualTo(line.OriginCommitSequence);
  }
}
