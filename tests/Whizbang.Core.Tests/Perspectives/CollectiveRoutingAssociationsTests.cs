// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// What the startup associations step writes so the store creates <c>__collective__</c> rows only for the collective
/// event types this service handles (#2145): one <c>collective</c> association per handled type plus the per-service
/// <c>collective_gate</c> marker that says this version gates, and a hash over the set so a changed handler set
/// re-writes them. An unknown set writes neither, so the store keeps creating every row.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/CollectiveRoutingAssociations.cs</code-under-test>
public class CollectiveRoutingAssociationsTests {
  private const string SERVICE = "Jobs.Service";
  private const string PERSPECTIVE_HASH = "abc123";

  [Test]
  public async Task Entries_ForAKnownSet_AreOneCollectiveAssociationPerType_AndTheGateMarkerAsync() {
    var json = CollectiveRoutingAssociations.AssociationEntriesJson(["Jobs.Archive, Jobs", "Jobs.Purge, Jobs"], SERVICE);

    await Assert.That(json).StartsWith(",").Because("the entries follow the perspective associations in one JSON array");
    using var doc = JsonDocument.Parse("[{}" + json + "]");
    var rows = doc.RootElement.EnumerateArray().Skip(1).Select(e => (
      Type: e.GetProperty("MessageType").GetString(),
      Kind: e.GetProperty("AssociationType").GetString(),
      Target: e.GetProperty("TargetName").GetString(),
      Service: e.GetProperty("ServiceName").GetString())).ToList();
    await Assert.That(rows).IsEquivalentTo([
      ("Jobs.Archive, Jobs", "collective", "__collective__", SERVICE),
      ("Jobs.Purge, Jobs", "collective", "__collective__", SERVICE),
      ("__collective__", "collective_gate", "__collective__", SERVICE)]);
  }

  [Test]
  public async Task Entries_ForAnEmptyKnownSet_AreTheGateMarkerAloneAsync() {
    // A service that handles no collective event gates every one of them: that is the case the issue is about.
    var json = CollectiveRoutingAssociations.AssociationEntriesJson([], SERVICE);

    using var doc = JsonDocument.Parse("[{}" + json + "]");
    var only = doc.RootElement.EnumerateArray().Skip(1).Single();
    await Assert.That(only.GetProperty("AssociationType").GetString()).IsEqualTo(CollectiveRoutingAssociations.GATE_ASSOCIATION_TYPE);
  }

  [Test]
  public async Task Entries_ForAnUnknownSet_AreNothing_SoTheStoreKeepsCreatingEveryRowAsync() {
    await Assert.That(CollectiveRoutingAssociations.AssociationEntriesJson(null, SERVICE)).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task Entries_EscapeWhatJsonRequiresAsync() {
    var json = CollectiveRoutingAssociations.AssociationEntriesJson(["Odd\"Type, Asm"], "svc\\name");

    using var doc = JsonDocument.Parse("[{}" + json + "]");
    var first = doc.RootElement.EnumerateArray().Skip(1).First();
    await Assert.That(first.GetProperty("MessageType").GetString()).IsEqualTo("Odd\"Type, Asm");
    await Assert.That(first.GetProperty("ServiceName").GetString()).IsEqualTo("svc\\name");
  }

  [Test]
  public async Task Hash_ChangesWhenTheHandledSetChanges_AndOnlyThenAsync() {
    var archive = CollectiveRoutingAssociations.AssociationsHash(PERSPECTIVE_HASH, ["Jobs.Archive, Jobs"]);
    var both = CollectiveRoutingAssociations.AssociationsHash(PERSPECTIVE_HASH, ["Jobs.Purge, Jobs", "Jobs.Archive, Jobs"]);
    var bothReordered = CollectiveRoutingAssociations.AssociationsHash(
      PERSPECTIVE_HASH, ["Jobs.Archive, Jobs", "Jobs.Purge, Jobs", "Jobs.Archive, Jobs"]);

    await Assert.That(both).IsNotEqualTo(archive).Because("adding a handler must re-write the associations");
    await Assert.That(bothReordered).IsEqualTo(both).Because("the order the assemblies loaded in is not a change");
    await Assert.That(archive).StartsWith(PERSPECTIVE_HASH).Because("the perspective set still drives the hash");
  }

  [Test]
  public async Task Hash_TellsUnknownFromEmpty_AndFromThePerspectiveSetAloneAsync() {
    var unknown = CollectiveRoutingAssociations.AssociationsHash(PERSPECTIVE_HASH, null);
    var empty = CollectiveRoutingAssociations.AssociationsHash(PERSPECTIVE_HASH, []);

    await Assert.That(unknown).IsNotEqualTo(empty)
      .Because("going from unknown to known writes the gate marker, so it must re-run the step");
    await Assert.That(unknown).IsNotEqualTo(PERSPECTIVE_HASH)
      .Because("a database an earlier release registered has no gate yet, and its next start must write one");
  }

  [Test]
  public async Task Hash_ForAContextWithNoPerspectives_StaysEmpty_SoItHasNoAssociationsStepAsync() {
    await Assert.That(CollectiveRoutingAssociations.AssociationsHash(string.Empty, ["Jobs.Archive, Jobs"])).IsEqualTo(string.Empty);
  }
}
