// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// The statuses an <see cref="IArchiveStore"/> reports. A refused prefix batch reports the prefix truncate's own
/// refusal (<c>blocked</c>, <c>no_carry_forward</c>, <c>held</c>, <c>cursor_behind</c>, <c>debug_skipped</c>), so a
/// caller reads one vocabulary whether it truncates or archives.
/// </summary>
/// <docs>fundamentals/events/archive-store#statuses</docs>
public static class ArchiveStatus {
  /// <summary>The batch was copied to the archive and removed from the hot store.</summary>
  public const string ARCHIVED = "archived";

  /// <summary>The batch named no hot event (an already archived stream, or a prefix that is already gone).</summary>
  public const string NOTHING_TO_ARCHIVE = "nothing_to_archive";

  /// <summary>A perspective has not processed an event the batch covers.</summary>
  public const string BLOCKED = "blocked";

  /// <summary>An active destruction hold covers an event the batch covers.</summary>
  public const string HELD = "held";

  /// <summary><c>debug_mode</c> keeps every event hot for forensics.</summary>
  public const string DEBUG_SKIPPED = "debug_skipped";

  /// <summary>
  /// A prefix through a version below 1 (#1738). A prefix always removes version 1, so an archived stream always shows a
  /// gap at its start and a read from the start sees it without asking the archive. Nothing moved.
  /// </summary>
  public const string NOT_PAST_FIRST_VERSION = "not_past_first_version";

  /// <summary>
  /// A prefix that reaches the newest snapshot a perspective holds for the stream (#1738). The snapshot's event and
  /// everything after it stay hot, so a rewind to a perspective's latest snapshot never needs the archive. Refused, not
  /// shortened: nothing moved, and the caller asks again below the snapshot.
  /// </summary>
  public const string SNAPSHOT_IN_RANGE = "snapshot_in_range";

  /// <summary>The archived events are back in the hot store and the stream is active again.</summary>
  public const string RESTORED = "restored";

  /// <summary>Nothing of the stream is archived.</summary>
  public const string NOTHING_ARCHIVED = "nothing_archived";

  /// <summary>
  /// A restore found a hot event at a version an archived event holds, so putting the archive back would give the
  /// stream two events at one version. Nothing is restored.
  /// </summary>
  public const string VERSION_CONFLICT = "version_conflict";
}

/// <summary>The outcome of <see cref="IArchiveStore.WriteAsync"/>.</summary>
/// <param name="Status">An <see cref="ArchiveStatus"/> value, or a prefix truncate refusal.</param>
/// <param name="EventsArchived">How many events moved (0 unless <see cref="ArchiveStatus.ARCHIVED"/>).</param>
/// <param name="LastVersion">The stream's last version when a whole-stream batch archived it (the tombstone's
/// version floor); otherwise <see langword="null"/>.</param>
/// <param name="Locator">Where the archived events are, when they moved.</param>
/// <param name="OriginGeneration">The origin generation after the write (bumped once by every archive).</param>
/// <docs>fundamentals/events/archive-store#writing-an-archive</docs>
public sealed record ArchiveWriteResult(
  string Status, long EventsArchived, long? LastVersion, ArchiveLocator? Locator, long OriginGeneration) {
  /// <summary>True when the batch moved.</summary>
  public bool Archived => string.Equals(Status, ArchiveStatus.ARCHIVED, StringComparison.Ordinal);
}

/// <summary>The outcome of <see cref="IArchiveStore.RestoreAsync"/>.</summary>
/// <param name="Status"><see cref="ArchiveStatus.RESTORED"/>, <see cref="ArchiveStatus.NOTHING_ARCHIVED"/> or
/// <see cref="ArchiveStatus.VERSION_CONFLICT"/>.</param>
/// <param name="EventsRestored">How many events went back into the hot store.</param>
/// <param name="OriginGeneration">The origin generation after the restore (bumped once by a restore that moved events).</param>
/// <docs>fundamentals/events/archive-store#restoring-an-archive</docs>
public sealed record ArchiveRestoreResult(string Status, long EventsRestored, long OriginGeneration);
