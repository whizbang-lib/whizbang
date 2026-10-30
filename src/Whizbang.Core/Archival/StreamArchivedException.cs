// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// Thrown to a caller that awaits a write (a direct append, or an outbox write flushed by the immediate or scoped
/// strategy) to a stream archived whole. Nothing is stored.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Policy"/> is the <see cref="AppendToArchivedPolicy"/> the stream's type declares, read from the store's
/// SQLSTATE (<c>WHA01</c> for <see cref="AppendToArchivedPolicy.Refuse"/>, <c>WHA02</c> for
/// <see cref="AppendToArchivedPolicy.AutoRestore"/>), never from the message. Under AutoRestore the framework restores the
/// stream and retries the write once before this exception reaches the caller, so a caller sees it under AutoRestore only
/// when the restore did not put the stream back.
/// </para>
/// <para>
/// <see cref="Exception.Message"/> is the store's own wording, the same text a dead-lettered refusal records:
/// <c>Stream {id} is archived through version {n} and refuses appends (AppendToArchived Refuse).</c>
/// </para>
/// </remarks>
/// <param name="message">The store's wording of the refusal.</param>
/// <param name="streamId">The archived stream, or <see cref="Guid.Empty"/> when the wording did not carry it.</param>
/// <param name="archivedThroughVersion">The last version the archive holds, or null when the wording did not carry it.</param>
/// <param name="policy">The policy the stream's type declares.</param>
/// <param name="innerException">The store's refusal.</param>
/// <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamArchivedRefusalTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:DirectAppend_Refuse_ThrowsStreamArchivedException_AndStoresNothingAsync</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
  Justification = "The exception is only meaningful with the stream, its archived version and the policy; the standard constructors would produce one without them.")]
public sealed class StreamArchivedException(
  string message, Guid streamId, long? archivedThroughVersion, AppendToArchivedPolicy policy, Exception? innerException = null)
    : Exception(message, innerException) {

  /// <summary>The archived stream.</summary>
  public Guid StreamId { get; } = streamId;

  /// <summary>The last version the stream's archive holds (its tombstone's version floor).</summary>
  public long? ArchivedThroughVersion { get; } = archivedThroughVersion;

  /// <summary>What the stream's type declares an append to it does.</summary>
  public AppendToArchivedPolicy Policy { get; } = policy;
}
