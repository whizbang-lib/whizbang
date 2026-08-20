using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for the <see cref="TagOptions.RouteNamespace"/> binding surface — tags classify,
/// policies bind (transport-traffic-classes). A routing binding attaches a TransportNamespace
/// key to a tag string; message types never name a broker namespace themselves, and the same
/// tag may carry BOTH a coalesce policy and a routing binding (additive policies, one
/// vocabulary).
/// </summary>
[Category("Core")]
[Category("Tags")]
public class TagOptionsRouteNamespaceTests {
  [Test]
  public async Task RouteNamespaceBindings_IsEmptyByDefaultAsync() {
    var options = new TagOptions();

    await Assert.That(options.RouteNamespaceBindings.Count).IsEqualTo(0);
  }

  [Test]
  public async Task RouteNamespace_RegistersBindingForTagAsync() {
    var options = new TagOptions();

    options.RouteNamespace("sys-control", "control");

    await Assert.That(options.RouteNamespaceBindings.ContainsKey("sys-control")).IsTrue();
    await Assert.That(options.RouteNamespaceBindings["sys-control"]).IsEqualTo("control");
  }

  [Test]
  public async Task RouteNamespace_LastWinsPerTagAsync() {
    // Same shipped-default-override mechanism as Coalesce: a later binding for the same tag
    // replaces the earlier one entirely.
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");

    options.RouteNamespace("bulk-import", "batch");

    await Assert.That(options.RouteNamespaceBindings.Count).IsEqualTo(1);
    await Assert.That(options.RouteNamespaceBindings["bulk-import"]).IsEqualTo("batch");
  }

  [Test]
  public async Task RouteNamespace_MultipleTags_BindIndependentlyAsync() {
    var options = new TagOptions();

    options.RouteNamespace("bulk-import", "bulk");
    options.RouteNamespace("sys-control", "control");

    await Assert.That(options.RouteNamespaceBindings.Count).IsEqualTo(2);
    await Assert.That(options.RouteNamespaceBindings["bulk-import"]).IsEqualTo("bulk");
    await Assert.That(options.RouteNamespaceBindings["sys-control"]).IsEqualTo("control");
  }

  [Test]
  public async Task RouteNamespace_ReturnsSameInstanceForChainingAsync() {
    var options = new TagOptions();

    var result = options.RouteNamespace("bulk-import", "bulk");

    await Assert.That(result).IsEqualTo(options);
  }

  [Test]
  public async Task RouteNamespace_NullTag_ThrowsAsync() {
    var options = new TagOptions();

    await Assert.That(() => options.RouteNamespace(null!, "bulk")).Throws<ArgumentException>();
  }

  [Test]
  public async Task RouteNamespace_WhitespaceTag_ThrowsAsync() {
    var options = new TagOptions();

    await Assert.That(() => options.RouteNamespace("   ", "bulk")).Throws<ArgumentException>();
  }

  [Test]
  public async Task RouteNamespace_NullKey_ThrowsAsync() {
    var options = new TagOptions();

    await Assert.That(() => options.RouteNamespace("bulk-import", null!)).Throws<ArgumentException>();
  }

  [Test]
  public async Task RouteNamespace_WhitespaceKey_ThrowsAsync() {
    var options = new TagOptions();

    await Assert.That(() => options.RouteNamespace("bulk-import", "  ")).Throws<ArgumentException>();
  }

  [Test]
  public async Task RouteNamespace_AndCoalesce_AreAdditiveOnOneTagAsync() {
    // One vocabulary, additive policies: a tag may both coalesce (fold singles into
    // composites) and route (carry its traffic class to a TransportNamespace). Neither
    // binding disturbs the other.
    var options = new TagOptions();

    options.Coalesce("bulk-import", c => c.SlideSeconds = 30);
    options.RouteNamespace("bulk-import", "bulk");

    await Assert.That(options.CoalesceBindings["bulk-import"].SlideSeconds).IsEqualTo(30);
    await Assert.That(options.RouteNamespaceBindings["bulk-import"]).IsEqualTo("bulk");
  }
}
