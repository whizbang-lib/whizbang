using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="ITrackedEventTypeRegistry"/> and <see cref="TrackedEventTypeRegistry"/>.
/// </summary>
/// <docs>core-concepts/perspectives/perspective-sync#type-registry</docs>
public class TrackedEventTypeRegistryTests {
  // Sample event types for testing
  private sealed record TestEventA;
  private sealed record TestEventB;
  private sealed record TestEventC;

  // ==========================================================================
  // Constructor tests
  // ==========================================================================

  [Test]
  public async Task Constructor_DefaultEmpty_CreatesEmptyRegistryAsync() {
    var registry = new TrackedEventTypeRegistry();

    await Assert.That(registry.ShouldTrack(typeof(TestEventA))).IsFalse();
    await Assert.That(registry.GetPerspectiveName(typeof(TestEventA))).IsNull();
    await Assert.That(registry.GetPerspectiveNames(typeof(TestEventA)).Count).IsEqualTo(0);
  }

  [Test]
  public async Task Constructor_WithSingleMappings_RegistersTypesAsync() {
    var mappings = new Dictionary<Type, string> {
      { typeof(TestEventA), "PerspectiveA" },
      { typeof(TestEventB), "PerspectiveB" }
    };

    var registry = new TrackedEventTypeRegistry(mappings);

    await Assert.That(registry.ShouldTrack(typeof(TestEventA))).IsTrue();
    await Assert.That(registry.ShouldTrack(typeof(TestEventB))).IsTrue();
    await Assert.That(registry.ShouldTrack(typeof(TestEventC))).IsFalse();
  }

  [Test]
  public async Task Constructor_WithArrayMappings_RegistersTypesAsync() {
    var mappings = new Dictionary<Type, string[]> {
      { typeof(TestEventA), ["PerspectiveA", "PerspectiveB"] },
      { typeof(TestEventB), ["PerspectiveB"] }
    };

    var registry = new TrackedEventTypeRegistry(mappings);

    await Assert.That(registry.ShouldTrack(typeof(TestEventA))).IsTrue();
    await Assert.That(registry.ShouldTrack(typeof(TestEventB))).IsTrue();
  }

  [Test]
  public async Task Constructor_NullMappings_ThrowsAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await Task.FromResult(new TrackedEventTypeRegistry((IReadOnlyDictionary<Type, string>)null!)));
  }

  [Test]
  public async Task Constructor_NullArrayMappings_ThrowsAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await Task.FromResult(new TrackedEventTypeRegistry((IReadOnlyDictionary<Type, string[]>)null!)));
  }

  // ==========================================================================
  // ShouldTrack tests
  // ==========================================================================

  [Test]
  public async Task ShouldTrack_RegisteredType_ReturnsTrueAsync() {
    var registry = new TrackedEventTypeRegistry(new Dictionary<Type, string> {
      { typeof(TestEventA), "TestPerspective" }
    });

    await Assert.That(registry.ShouldTrack(typeof(TestEventA))).IsTrue();
  }

  [Test]
  public async Task ShouldTrack_UnregisteredType_ReturnsFalseAsync() {
    var registry = new TrackedEventTypeRegistry(new Dictionary<Type, string> {
      { typeof(TestEventA), "TestPerspective" }
    });

    await Assert.That(registry.ShouldTrack(typeof(TestEventB))).IsFalse();
  }

  [Test]
  public async Task ShouldTrack_NullType_ThrowsAsync() {
    var registry = new TrackedEventTypeRegistry();

    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await Task.FromResult(registry.ShouldTrack(null!)));
  }

  // ==========================================================================
  // GetPerspectiveName tests
  // ==========================================================================

  [Test]
  public async Task GetPerspectiveName_RegisteredType_ReturnsPerspectiveNameAsync() {
    var registry = new TrackedEventTypeRegistry(new Dictionary<Type, string> {
      { typeof(TestEventA), "TestPerspective" }
    });

    await Assert.That(registry.GetPerspectiveName(typeof(TestEventA))).IsEqualTo("TestPerspective");
  }

  [Test]
  public async Task GetPerspectiveName_UnregisteredType_ReturnsNullAsync() {
    var registry = new TrackedEventTypeRegistry(new Dictionary<Type, string> {
      { typeof(TestEventA), "TestPerspective" }
    });

    await Assert.That(registry.GetPerspectiveName(typeof(TestEventB))).IsNull();
  }

  [Test]
  public async Task GetPerspectiveName_MultiplePerspectives_ReturnsFirstAsync() {
    var registry = new TrackedEventTypeRegistry(new Dictionary<Type, string[]> {
      { typeof(TestEventA), ["PerspectiveA", "PerspectiveB"] }
    });

    await Assert.That(registry.GetPerspectiveName(typeof(TestEventA))).IsEqualTo("PerspectiveA");
  }

  [Test]
  public async Task GetPerspectiveName_NullType_ThrowsAsync() {
    var registry = new TrackedEventTypeRegistry();

    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await Task.FromResult(registry.GetPerspectiveName(null!)));
  }

  // ==========================================================================
  // GetPerspectiveNames tests
  // ==========================================================================

  [Test]
  public async Task GetPerspectiveNames_SinglePerspective_ReturnsListWithOneItemAsync() {
    var registry = new TrackedEventTypeRegistry(new Dictionary<Type, string> {
      { typeof(TestEventA), "TestPerspective" }
    });

    var names = registry.GetPerspectiveNames(typeof(TestEventA));

    await Assert.That(names.Count).IsEqualTo(1);
    await Assert.That(names[0]).IsEqualTo("TestPerspective");
  }

  [Test]
  public async Task GetPerspectiveNames_MultiplePerspectives_ReturnsAllAsync() {
    var registry = new TrackedEventTypeRegistry(new Dictionary<Type, string[]> {
      { typeof(TestEventA), ["PerspectiveA", "PerspectiveB", "PerspectiveC"] }
    });

    var names = registry.GetPerspectiveNames(typeof(TestEventA));

    await Assert.That(names.Count).IsEqualTo(3);
    await Assert.That(names).Contains("PerspectiveA");
    await Assert.That(names).Contains("PerspectiveB");
    await Assert.That(names).Contains("PerspectiveC");
  }

  [Test]
  public async Task GetPerspectiveNames_UnregisteredType_ReturnsEmptyListAsync() {
    var registry = new TrackedEventTypeRegistry(new Dictionary<Type, string> {
      { typeof(TestEventA), "TestPerspective" }
    });

    var names = registry.GetPerspectiveNames(typeof(TestEventB));

    await Assert.That(names.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetPerspectiveNames_NullType_ThrowsAsync() {
    var registry = new TrackedEventTypeRegistry();

    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await Task.FromResult(registry.GetPerspectiveNames(null!)));
  }
}
