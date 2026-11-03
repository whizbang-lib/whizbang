// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// What happens to a perspective's row for a stream when that stream is archived whole (#1739, WS-3 S10). Declared on the
/// perspective with <see cref="Attributes.ArchivedStreamRowsAttribute"/>, overridden per perspective from
/// <c>Whizbang:Perspectives:ArchivedStreamRows:&lt;Perspective&gt;</c>. A stream archived through a prefix is still alive,
/// so no policy applies to it.
/// </summary>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/ArchivedStreamRowPoliciesTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRowsSqlTests.cs</tests>
public enum ArchivedStreamRowPolicy {
  /// <summary>The default. The row stays in the perspective's table, readable as it was, while its stream is archived.</summary>
  Keep = 0,

  /// <summary>
  /// The row is deleted with the archive, with the companions a row eviction removes: the cursor of each perspective of
  /// its model (parked by the archive, so the parked cursor goes) and every snapshot but the latest, which a later
  /// restore or new generation rebuilds the row from.
  /// </summary>
  Drop = 1,

  /// <summary>
  /// The row moves beside the archived events (<c>wh_event_archive_rows</c>), out of the perspective's table, and comes
  /// back when the stream is restored or starts a new generation.
  /// </summary>
  Archive = 2,
}
