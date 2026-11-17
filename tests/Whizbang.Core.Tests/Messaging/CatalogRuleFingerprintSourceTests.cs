// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.SystemEvents;
using Whizbang.Rules;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2362: the catalog-backed rule source reads each event type's declared rules from the generated catalog (and the end
/// of life its streams are given), keys them by the type's name without the assembly, and covers only the types asked
/// for that it knows.
/// </summary>
[Category("Messaging")]
public class CatalogRuleFingerprintSourceTests {
  public sealed record Placed([property: StreamId] Guid Id) : IEvent;
  public sealed record Pinged([property: StreamId] Guid Id) : IEvent;
  public sealed record Ship : ICommand;

  private static readonly string _placed = TypeNameFormatter.FormatClrTypeName(typeof(Placed));
  private static readonly string _pinged = TypeNameFormatter.FormatClrTypeName(typeof(Pinged));

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private static MessageTypeCatalogEntry _event(Type type) =>
    new(type, TypeNameFormatter.FormatClrTypeName(type), "event", null);

  [Test]
  public async Task For_CoversTheNamedTypesItKnows_InAnyForm_AndLeavesOutTheRestAsync() {
    var source = new CatalogRuleFingerprintSource(new Catalog(_event(typeof(Placed)), _event(typeof(Pinged)),
      new MessageTypeCatalogEntry(typeof(Ship), TypeNameFormatter.FormatClrTypeName(typeof(Ship)), "command", null)),
      Options.Create(new StreamLifecycleOptions()));

    var rules = source.RulesFor([TypeNameFormatter.Format(typeof(Placed)), "Contracts.Unknown, Contracts"]);

    await Assert.That(rules.SemanticsVersion).IsEqualTo(RuleSemantics.VERSION);
    await Assert.That(rules.EligibilitySqlVersion).IsEqualTo(RuleSemantics.ELIGIBILITY_SQL_VERSION);
    await Assert.That(rules.TypeHashes.Keys).IsEquivalentTo([_placed])
      .Because("the stored form names the type with its assembly; an unknown type is not shared");
    await Assert.That(rules.TypeHashes[_placed]).IsEqualTo(new TypeRules(_placed).Hash());
    await Assert.That(source.RulesFor(null).TypeHashes.Keys).IsEquivalentTo([_placed, _pinged])
      .Because("null covers every event type it knows; a command has no eligibility rules");
  }

  [Test]
  public async Task RulesOf_ReadsEveryDeclaredRule_AndTheConfiguredEndOfLifeActionAsync() {
    var catalog = new Catalog(
      _event(typeof(Placed)) with { Ephemeral = new EphemeralInfo(Destruction.WhenConsumed, TransientStorage.PersistedRow), IsTerminal = true, IsSuperseding = true },
      _event(typeof(Pinged)),
      new MessageTypeCatalogEntry(typeof(EventAudited), TypeNameFormatter.FormatClrTypeName(typeof(EventAudited)), "event", null));
    var lifecycle = new StreamLifecycleOptions();
    lifecycle.Types[_placed] = new StreamTypeLifecycleOptions { Action = StreamLifecycleAction.Purge };

    var rules = CatalogRuleFingerprintSource.RulesOf(catalog, lifecycle);

    await Assert.That(rules[_placed]).IsEqualTo(new TypeRules(_placed) {
      Ephemeral = $"{Destruction.WhenConsumed};{TransientStorage.PersistedRow}",
      Superseding = true,
      Terminal = true,
      EndOfLife = nameof(StreamLifecycleAction.Purge),
    });
    await Assert.That(rules[_pinged]).IsEqualTo(new TypeRules(_pinged));
    await Assert.That(rules[TypeNameFormatter.FormatClrTypeName(typeof(EventAudited))].DigestExempt).IsTrue();
    await Assert.That(CatalogRuleFingerprintSource.RulesOf(catalog, null)[_placed].EndOfLife).IsNull()
      .Because("with no lifecycle settings a type has no end-of-life action");
  }

  [Test]
  public async Task For_ReflectsTheLifecycleSettings_AndTheEmptySourceKnowsNoTypeAsync() {
    var lifecycle = new StreamLifecycleOptions();
    lifecycle.Types[_placed] = new StreamTypeLifecycleOptions { Action = StreamLifecycleAction.Purge };
    var withPolicy = new CatalogRuleFingerprintSource(new Catalog(_event(typeof(Placed))), Options.Create(lifecycle));
    var without = new CatalogRuleFingerprintSource(new Catalog(_event(typeof(Placed))), Options.Create(new StreamLifecycleOptions()));

    await Assert.That(withPolicy.RulesFor([_placed]).TypeHashes[_placed]).IsNotEqualTo(without.RulesFor([_placed]).TypeHashes[_placed]);
    await Assert.That(CatalogRuleFingerprintSource.Empty.RulesFor([_placed]).TypeHashes).IsEmpty();
    await Assert.That(() => new CatalogRuleFingerprintSource(null!, Options.Create(lifecycle))).Throws<ArgumentNullException>();
    await Assert.That(() => new CatalogRuleFingerprintSource(new Catalog(_event(typeof(Placed))), null)).Throws<ArgumentNullException>()
      .Because("the lifecycle settings are a required dependency: a construction site that drops them fails loudly");
    await Assert.That(() => CatalogRuleFingerprintSource.RulesOf(null!, null)).Throws<ArgumentNullException>();
    await Assert.That(RuleTypeName.Of("global::Contracts.Placed, Contracts ")).IsEqualTo("Contracts.Placed");
  }
}
