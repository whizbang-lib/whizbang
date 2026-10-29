// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Whizbang.Core.Archival;

/// <summary>
/// How an archive's bytes are compressed. Recorded per archive in its <see cref="ArchiveLocator"/>, so a store can
/// change codec without rewriting what it already archived: a reader decodes each archive by its own codec.
/// </summary>
/// <docs>fundamentals/events/archive-store#compression</docs>
public enum ArchiveCompression {
  /// <summary>
  /// Not compressed by the store. The Postgres-table store keeps events as <c>jsonb</c> rows, which Postgres already
  /// compresses (TOAST), so they stay queryable in place.
  /// </summary>
  None = 0,

  /// <summary>Brotli, from the base class library: the codec a byte-stream store (a blob) uses first.</summary>
  Brotli = 1,
}

/// <summary>
/// Where one archive lives and how to read it, written on the stream's tombstone as
/// <c>{store}://{address}?codec={codec}&amp;format={format}</c>; for example
/// <c>postgres-table://svc.wh_event_archive?codec=none&amp;format=1</c>.
/// </summary>
/// <remarks>
/// <para>
/// The store names the <see cref="IArchiveStore"/> that wrote the archive, so a reader can tell an archive the
/// Postgres table holds from one a blob store holds once both have existed in one database. The address is the
/// store's own: a table for the Postgres store, a container and path for a blob store. The codec says how the bytes
/// are compressed and the format says how the events are laid out, each recorded per archive so either can change
/// later without rewriting older archives.
/// </para>
/// <para>
/// The text form is a plain string because the tombstone column is <c>text</c> and every store, in any language,
/// must read it; it is not parsed as a <see cref="Uri"/>, which would normalize a store's address.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/archive-store#locator-format</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/ArchiveLocatorTests.cs</tests>
public sealed record ArchiveLocator {
  /// <summary>The layout of the archived events this release writes and reads.</summary>
  public const int CURRENT_FORMAT = 1;


  /// <summary>Creates a locator.</summary>
  /// <param name="store">The store's name: lowercase letters, digits and hyphens (for example <c>postgres-table</c>).</param>
  /// <param name="address">Where in that store: any text without <c>?</c>.</param>
  /// <param name="compression">How the archive's bytes are compressed.</param>
  /// <param name="format">The layout of the archived events; 1 or more.</param>
  /// <exception cref="ArgumentException">The store or the address cannot be written in the text form.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The codec is not a defined value, or the format is below 1.</exception>
  public ArchiveLocator(string store, string address, ArchiveCompression compression, int format = CURRENT_FORMAT) {
    Store = store;
    Address = address;
    Compression = compression;
    Format = format;
  }


  /// <summary>The store that wrote the archive.</summary>
  public string Store { get; }

  /// <summary>Where in that store.</summary>
  public string Address { get; }

  /// <summary>How the archive's bytes are compressed.</summary>
  public ArchiveCompression Compression { get; }

  /// <summary>The layout of the archived events.</summary>
  public int Format { get; }

  /// <summary>The text written on the tombstone.</summary>
  /// <returns><c>{store}://{address}?codec={codec}&amp;format={format}</c>.</returns>
  public override string ToString() => throw new NotImplementedException();

  /// <summary>Reads a locator's text form.</summary>
  /// <param name="text">The text, as <see cref="ToString"/> wrote it.</param>
  /// <param name="locator">The locator, when the text is one.</param>
  /// <returns>True when the text is a locator this release can read: a known codec and a format of 1 or more.</returns>
  public static bool TryParse(string? text, [NotNullWhen(true)] out ArchiveLocator? locator) => throw new NotImplementedException();
}
