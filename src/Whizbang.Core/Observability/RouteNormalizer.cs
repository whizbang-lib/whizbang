// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Core.Observability;

/// <summary>
/// The route id of an event: a hash of its type and its hops reduced to their route form, the same value the write paths
/// stamp on the row in SQL (<c>_wh_route_id</c>, migration 285), so a route found here and one found in the store are
/// the same route (#1725).
/// </summary>
/// <remarks>
/// <para>
/// The route form keeps, per hop and in order: the hop type, the service name (never the instance), the topic, the
/// execution strategy, the caller's class (its file's name, without folders or extension) and member (never the line),
/// and each policy decision's policy, rule and outcome. It drops everything that varies by message: ids, timestamps,
/// durations, partition, sequence, trace parent, scope and the hop metadata dictionary. The event's type is its
/// namespace and type name, the part of the stored event type before its first comma.
/// </para>
/// <para>
/// The canonical text joins the type and each hop with U+001E, a hop's fields with U+001F, a decision's fields with
/// U+001D and the decisions with U+001C; a missing value is empty. The id is the first sixteen bytes of the text's
/// SHA-256, read as a uuid. A test runs one corpus through both implementations and requires the same id.
/// </para>
/// </remarks>
/// <docs>data/event-store#routes-and-traces</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/RouteNormalizerTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRouteCorpusTests.cs:EveryCorpusEntry_HasTheSameRouteId_InSqlAndInCSharpAsync</tests>
public static class RouteNormalizer {
  private const char HOP_SEPARATOR = '\u001E';
  private const char FIELD_SEPARATOR = '\u001F';
  private const char DECISION_FIELD_SEPARATOR = '\u001D';
  private const char DECISION_SEPARATOR = '\u001C';

  /// <summary>The canonical text the route id hashes.</summary>
  /// <param name="eventType">The event type, assembly-qualified or not; only the part before the first comma counts.</param>
  /// <param name="hops">The event's hops, in order; null is no hops.</param>
  /// <returns>The canonical text.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Observability/RouteNormalizerTests.cs:CanonicalText_KeepsTheRouteFields_InOrder_AndNothingElseAsync</tests>
  public static string CanonicalText(string eventType, IReadOnlyList<MessageHop>? hops) {
    ArgumentNullException.ThrowIfNull(eventType);
    var text = new StringBuilder(TypeNamePart(eventType));
    foreach (var hop in hops ?? []) {
      text.Append(HOP_SEPARATOR);
      _appendHop(text, hop);
    }
    return text.ToString();
  }

  /// <summary>The route id of an event of <paramref name="eventType"/> that took <paramref name="hops"/>.</summary>
  /// <param name="eventType">The event type, assembly-qualified or not.</param>
  /// <param name="hops">The event's hops, in order; null is no hops.</param>
  /// <returns>The route id.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Observability/RouteNormalizerTests.cs:RouteId_IsTheFirstSixteenBytesOfTheSha256_AsAUuidAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Observability/RouteNormalizerTests.cs:RouteId_IsStable_AcrossInstancesLinesAndPerMessageValuesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Observability/RouteNormalizerTests.cs:RouteId_Differs_WhenAServiceMemberTopicPolicyOrTypeDiffersAsync</tests>
  public static Guid RouteId(string eventType, IReadOnlyList<MessageHop>? hops) {
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalText(eventType, hops)));
    return new Guid(hash.AsSpan(0, 16), bigEndian: true);
  }

  /// <summary>The part of an event type name the route keys on: the type name without its assembly
  /// (<see cref="TypeNameFormatter.GetFullName"/>), as SQL's <c>split_part(event_type, ',', 1)</c> takes it from a stored
  /// event type.</summary>
  /// <param name="eventType">The event type.</param>
  /// <returns>The type name without its assembly.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Observability/RouteNormalizerTests.cs:TypeNamePart_IsEverythingBeforeTheFirstCommaAsync</tests>
  public static string TypeNamePart(string eventType) {
    ArgumentNullException.ThrowIfNull(eventType);
    return TypeNameFormatter.GetFullName(eventType);
  }

  /// <summary>
  /// The class a caller file names: the file's name without its folders (either separator) or its extension, as SQL's
  /// two <c>regexp_replace</c> calls take it.
  /// </summary>
  /// <param name="callerFilePath">The caller's file path, or null.</param>
  /// <returns>The class part, empty for no path.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Observability/RouteNormalizerTests.cs:CallerClass_IsTheFileNameWithoutFoldersOrExtensionAsync</tests>
  public static string CallerClass(string? callerFilePath) {
    if (string.IsNullOrEmpty(callerFilePath)) {
      return string.Empty;
    }
    var name = callerFilePath[(callerFilePath.LastIndexOfAny(['/', '\\']) + 1)..];
    var dot = name.LastIndexOf('.');
    return dot < 0 ? name : name[..dot];
  }

  private static void _appendHop(StringBuilder text, MessageHop hop) {
    // The hop writer leaves the unknown instance out, so the SQL sees no service name for it.
    var service = hop.ServiceInstance != ServiceInstanceInfo.Unknown ? hop.ServiceInstance.ServiceName : string.Empty;
    text.Append(((int)hop.Type).ToString(CultureInfo.InvariantCulture)).Append(FIELD_SEPARATOR)
      .Append(service).Append(FIELD_SEPARATOR)
      .Append(hop.Topic).Append(FIELD_SEPARATOR)
      .Append(hop.ExecutionStrategy).Append(FIELD_SEPARATOR)
      .Append(CallerClass(hop.CallerFilePath)).Append(FIELD_SEPARATOR)
      .Append(hop.CallerMemberName).Append(FIELD_SEPARATOR);
    var first = true;
    foreach (var decision in hop.Trail?.Decisions ?? []) {
      if (!first) {
        text.Append(DECISION_SEPARATOR);
      }
      first = false;
      text.Append(decision.PolicyName).Append(DECISION_FIELD_SEPARATOR)
        .Append(decision.Rule).Append(DECISION_FIELD_SEPARATOR)
        .Append(decision.Matched ? '1' : '0');
    }
  }
}
