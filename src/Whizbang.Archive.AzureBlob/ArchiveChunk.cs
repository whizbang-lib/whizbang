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
  /// Reads an object's locator (<c>azure-blob://{container}/{name}?codec=..&amp;format=..</c>) into the container, the
  /// object name and the codec.
  /// </summary>
  /// <returns>False when the text is not a locator this store wrote: another store, an unknown codec or format, or an
  /// address without a container and a name.</returns>
  public static bool TryReadObjectLocator(string? text, out string container, out string name, out ArchiveCompression compression) {
    container = string.Empty;
    name = string.Empty;
    compression = ArchiveCompression.None;
    if (!ArchiveLocator.TryParse(text, out var locator)
        || !string.Equals(locator.Store, AzureBlobArchiveStore.STORE_NAME, StringComparison.Ordinal)
        || locator.Format != ArchiveLocator.CURRENT_FORMAT) {
      return false;
    }
    var slash = locator.Address.IndexOf('/', StringComparison.Ordinal);
    if (slash <= 0 || slash == locator.Address.Length - 1) {
      return false;
    }
    container = locator.Address[..slash];
    name = locator.Address[(slash + 1)..];
    compression = locator.Compression;
    return true;
  }

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
