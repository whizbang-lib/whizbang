// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;

namespace Whizbang.Archive.AzureBlob;

/// <summary>
/// One archived event as an archive object holds it (format 1): every column of <c>wh_event_archive</c> a restore needs,
/// with the body, metadata and scope as the exact <c>jsonb</c> text the hot store returned, so a read is byte-identical.
/// </summary>
/// <docs>fundamentals/events/archive-store#azure-blob-object-format</docs>
internal sealed record ArchiveChunkLine(
  [property: JsonPropertyName("event_id")] Guid EventId,
  [property: JsonPropertyName("stream_id")] Guid StreamId,
  [property: JsonPropertyName("version")] long Version,
  [property: JsonPropertyName("event_type")] string EventType,
  [property: JsonPropertyName("event_data")] string? EventData,
  [property: JsonPropertyName("metadata")] string? Metadata,
  [property: JsonPropertyName("scope")] string? Scope,
  [property: JsonPropertyName("aggregate_id")] Guid? AggregateId,
  [property: JsonPropertyName("aggregate_type")] string? AggregateType,
  [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
  [property: JsonPropertyName("flags")] int? Flags,
  [property: JsonPropertyName("commit_sequence")] long? CommitSequence,
  [property: JsonPropertyName("origin_service_id")] Guid? OriginServiceId,
  [property: JsonPropertyName("origin_commit_sequence")] long? OriginCommitSequence,
  [property: JsonPropertyName("insert_order")] long? InsertOrder) {

  /// <summary>The event as <see cref="IArchiveStore.ReadAsync"/> returns it.</summary>
  public ArchivedEvent ToArchivedEvent() =>
    new(EventId, StreamId, Version, EventType, EventData, Metadata) {
      ScopeJson = Scope,
      AggregateId = AggregateId,
      AggregateType = AggregateType,
      CreatedAt = CreatedAt,
      Flags = Flags,
      CommitSequence = CommitSequence,
      OriginServiceId = OriginServiceId,
      OriginCommitSequence = OriginCommitSequence,
    };
}

/// <summary>Source-generated JSON for <see cref="ArchiveChunkLine"/>: no reflection, the same bytes every time.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ArchiveChunkLine))]
internal sealed partial class ArchiveChunkJsonContext : JsonSerializerContext;

/// <summary>
/// How an archive object is written and read: JSON lines (one <see cref="ArchiveChunkLine"/> per line, in version
/// order), compressed by the codec its locator records, named by its stream, version range and content hash.
/// </summary>
/// <docs>fundamentals/events/archive-store#azure-blob-object-format</docs>
/// <tests>tests/Whizbang.Archive.AzureBlob.Tests/ArchiveChunkTests.cs</tests>
internal static class ArchiveChunk {
  private const byte NEW_LINE = (byte)'\n';

  /// <summary>Writes the lines as JSON lines, compressed by <paramref name="compression"/>.</summary>
  public static byte[] Encode(IReadOnlyList<ArchiveChunkLine> lines, ArchiveCompression compression, int quality) {
    using var output = new MemoryStream();
    switch (compression) {
      case ArchiveCompression.None:
        _writeLines(output, lines);
        break;
      case ArchiveCompression.Brotli:
        using (var brotli = new BrotliStream(output, new BrotliCompressionOptions { Quality = quality }, leaveOpen: true)) {
          _writeLines(brotli, lines);
        }
        break;
      default:
        throw _unknown(compression);
    }
    return output.ToArray();
  }

  /// <summary>Reads what <see cref="Encode"/> wrote, by the codec the object's locator records.</summary>
  public static IReadOnlyList<ArchiveChunkLine> Decode(byte[] bytes, ArchiveCompression compression) {
    ReadOnlyMemory<byte> text = (compression switch {
      ArchiveCompression.None => bytes,
      ArchiveCompression.Brotli => _decompress(bytes),
      _ => throw _unknown(compression),
    }).AsMemory();
    var lines = new List<ArchiveChunkLine>();
    while (!text.IsEmpty) {
      var end = text.Span.IndexOf(NEW_LINE);
      var line = end < 0 ? text : text[..end];
      lines.Add(JsonSerializer.Deserialize(line.Span, ArchiveChunkJsonContext.Default.ArchiveChunkLine)
        ?? throw new InvalidDataException("An archive object holds a null line."));
      text = end < 0 ? ReadOnlyMemory<byte>.Empty : text[(end + 1)..];
    }
    return lines;
  }

  /// <summary>The lowercase hex SHA-256 of an object's stored bytes.</summary>
  public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

  /// <summary>
  /// An object's name: <c>{stream}/{first}-{last}-{sha256}.jsonl.br</c> (<c>.jsonl</c> uncompressed). The hash makes
  /// the name follow the content, so a retried write of the same events writes the same object, never a second one.
  /// </summary>
  public static string ObjectName(Guid streamId, long firstVersion, long lastVersion, string sha256, ArchiveCompression compression) =>
    string.Create(CultureInfo.InvariantCulture,
      $"{streamId:D}/{firstVersion:D10}-{lastVersion:D10}-{sha256}{_extension(compression)}");

  /// <summary>
  /// Reads the codec of an object from the extension <see cref="ObjectName"/> gave its name (<c>.jsonl.br</c> Brotli,
  /// <c>.jsonl</c> none), so a record that holds only its location and path can be decoded (#2137).
  /// </summary>
  /// <param name="relativePath">The object's path from its stream, as its record holds it.</param>
  /// <param name="compression">The codec, when the name is one this store wrote.</param>
  /// <returns>False for a name this store did not write.</returns>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/ArchiveChunkTests.cs:TryReadCompression_ReadsTheCodecFromTheObjectNamesExtensionAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/ArchiveChunkTests.cs:TryReadCompression_RefusesANameThisStoreDidNotWrite_RatherThanGuessingAsync</tests>
  public static bool TryReadCompression(string? relativePath, out ArchiveCompression compression) {
    foreach (var candidate in _codecs) {
      if (relativePath?.EndsWith(_extension(candidate), StringComparison.Ordinal) == true) {
        compression = candidate;
        return true;
      }
    }
    compression = ArchiveCompression.None;
    return false;
  }

  // The codecs whose object names this store writes.
  private static readonly ArchiveCompression[] _codecs = [ArchiveCompression.Brotli, ArchiveCompression.None];

  private static string _extension(ArchiveCompression compression) => compression switch {
    ArchiveCompression.None => ".jsonl",
    ArchiveCompression.Brotli => ".jsonl.br",
    _ => throw _unknown(compression),
  };

  private static void _writeLines(Stream body, IReadOnlyList<ArchiveChunkLine> lines) {
    foreach (var line in lines) {
      body.Write(JsonSerializer.SerializeToUtf8Bytes(line, ArchiveChunkJsonContext.Default.ArchiveChunkLine));
      body.WriteByte(NEW_LINE);
    }
  }

  private static byte[] _decompress(byte[] bytes) {
    using var input = new MemoryStream(bytes, writable: false);
    using var brotli = new BrotliStream(input, CompressionMode.Decompress);
    using var decoded = new MemoryStream();
    brotli.CopyTo(decoded);
    return decoded.ToArray();
  }

  private static ArgumentOutOfRangeException _unknown(ArchiveCompression compression) =>
    new(nameof(compression), compression, "Not an archive codec this store can write or read.");
}
