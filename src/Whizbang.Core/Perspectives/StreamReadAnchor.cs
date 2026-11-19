// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Where a replay's next read of a stream resumes (#2379): after the event last in the stream's order among those it read.
/// </summary>
/// <remarks>
/// <para>
/// A store reads "the events after an event" by that event's position in the stream, its version (#2379). The anchor a
/// catch-up pass hands the next read is therefore the event read with the highest version: any event appended meanwhile
/// sits above it. Taking the highest id instead named an event that can sit anywhere in the stream when ids and versions
/// disagree, and a read after it either served the window again or skipped what lay between.
/// </para>
/// <para>
/// When an event read carries no version the stream order is not known, and the anchor stays the highest id by its
/// canonical text, the order every such store reads in.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rewind#one-order-for-a-stream</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/StreamReadAnchorTests.cs</tests>
public sealed class StreamReadAnchor {
  private Guid? _byVersion;
  private long _version;
  private Guid? _byId;
  private bool _allVersioned = true;

  /// <summary>
  /// The event the next read resumes after: the event of <paramref name="read"/> last in stream order, or
  /// <paramref name="current"/> when nothing was read.
  /// </summary>
  /// <param name="read">The stream's own events the pass read (no collectives, which belong to other streams).</param>
  /// <param name="current">The anchor the pass read after; null for a read from the stream's start.</param>
  /// <returns>The anchor for the next read.</returns>
  /// <docs>fundamentals/perspectives/rewind#one-order-for-a-stream</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/StreamReadAnchorTests.cs:After_EveryEventVersioned_IsTheHighestVersion_NotTheHighestIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/StreamReadAnchorTests.cs:After_AnEventWithNoVersion_IsTheHighestIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/StreamReadAnchorTests.cs:After_NothingRead_KeepsTheCurrentAnchorAsync</tests>
  public static Guid? After(IEnumerable<MessageEnvelope<IEvent>> read, Guid? current) {
    ArgumentNullException.ThrowIfNull(read);
    var anchor = new StreamReadAnchor();
    foreach (var envelope in read) {
      anchor.Offer(envelope);
    }
    return anchor.Next(current);
  }

  /// <summary>Offers one event the pass read, keeping only what the anchor needs, never the event itself.</summary>
  /// <param name="envelope">The event read.</param>
  /// <docs>fundamentals/perspectives/rewind#one-order-for-a-stream</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/StreamReadAnchorTests.cs:After_EveryEventVersioned_IsTheHighestVersion_NotTheHighestIdAsync</tests>
  public void Offer(MessageEnvelope<IEvent> envelope) {
    ArgumentNullException.ThrowIfNull(envelope);
    var id = envelope.MessageId.Value;
    if (_byId is not { } heldId || string.CompareOrdinal(id.ToString("D"), heldId.ToString("D")) > 0) {
      _byId = id;
    }
    if (envelope.LocalStreamVersion is not long version) {
      _allVersioned = false;
    } else if (_byVersion is null || version > _version) {
      _byVersion = id;
      _version = version;
    }
  }

  /// <summary>The anchor for the next read: the event offered last in stream order, or <paramref name="current"/>.</summary>
  /// <param name="current">The anchor the pass read after.</param>
  /// <returns>The anchor for the next read.</returns>
  /// <docs>fundamentals/perspectives/rewind#one-order-for-a-stream</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/StreamReadAnchorTests.cs:After_NothingRead_KeepsTheCurrentAnchorAsync</tests>
  public Guid? Next(Guid? current) {
    if (_byId is null) {
      return current;
    }
    return _allVersioned ? _byVersion : _byId;
  }
}
