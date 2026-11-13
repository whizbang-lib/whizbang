// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// What the startup associations step registers for a perspective that keeps no rewind floor (#2295): one
/// <see cref="NO_FLOOR_ASSOCIATION_TYPE"/> marker per perspective, beside its perspective associations, in the same
/// <c>register_message_associations</c> call. The ephemeral body reaper reads the marker and reaps the perspective's
/// bodies without waiting for a snapshot, and the reap-driven snapshot pass skips it.
/// </summary>
/// <remarks>
/// A perspective keeps no rewind floor when it applies only ephemeral events and is not marked
/// <see cref="Attributes.KeepEphemeralRewindFloorAttribute"/>; the generators decide it at compile time. The marker is
/// an association row rather than a column so that its absence is the safe default: a database no startup of this
/// version has registered, a service on another driver, and an older instance in a rolling deploy (whose registration
/// removes the marker as an orphan) all keep the coverage gate, which holds bodies rather than losing them.
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#snapshots-follow-what-a-read-model-consumes</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/EphemeralRewindFloorTests.cs</tests>
public static class EphemeralRewindFloor {
  /// <summary>
  /// The association type of the marker. The migrations spell it <c>__ASSOCIATION_NO_REWIND_FLOOR__</c>
  /// (<c>Migrations/constants.txt</c>), and a test holds the two equal.
  /// </summary>
  public const string NO_FLOOR_ASSOCIATION_TYPE = "no_rewind_floor";

  /// <summary>
  /// The marker's JSON object, preceded by a comma, to append to the perspective associations array.
  /// </summary>
  /// <param name="perspectiveName">The perspective, in the CLR form its cursors and associations name it.</param>
  /// <param name="serviceName">The service the associations belong to.</param>
  /// <returns>The entry, ready to follow at least one entry already in the array.</returns>
#pragma warning disable RCS1163, IDE0060 // RED stub
  public static string NoFloorAssociationEntryJson(string perspectiveName, string serviceName) => string.Empty;
#pragma warning restore RCS1163, IDE0060
}
