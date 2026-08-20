using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tags;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="TransportNamespaceResolver"/> — the AOT tag lookup the transport
/// boundary consults to map a message type to its TransportNamespace key. A message type
/// whose tag carries a routing binding resolves to that binding's key; everything else
/// resolves to <see cref="TransportNamespaces.DefaultKey"/>. Resolution rides the generated
/// MessageTagRegistry via <see cref="EventTypeMatchingHelper"/> (never reflection) and caches
/// per type-name string — the CoalesceGroupResolver idiom.
/// </summary>
[Category("Core")]
[Category("Tags")]
public class TransportNamespaceResolverTests {
  private static readonly string[] _bulkAndControl = ["bulk", "control"];

  [Test]
  public async Task Resolve_BoundTag_ReturnsRoutedKeyAsync() {
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");
    var resolver = new TransportNamespaceResolver(
      options, () => [CoalesceGroupResolverTests.TagRegistration(typeof(TestBulkEvent), "bulk-import")]);

    var key = resolver.ResolveNamespaceKey(typeof(TestBulkEvent).AssemblyQualifiedName!);

    await Assert.That(key).IsEqualTo("bulk");
  }

  [Test]
  public async Task Resolve_UnboundTag_ReturnsDefaultAsync() {
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");
    var resolver = new TransportNamespaceResolver(
      options, () => [CoalesceGroupResolverTests.TagRegistration(typeof(TestBulkEvent), "some-other-tag")]);

    var key = resolver.ResolveNamespaceKey(typeof(TestBulkEvent).AssemblyQualifiedName!);

    await Assert.That(key).IsEqualTo(TransportNamespaces.DefaultKey);
  }

  [Test]
  public async Task Resolve_UntaggedType_ReturnsDefaultAsync() {
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");
    var resolver = new TransportNamespaceResolver(options, () => []);

    var key = resolver.ResolveNamespaceKey(typeof(TestBulkEvent).AssemblyQualifiedName!);

    await Assert.That(key).IsEqualTo(TransportNamespaces.DefaultKey);
  }

  [Test]
  public async Task Resolve_NoBindingsAtAll_ReturnsDefaultAsync() {
    // The spec's key promise: with no routing bindings, everything uses default.
    var resolver = new TransportNamespaceResolver(
      new TagOptions(), () => [CoalesceGroupResolverTests.TagRegistration(typeof(TestBulkEvent), "bulk-import")]);

    var key = resolver.ResolveNamespaceKey(typeof(TestBulkEvent).AssemblyQualifiedName!);

    await Assert.That(key).IsEqualTo(TransportNamespaces.DefaultKey);
    await Assert.That(resolver.HasBindings).IsFalse();
  }

  [Test]
  public async Task HasBindings_WithRouteBinding_IsTrueAsync() {
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");

    var resolver = new TransportNamespaceResolver(options, () => []);

    await Assert.That(resolver.HasBindings).IsTrue();
  }

  [Test]
  public async Task Resolve_ResolutionIsCachedPerTypeNameAsync() {
    // The registration source must be consulted once (lazily); subsequent resolutions for the
    // same type-name string hit the per-name cache, never re-walk the registry.
    var calls = 0;
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");
    var resolver = new TransportNamespaceResolver(options, () => {
      calls++;
      return [CoalesceGroupResolverTests.TagRegistration(typeof(TestBulkEvent), "bulk-import")];
    });
    var typeName = typeof(TestBulkEvent).AssemblyQualifiedName!;

    _ = resolver.ResolveNamespaceKey(typeName);
    _ = resolver.ResolveNamespaceKey(typeName);
    _ = resolver.ResolveNamespaceKey(typeName);

    await Assert.That(calls).IsEqualTo(1);
  }

  [Test]
  public async Task Resolve_ShortTypeNameForm_ResolvesViaMatchingHelperAsync() {
    // The transport boundary holds stored type-name STRINGS in several canonical forms;
    // resolution must ride EventTypeMatchingHelper like the coalesce path, not exact match.
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");
    var resolver = new TransportNamespaceResolver(
      options, () => [CoalesceGroupResolverTests.TagRegistration(typeof(TestBulkEvent), "bulk-import")]);

    var key = resolver.ResolveNamespaceKey(typeof(TestBulkEvent).FullName!);

    await Assert.That(key).IsEqualTo("bulk");
  }

  [Test]
  public async Task ResolveConsumeNamespaceKeys_ReturnsDistinctNonDefaultKeysSortedAsync() {
    // The consume-side rule: a service subscribes its normal set in default PLUS the same
    // entity set in every non-default namespace any of its consumed/handled types resolves
    // to. This projection feeds that rule — distinct, default excluded, deterministic order.
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");
    options.RouteNamespace("sys-control", "control");
    var resolver = new TransportNamespaceResolver(options, () => [
      CoalesceGroupResolverTests.TagRegistration(typeof(TestBulkEvent), "bulk-import"),
      CoalesceGroupResolverTests.TagRegistration(typeof(TestOtherBulkEvent), "bulk-import"),
      CoalesceGroupResolverTests.TagRegistration(typeof(TestControlCommand), "sys-control")
    ]);

    var keys = resolver.ResolveConsumeNamespaceKeys([
      typeof(TestControlCommand).FullName!,
      typeof(TestBulkEvent).FullName!,
      typeof(TestOtherBulkEvent).FullName!,
      typeof(TestUntaggedEvent).FullName!
    ]);

    await Assert.That(keys).IsEquivalentTo(_bulkAndControl)
      .Because("distinct non-default keys only — untagged types resolve to default and add nothing");
  }

  [Test]
  public async Task ResolveConsumeNamespaceKeys_NoRoutedTypes_IsEmptyAsync() {
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");
    var resolver = new TransportNamespaceResolver(options, () => []);

    var keys = resolver.ResolveConsumeNamespaceKeys([typeof(TestUntaggedEvent).FullName!]);

    await Assert.That(keys.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Constructor_NullTagOptions_ThrowsAsync() {
    await Assert.That(() => new TransportNamespaceResolver(null!)).Throws<ArgumentNullException>();
  }

  internal sealed record TestBulkEvent : IEvent;

  internal sealed record TestOtherBulkEvent : IEvent;

  internal sealed record TestControlCommand;

  internal sealed record TestUntaggedEvent : IEvent;
}
