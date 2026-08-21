using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tests for <see cref="TransportNamespaces"/> — the shared TransportNamespace vocabulary
/// (topology arc phase 8): the reserved <c>default</c> key, the destination-metadata carrier
/// the publish strategy stamps and the transports read, and the naming invariant that a
/// namespace key never alters the entity name (the class prefix is the NAMESPACE, not the
/// entity name).
/// </summary>
[Category("Core")]
[Category("Transports")]
public class TransportNamespacesTests {
  [Test]
  public async Task DefaultKey_IsDefaultAsync() {
    await Assert.That(TransportNamespaces.DefaultKey).IsEqualTo("default");
  }

  [Test]
  public async Task IsDefault_MatchesOrdinalAsync() {
    await Assert.That(TransportNamespaces.IsDefault("default")).IsTrue();
    await Assert.That(TransportNamespaces.IsDefault("bulk")).IsFalse();
    await Assert.That(TransportNamespaces.IsDefault("Default")).IsFalse();
  }

  [Test]
  public async Task FromMetadata_NullMetadata_ReturnsDefaultAsync() {
    await Assert.That(TransportNamespaces.FromMetadata(null)).IsEqualTo(TransportNamespaces.DefaultKey);
  }

  [Test]
  public async Task FromMetadata_NoKey_ReturnsDefaultAsync() {
    var metadata = new Dictionary<string, JsonElement> {
      ["StreamId"] = JsonDocument.Parse("\"abc\"").RootElement
    };

    await Assert.That(TransportNamespaces.FromMetadata(metadata)).IsEqualTo(TransportNamespaces.DefaultKey);
  }

  [Test]
  public async Task FromMetadata_StampedKey_ReturnsItAsync() {
    var metadata = new Dictionary<string, JsonElement> {
      [TransportNamespaces.MetadataKey] = JsonDocument.Parse("\"bulk\"").RootElement
    };

    await Assert.That(TransportNamespaces.FromMetadata(metadata)).IsEqualTo("bulk");
  }

  [Test]
  public async Task FromMetadata_NonStringValue_ReturnsDefaultAsync() {
    var metadata = new Dictionary<string, JsonElement> {
      [TransportNamespaces.MetadataKey] = JsonDocument.Parse("42").RootElement
    };

    await Assert.That(TransportNamespaces.FromMetadata(metadata)).IsEqualTo(TransportNamespaces.DefaultKey);
  }

  [Test]
  public async Task Stamp_AddsMetadataKeyPreservingExistingEntriesAndNamesAsync() {
    // The naming forward-compat lock: stamping a TransportNamespace changes WHERE the entity
    // lives (which broker namespace), never WHAT it is called — Address and RoutingKey must
    // come through untouched, alongside any metadata already present.
    var original = new TransportDestination(
      Address: "inbox.myapp.orders.commands",
      RoutingKey: "myapp.orders.commands.createorder",
      Metadata: new Dictionary<string, JsonElement> {
        ["StreamId"] = JsonDocument.Parse("\"abc\"").RootElement
      });

    var stamped = TransportNamespaces.Stamp(original, "bulk");

    await Assert.That(stamped.Address).IsEqualTo(original.Address);
    await Assert.That(stamped.RoutingKey).IsEqualTo(original.RoutingKey);
    await Assert.That(TransportNamespaces.FromMetadata(stamped.Metadata)).IsEqualTo("bulk");
    await Assert.That(stamped.Metadata!["StreamId"].GetString()).IsEqualTo("abc");
    await Assert.That(original.Metadata!.ContainsKey(TransportNamespaces.MetadataKey)).IsFalse()
      .Because("stamping returns a copy — TransportDestination stays immutable");
  }

  [Test]
  public async Task Stamp_NullMetadata_CreatesMetadataWithOnlyTheKeyAsync() {
    var original = new TransportDestination(Address: "orders", RoutingKey: "orders.#");

    var stamped = TransportNamespaces.Stamp(original, "control");

    await Assert.That(TransportNamespaces.FromMetadata(stamped.Metadata)).IsEqualTo("control");
    await Assert.That(stamped.Metadata!.Count).IsEqualTo(1);
  }
}
