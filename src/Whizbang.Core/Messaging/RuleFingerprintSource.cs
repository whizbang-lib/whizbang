// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.SystemEvents;
using Whizbang.Rules;

namespace Whizbang.Core.Messaging;

/// <summary>
/// This service's eligibility rules for the event types an integrity message is about (#2362). Every integrity message
/// carries the rules for its types, and every receiver compares them with its own through the same source.
/// </summary>
/// <docs>resilience/stream-integrity#rule-fingerprint</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/CatalogRuleFingerprintSourceTests.cs</tests>
public interface IRuleFingerprintSource {
  /// <summary>
  /// The rules for <paramref name="eventTypes"/>: both versions, and the rule hash of each named type this service knows.
  /// A type this service does not know is left out, so it is not shared. Null names every type this service knows.
  /// </summary>
  /// <param name="eventTypes">The event types, in any form (with or without the assembly); null for all.</param>
  /// <returns>The rules.</returns>
  RuleSet RulesFor(IEnumerable<string>? eventTypes);
}

/// <summary>
/// The rules the generated message-type catalog declares (#2362): per event type its ephemeral mode, superseding group
/// and key, terminal flag and digest exemption, plus the end-of-life action configuration gives the streams it opens.
/// Built once, on first use.
/// </summary>
/// <docs>resilience/stream-integrity#rule-fingerprint</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/CatalogRuleFingerprintSourceTests.cs</tests>
public sealed class CatalogRuleFingerprintSource : IRuleFingerprintSource {
  private const string EVENT_KIND = "event";

  private readonly Lazy<IReadOnlyDictionary<string, string>> _hashes;

  /// <summary>A source over the catalog, the stream lifecycle settings and the retired event types.</summary>
  /// <param name="catalog">The message-type catalog.</param>
  /// <param name="lifecycle">
  /// The stream lifecycle settings. Required: an end-of-life action is part of a type's rules, so a source built without
  /// them would fingerprint every type as having none and halt a pair whose peer read its settings (WHIZ501).
  /// </param>
  /// <param name="retired">
  /// The retired event types (#2329), or null for none. A retired type is no longer in the catalog, so its rule comes
  /// from here; the turnkey registration always passes it.
  /// </param>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/CatalogRuleFingerprintSourceTests.cs:For_CoversEachRetiredName_AsRetired_ThoughTheCatalogNoLongerDeclaresItAsync</tests>
  public CatalogRuleFingerprintSource(
      IMessageTypeCatalog catalog, IOptions<StreamLifecycleOptions> lifecycle, IOptions<RetiredEventTypesOptions>? retired = null) {
    ArgumentNullException.ThrowIfNull(catalog);
    ArgumentNullException.ThrowIfNull(lifecycle);
    _hashes = new(() => RulesOf(catalog, lifecycle.Value, retired?.Value)
        .ToDictionary(r => r.Key, r => r.Value.Hash(), StringComparer.Ordinal),
      LazyThreadSafetyMode.ExecutionAndPublication);
  }

  /// <summary>The source for a host without a catalog: it knows no type, so it shares none and compares only versions.</summary>
  public static IRuleFingerprintSource Empty { get; } =
    new CatalogRuleFingerprintSource(NullMessageTypeCatalog.Instance, Options.Create(new StreamLifecycleOptions()));

  /// <inheritdoc />
  public RuleSet RulesFor(IEnumerable<string>? eventTypes) {
    var known = _hashes.Value;
    var covered = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var name in eventTypes ?? known.Keys) {
      var bare = RuleTypeName.Of(name);
      if (known.TryGetValue(bare, out var hash)) {
        covered[bare] = hash;
      }
    }
    return new RuleSet(RuleSemantics.VERSION, RuleSemantics.ELIGIBILITY_SQL_VERSION, covered);
  }

  /// <summary>Every event type's rules, by its name without the assembly.</summary>
  /// <param name="catalog">The catalog.</param>
  /// <param name="lifecycle">The stream lifecycle settings, or null.</param>
  /// <param name="retired">The retired event types (#2329), or null for none: each is covered as retired.</param>
  /// <returns>The rules of each event type.</returns>
  public static IReadOnlyDictionary<string, TypeRules> RulesOf(
      IMessageTypeCatalog catalog, StreamLifecycleOptions? lifecycle, RetiredEventTypesOptions? retired = null) {
    ArgumentNullException.ThrowIfNull(catalog);
    // not the digest rule: the per-type DigestExempt field a fingerprint carries, read from the one exempt list
    var exempt = new HashSet<string>(DigestExemptTypes.TypeNames, StringComparer.Ordinal);
    var rules = new Dictionary<string, TypeRules>(StringComparer.Ordinal);
    foreach (var entry in catalog.GetAll().Where(e => e.Kind == EVENT_KIND)) {
      var name = entry.ClrTypeName;
      rules[name] = new TypeRules(name) {
        Ephemeral = entry.Ephemeral is { } e ? $"{e.Destruction};{e.Storage}" : null,
        Superseding = entry.IsSuperseding,
        SupersedingGroup = SupersedingEvents.GroupOf(entry.Type),
        SupersedingKey = SupersedingEvents.KeyMemberOf(entry.Type),
        Terminal = entry.IsTerminal,
        EndOfLife = lifecycle is not null && lifecycle.Types.TryGetValue(name, out var type) ? type.Action.ToString() : null,
        DigestExempt = exempt.Contains(name),
      };
    }
    // #2329: a retired type's class is deleted, so the catalog no longer has it. A name the catalog still declares is not
    // retired here and keeps its declared rules (owner decision, 2026-11-20).
    foreach (var name in retired?.EffectiveNames(catalog) ?? []) {
      rules[name] = new TypeRules(name) { Retired = true };
    }
    return rules;
  }
}

/// <summary>The form a rule fingerprint keys types by (#2362): the type's name without its assembly.</summary>
/// <docs>resilience/stream-integrity#rule-fingerprint</docs>
public static class RuleTypeName {
  /// <summary>The name of <paramref name="eventType"/> without its assembly or a <c>global::</c> prefix.</summary>
  /// <param name="eventType">A stored, wire or bare type name.</param>
  /// <returns>The bare name.</returns>
  public static string Of(string eventType) => TypeNameFormatter.GetFullName(eventType).Trim();
}
