// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// What a rebuild may read of a stream's archived events (#1931). A rebuild re-derives rows, so whether it reaches into
/// the archive is the caller's choice, made per rebuild on <see cref="RebuildOrigin.ArchivedData"/> or
/// <see cref="Commands.System.RebuildPerspectiveCommand.ArchivedData"/>. Rewinds are not affected: a rewind past an
/// archived snapshot always reads through the archive.
/// </summary>
/// <remarks>
/// A stream is <em>alive</em> while it still has live (not archived) events, so only a prefix of it is archived; a stream
/// archived whole is not alive. A stream never archived rebuilds the same way under every choice.
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild#archived-data</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRebuilderArchivedDataTests.cs</tests>
public enum RebuildArchivedData {
  /// <summary>
  /// The default: the rebuild never reads the archive. A stream archived through a prefix starts from its newest live
  /// snapshot and replays the hot events after it; one with no live snapshot is skipped. A stream archived whole is
  /// skipped. A skipped stream is reported in <see cref="RebuildResult.StreamsSkipped"/>, not failed. Reading the archive
  /// costs a restore-sized read per stream and may reach cold storage, so a rebuild does not do it unasked.
  /// </summary>
  Skip = 0,

  /// <summary>
  /// A stream that is alive replays from its start, reading its archived prefix through the archive, as it would have
  /// before the archive. A stream archived whole is still skipped.
  /// </summary>
  IncludeWhileLive = 1,

  /// <summary>
  /// Every stream replays from its start through the archive, a stream archived whole included. The rebuilt cursor of a
  /// stream archived whole would name an event the hot store does not hold, so it is parked with the stream and comes
  /// back when the stream is restored.
  /// </summary>
  IncludeAll = 2,
}

/// <summary>
/// The <see cref="RebuildArchivedData"/> of the rebuild running in the current async flow, held by the rebuilder while it
/// replays, so the readers it calls (the event store's archive seam, the collective replay) honor it. It applies only
/// while <see cref="Messaging.ProcessingModeAccessor.Current"/> is <see cref="Messaging.ProcessingMode.Rebuild"/>.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#archived-data</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRebuilderArchivedDataTests.cs:Rebuild_HoldsItsArchivedDataChoice_AmbientForItsReaders_AndRestoresItAfterwardsAsync</tests>
public static class RebuildArchivedDataAccessor {
  private static readonly AsyncLocal<RebuildArchivedData> _current = new();

  /// <summary>The choice of the rebuild in this async flow; <see cref="RebuildArchivedData.Skip"/> when none set one.</summary>
  public static RebuildArchivedData Current {
    get => _current.Value;
    set => _current.Value = value;
  }
}
