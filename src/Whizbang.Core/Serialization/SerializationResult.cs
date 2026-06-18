using System;

namespace Whizbang.Core.Serialization;

/// <summary>
/// Rich result of a serialize operation: the serialized bytes plus metadata derived from the same
/// single serialization pass. Callers use <see cref="SizeBytes"/> to decide the message body path
/// (inline vs offload) without re-serializing. Forward-extensible — add fields here as more
/// serialize-time information is needed, rather than re-deriving it downstream.
/// </summary>
/// <docs>event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/WireEnvelopeSerializerTests.cs</tests>
public sealed record SerializationResult {
  /// <summary>The serialized bytes.</summary>
  public required ReadOnlyMemory<byte> Data { get; init; }

  /// <summary>MIME type of <see cref="Data"/>. Defaults to <c>application/json</c>.</summary>
  public string ContentType { get; init; } = "application/json";

  /// <summary>Serialized size in bytes — derived from <see cref="Data"/>, the input to the
  /// body-path (inline vs offload) decision.</summary>
  public int SizeBytes => Data.Length;

  /// <summary>The serialization-logic version that produced this result
  /// (see <see cref="SerializationVersion.CURRENT"/>).</summary>
  public int Version { get; init; } = SerializationVersion.CURRENT;
}
