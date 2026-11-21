// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Registry;

/// <summary>
/// One message type's stream id, as the source generator of the assembly that compiles the type resolved it: the
/// effective <c>[StreamId]</c> property the type exposes (its own, inherited or an override), how to read and write it,
/// the type of id it holds, and the <c>[GenerateStreamId]</c> policy that applies to the type (#1905, #1908).
/// </summary>
/// <remarks>
/// <para>
/// An entry describes exactly one type, never the types derived from it: a derived type the generator saw has an entry
/// of its own, and one it did not see (a runtime subclass, or a type compiled without the generator) is answered by its
/// nearest registered base type through <see cref="StreamIdTypeRegistry.Resolve"/>.
/// </para>
/// <para>
/// The accessors are generated delegates, so reading and writing a stream id needs no reflection and is AOT-safe.
/// </para>
/// </remarks>
/// <docs>extending/attributes/streamid#how-a-message-type-finds-its-stream-id</docs>
/// <tests>tests/Whizbang.Core.Tests/Registry/StreamIdTypeRegistryTests.cs</tests>
public sealed class StreamIdTypeEntry {
  private readonly Func<object, Guid?> _extract;
  private readonly Func<object, Guid, Type?, bool>? _set;

  /// <summary>Creates the entry for <paramref name="messageType"/>.</summary>
  /// <param name="messageType">The exact message type the entry describes.</param>
  /// <param name="extract">Reads the stream id of an instance of <paramref name="messageType"/>; null when it has
  /// none or it is not a Guid.</param>
  /// <param name="streamIdType">The strong-id type of the stream id, or <see cref="Guid"/> for one that is not a strong
  /// id (#2072).</param>
  /// <param name="shouldGenerate">Whether the dispatcher mints a stream id for the type (<c>[GenerateStreamId]</c>, as
  /// #1893's rule applies it to this type).</param>
  /// <param name="onlyIfEmpty">Whether a minted stream id is written only over an empty one.</param>
  /// <param name="set">Writes a stream id the type inherits from a source of the given stream-id type, returning
  /// whether it was written; null when the stream id cannot be written (init-only, read-only, or not a Guid or a
  /// strong id with a <c>From</c>).</param>
  public StreamIdTypeEntry(
      Type messageType,
      Func<object, Guid?> extract,
      Type streamIdType,
      bool shouldGenerate = false,
      bool onlyIfEmpty = false,
      Func<object, Guid, Type?, bool>? set = null) {
    ArgumentNullException.ThrowIfNull(messageType);
    ArgumentNullException.ThrowIfNull(extract);
    ArgumentNullException.ThrowIfNull(streamIdType);
    MessageType = messageType;
    StreamIdType = streamIdType;
    ShouldGenerate = shouldGenerate;
    OnlyIfEmpty = onlyIfEmpty;
    _extract = extract;
    _set = set;
  }

  /// <summary>The exact message type the entry describes.</summary>
  public Type MessageType { get; }

  /// <summary>The strong-id type of the stream id, or <see cref="Guid"/> for one that is not a strong id.</summary>
  public Type StreamIdType { get; }

  /// <summary>Whether the dispatcher mints a stream id for the type.</summary>
  public bool ShouldGenerate { get; }

  /// <summary>Whether a minted stream id is written only over an empty one.</summary>
  public bool OnlyIfEmpty { get; }

  /// <summary>Whether the stream id can be written.</summary>
  public bool IsSettable => _set is not null;

  /// <summary>Reads the stream id of <paramref name="message"/>.</summary>
  /// <param name="message">An instance of <see cref="MessageType"/> or of a type derived from it.</param>
  /// <returns>The stream id, or null when the message has none or it is not a Guid.</returns>
  public Guid? Extract(object message) => _extract(message);

  /// <summary>Writes a stream id <paramref name="message"/> inherits from a source of another message.</summary>
  /// <param name="message">An instance of <see cref="MessageType"/> or of a type derived from it.</param>
  /// <param name="streamId">The stream id to write.</param>
  /// <param name="sourceStreamIdType">The source's stream-id type; null or <see cref="Guid"/> for a plain Guid.</param>
  /// <returns>True when the stream id was written; false when it cannot be, or the source's type is refused.</returns>
  public bool Set(object message, Guid streamId, Type? sourceStreamIdType) =>
    _set is not null && _set(message, streamId, sourceStreamIdType);
}
