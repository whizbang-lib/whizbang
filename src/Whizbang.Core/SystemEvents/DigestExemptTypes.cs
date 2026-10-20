// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// The event types no integrity digest ever folds: the audit system events.
/// </summary>
/// <remarks>
/// <para>
/// An audit record is a function of the event it audits, re-derivable from it, so it needs no integrity
/// accounting of its own. It also cannot be accounted for: the origin publishes its audit records without
/// storing them, while a subscriber stores what it receives under the origin's id, so the two sides could
/// never agree on them (#1492). Every fold, in SQL and in the coordinator's recomputes, leaves these types
/// out, matched on the type-name part of the stored event type.
/// </para>
/// <para>
/// The same list is the <c>__DIGEST_EXEMPT_TYPES__</c> migration constant; the two must name the same types.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity#what-is-digested</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/DigestExemptTypesTests.cs</tests>
public static class DigestExemptTypes {
  /// <summary>The full type names (namespace and type, no assembly) of the event types left out of every digest.</summary>
  public static IReadOnlyList<string> TypeNames { get; } = [
    TypeNameFormatter.FormatClrTypeName(typeof(EventAudited)),
    TypeNameFormatter.FormatClrTypeName(typeof(CommandAudited)),
  ];
}
