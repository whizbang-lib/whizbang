// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// What the startup associations step writes so the event store creates <c>__collective__</c> rows only for the
/// collective event types a handler on this service applies (#2145): one <see cref="ASSOCIATION_TYPE"/> association
/// per handled type and one <see cref="GATE_ASSOCIATION_TYPE"/> marker per service, beside the perspective
/// associations, in the same <c>register_message_associations</c> call.
/// </summary>
/// <remarks>
/// <para>
/// Why a marker: the SQL that creates the rows is shared by every instance of the schema, but only an instance of this
/// version writes collective associations. The store skips a type nobody handles only when every service with
/// associations in the schema has written its marker, so an older instance in a rolling deploy (whose registration
/// removes the marker as an orphan) and a service that cannot say what it handles keep every row being created.
/// </para>
/// <para>
/// Why the hash covers the set: the handled set is read at run time from the consumer registry, which a handler added
/// or removed changes without changing any table, migration or perspective. Without the set in the step's hash a start
/// with new handlers would skip the step and the store would keep gating on the old set.
/// </para>
/// <para>
/// A set that cannot be known (<see langword="null"/>) writes nothing, which turns the gate off: a gap in what the gate
/// knows may create rows, never drop them.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events#only-handled-types-are-queued</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveRoutingAssociationsTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveAssociationRegistrationTests.cs</tests>
public static class CollectiveRoutingAssociations {
  /// <summary>The association type of a collective event type a handler here applies.</summary>
  public const string ASSOCIATION_TYPE = "collective";

  /// <summary>The association type of the marker that says this service's registration gates collective rows.</summary>
  public const string GATE_ASSOCIATION_TYPE = "collective_gate";

  /// <summary>
  /// The JSON objects to append to the perspective associations array: each preceded by a comma, one
  /// <see cref="ASSOCIATION_TYPE"/> entry per handled type and then the <see cref="GATE_ASSOCIATION_TYPE"/> marker.
  /// Empty when <paramref name="handledEventTypes"/> is <see langword="null"/>.
  /// </summary>
  /// <param name="handledEventTypes">The handled collective event types, or null when they cannot be known.</param>
  /// <param name="serviceName">The service the rows belong to, as the perspective associations name it.</param>
  /// <returns>The entries, ready to follow at least one entry already in the array.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveRoutingAssociationsTests.cs:Entries_ForAKnownSet_AreOneCollectiveAssociationPerType_AndTheGateMarkerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveRoutingAssociationsTests.cs:Entries_ForAnUnknownSet_AreNothing_SoTheStoreKeepsCreatingEveryRowAsync</tests>
  public static string AssociationEntriesJson(IReadOnlyList<string>? handledEventTypes, string serviceName) {
    if (handledEventTypes is null) {
      return string.Empty;
    }
    var json = new StringBuilder();
    foreach (var type in _canonical(handledEventTypes)) {
      _appendEntry(json, type, ASSOCIATION_TYPE, serviceName);
    }
    _appendEntry(json, CollectiveRouting.SINK_PERSPECTIVE_NAME, GATE_ASSOCIATION_TYPE, serviceName);
    return json.ToString();
  }

  /// <summary>
  /// The associations step's hash: a SHA-256 over the perspective associations' compile-time hash and the handled
  /// collective set. Empty when <paramref name="perspectiveAssociationsHash"/> is (a context with no perspectives has no
  /// associations step).
  /// </summary>
  /// <param name="perspectiveAssociationsHash">The generated hash of the perspective associations.</param>
  /// <param name="handledEventTypes">The handled collective event types, or null when they cannot be known.</param>
  /// <returns>The hash the step records and compares.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveRoutingAssociationsTests.cs:Hash_ChangesWhenTheHandledSetChanges_AndOnlyThenAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveRoutingAssociationsTests.cs:Hash_TellsUnknownFromEmpty_AndFromThePerspectiveSetAloneAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveRoutingAssociationsTests.cs:Hash_ForAContextWithNoPerspectives_StaysEmpty_SoItHasNoAssociationsStepAsync</tests>
  public static string AssociationsHash(string perspectiveAssociationsHash, IReadOnlyList<string>? handledEventTypes) {
    ArgumentNullException.ThrowIfNull(perspectiveAssociationsHash);
    if (perspectiveAssociationsHash.Length == 0) {
      return string.Empty;
    }
    // One SHA-256, the width of every hash the migration ledger records (content_hash is VARCHAR(64)). "unknown" cannot
    // be a type name, so an unknown set never hashes like a known one.
    var collective = handledEventTypes is null ? "unknown" : "known\n" + string.Join('\n', _canonical(handledEventTypes));
    var digest = SHA256.HashData(Encoding.UTF8.GetBytes(perspectiveAssociationsHash + "\ncollective:" + collective));
    return Convert.ToHexStringLower(digest);
  }

  private static IEnumerable<string> _canonical(IReadOnlyList<string> types) =>
    types.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

  private static void _appendEntry(StringBuilder json, string messageType, string associationType, string serviceName) {
    json.Append(CultureInfo.InvariantCulture,
      $",{{\"MessageType\":\"{JsonEncodedText.Encode(messageType)}\",\"AssociationType\":\"{associationType}\","
      + $"\"TargetName\":\"{CollectiveRouting.SINK_PERSPECTIVE_NAME}\",\"ServiceName\":\"{JsonEncodedText.Encode(serviceName)}\"}}");
  }
}
