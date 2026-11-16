// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The other declared-ephemeral event types one type's history walk converts with it (#2278): their stored names, and
/// their own released and allowed types as rules, so a stream whose Sourced events are all of declared types (or
/// released or allowed for one of them) converts whole in the walk that reaches it first.
/// </summary>
/// <param name="Names">Every stored name of every other declared-ephemeral type, ordinal order, no duplicates.</param>
/// <param name="RuleOwner">For each rule, the stored name of the declared type it belongs to.</param>
/// <param name="RuleKind">For each rule, <c>released</c> or <c>allowed</c>.</param>
/// <param name="RuleType">For each rule, the released or allowed type's stored name.</param>
/// <docs>fundamentals/events/ephemeral-events#a-stream-becomes-ephemeral-whole</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs</tests>
internal sealed record EphemeralStreamMates(string[] Names, string[] RuleOwner, string[] RuleKind, string[] RuleType) {
  /// <summary>The rule kind of a type released for a declared type.</summary>
  internal const string RELEASED = "released";

  /// <summary>The rule kind of a Sourced type allowed to share a stream with a declared type.</summary>
  internal const string ALLOWED = "allowed";

  /// <summary>No other declared type: the walk judges streams as it did before #2278.</summary>
  internal static readonly EphemeralStreamMates None = new([], [], [], []);

  /// <summary>The other declared types, from each one's stored names, release list and allowed Sourced types.</summary>
  /// <param name="others">Every declared-ephemeral type but the walked one.</param>
  /// <param name="own">The walked type's stored names: never one of its mates.</param>
  /// <returns>The mates; <see cref="None"/> when there are none.</returns>
  public static EphemeralStreamMates From(IEnumerable<(string[] Names, string[] Released, string[] Allowed)> others, string[] own) {
    var names = new SortedSet<string>(StringComparer.Ordinal);
    var rules = new SortedSet<(string Owner, string Kind, string Type)>();
    foreach (var (otherNames, released, allowed) in others) {
      foreach (var name in otherNames.Where(n => !own.Contains(n, StringComparer.Ordinal))) {
        _ = names.Add(name);
        rules.UnionWith(released.Select(r => (name, RELEASED, r)));
        rules.UnionWith(allowed.Select(a => (name, ALLOWED, a)));
      }
    }
    return names.Count == 0
      ? None
      : new EphemeralStreamMates([.. names], [.. rules.Select(r => r.Owner)], [.. rules.Select(r => r.Kind)], [.. rules.Select(r => r.Type)]);
  }

  /// <summary>
  /// A short, order-independent digest of the mates and their rules, for the walk's variant: the set grows with the
  /// catalog, so the variant carries its digest rather than every name, and any change to it walks the type again.
  /// </summary>
  /// <returns>Sixteen lowercase hex characters; null when there are no mates.</returns>
  public string? Fingerprint() {
    if (Names.Length == 0) {
      return null;
    }
    var text = new StringBuilder();
    foreach (var name in Names) {
      text.Append(name).Append('\n');
    }
    for (var i = 0; i < RuleOwner.Length; i++) {
      text.Append(RuleOwner[i]).Append('\t').Append(RuleKind[i]).Append('\t').Append(RuleType[i]).Append('\n');
    }
    return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
  }
}
