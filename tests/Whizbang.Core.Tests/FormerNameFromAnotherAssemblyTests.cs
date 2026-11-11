// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests;

/// <summary>An event type whose ledger entry records a former name from another assembly.</summary>
public sealed record MovedAssemblyProbeEvent : IEvent;

/// <summary>
/// #2264: the pinned-type ledger records a former name with its assembly when the type moved from another assembly
/// (<c>Ns.Type, Assembly</c>), and the catalog carries it so. Every consumer keyed by the bare CLR name (the registry
/// reconcile, the marker resolver, the declared-kind lookup) reads the name part of such a former name; the history walks
/// and the rewind grace keep the assembly, through <see cref="TypeNameFormatter.FormatFormerName"/>.
/// </summary>
/// <docs>fundamentals/identity/pinned-type-ledger#a-type-that-moves-to-another-assembly</docs>
/// <code-under-test>src/Whizbang.Core/MessageTypeRegistryJson.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/EventMarkerResolver.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/CatalogMessageKinds.cs</code-under-test>
public class FormerNameFromAnotherAssemblyTests {
  // The former name as the ledger records a type that moved assemblies. The assembly is a simulated old one: the type
  // never lived anywhere but this test assembly, so its old assembly's name has to be stated here.
  private const string FORMER_BARE = "Former.Contracts.OrderSubmitted";
  private const string FORMER_RECORDED = FORMER_BARE + ", Former.Contracts";

  private static readonly MessageTypeCatalogEntry _entry =
    new(typeof(MovedAssemblyProbeEvent), TypeNameFormatter.FormatClrTypeName(typeof(MovedAssemblyProbeEvent)), "event",
      "44444444-4444-4444-4444-444444444444") {
      // A blank former name is skipped, and one recorded both ways is one bare name.
      FormerNames = [FORMER_RECORDED, " ", FORMER_BARE],
      IsComposite = true,
    };

  [Test]
  public async Task TheRegistryReconcile_IsGivenTheBareFormerName_ItsRowsAreKeyedByAsync() {
    using var document = JsonDocument.Parse(MessageTypeRegistryJson.Serialize([_entry]));

    var former = document.RootElement[0].GetProperty("FormerNames").EnumerateArray().Select(n => n.GetString()!).ToList();

    await Assert.That(former).IsEquivalentTo([FORMER_BARE])
      .Because("wh_message_type_registry.clr_type_name holds the bare CLR name, so a former name with an assembly heals no row");
  }

  [Test]
  public async Task TheMarkerResolver_ResolvesTheBareFormerNameAsync() {
    var flags = new EventMarkerResolver(new Catalog(_entry)).Resolve(FORMER_BARE);

    await Assert.That(flags.HasValue).IsTrue().Because("the flag deriver asks by the bare CLR name of the wire type");
    await Assert.That(flags!.Value.HasFlag(EventFlags.Composite)).IsTrue();
  }

  [Test]
  public async Task TheDeclaredKind_IsFoundForAnEnvelopeWrittenUnderTheOldAssemblyAsync() {
    var kinds = new CatalogMessageKinds(new Catalog(_entry));

    var isEvent = kinds.IsEvent(FORMER_RECORDED + ", Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");

    await Assert.That(isEvent.HasValue).IsTrue();
    await Assert.That(isEvent!.Value).IsTrue();
  }

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }
}
