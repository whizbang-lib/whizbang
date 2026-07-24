using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for EventNamespaceRegistry static holder.
/// Verifies the module initializer pattern for event namespace registration.
/// Note: Tests track RELATIVE counts since static state persists across parallel tests.
/// </summary>
public class EventNamespaceRegistryTests {
  #region Register Tests

  [Test]
  public async Task Register_WithValidSource_MakesNamespacesAvailableAsync() {
    // Arrange - use unique namespaces for this test
    var uniqueNs1 = $"register_valid_test_{Guid.NewGuid():N}.orders.events";
    var uniqueNs2 = $"register_valid_test_{Guid.NewGuid():N}.payments.events";
    var source = new TestEventNamespaceSource(
        perspectiveNamespaces: [uniqueNs1],
        receptorNamespaces: [uniqueNs2]
    );

    // Act
    EventNamespaceRegistry.Register(source);

    // Assert - the namespaces should now be available
    var allNamespaces = EventNamespaceRegistry.GetAllNamespaces();
    await Assert.That(allNamespaces.Contains(uniqueNs1)).IsTrue();
    await Assert.That(allNamespaces.Contains(uniqueNs2)).IsTrue();
  }

  [Test]
  public async Task Register_WithNull_ThrowsArgumentNullExceptionAsync() {
    // Act
    void action() => EventNamespaceRegistry.Register(null!);

    // Assert
    await Assert.That(action).Throws<ArgumentNullException>()
        .WithMessageContaining("source");
  }

  [Test]
  public async Task Register_MultipleSources_MakesAllNamespacesAvailableAsync() {
    // Arrange - use unique namespaces for each source
    var uniqueNs1 = $"multi_test_{Guid.NewGuid():N}.ns1";
    var uniqueNs2 = $"multi_test_{Guid.NewGuid():N}.ns2";
    var uniqueNs3 = $"multi_test_{Guid.NewGuid():N}.ns3";
    var source1 = new TestEventNamespaceSource(perspectiveNamespaces: [uniqueNs1]);
    var source2 = new TestEventNamespaceSource(perspectiveNamespaces: [uniqueNs2]);
    var source3 = new TestEventNamespaceSource(receptorNamespaces: [uniqueNs3]);

    // Act
    EventNamespaceRegistry.Register(source1);
    EventNamespaceRegistry.Register(source2);
    EventNamespaceRegistry.Register(source3);

    // Assert - all namespaces should be available
    var allNamespaces = EventNamespaceRegistry.GetAllNamespaces();
    await Assert.That(allNamespaces.Contains(uniqueNs1)).IsTrue();
    await Assert.That(allNamespaces.Contains(uniqueNs2)).IsTrue();
    await Assert.That(allNamespaces.Contains(uniqueNs3)).IsTrue();
  }

  #endregion

  #region GetAllNamespaces Tests

  [Test]
  public async Task GetAllNamespaces_AfterRegisteringSource_ContainsAllNamespacesAsync() {
    // Arrange
    var source = new TestEventNamespaceSource(
        perspectiveNamespaces: ["getall_test.orders.events"],
        receptorNamespaces: ["getall_test.payments.events"]
    );
    EventNamespaceRegistry.Register(source);

    // Act
    var namespaces = EventNamespaceRegistry.GetAllNamespaces();

    // Assert
    await Assert.That(namespaces.Contains("getall_test.orders.events")).IsTrue();
    await Assert.That(namespaces.Contains("getall_test.payments.events")).IsTrue();
  }

  [Test]
  public async Task GetAllNamespaces_WithMultipleSources_CombinesNamespacesAsync() {
    // Arrange
    var source1 = new TestEventNamespaceSource(perspectiveNamespaces: ["combine_test.orders.events"]);
    var source2 = new TestEventNamespaceSource(receptorNamespaces: ["combine_test.payments.events"]);
    EventNamespaceRegistry.Register(source1);
    EventNamespaceRegistry.Register(source2);

    // Act
    var namespaces = EventNamespaceRegistry.GetAllNamespaces();

    // Assert
    await Assert.That(namespaces.Contains("combine_test.orders.events")).IsTrue();
    await Assert.That(namespaces.Contains("combine_test.payments.events")).IsTrue();
  }

  [Test]
  public async Task GetAllNamespaces_WithDuplicates_DeduplicatesNamespacesAsync() {
    // Arrange - use unique namespace for this test
    const string uniqueNs = "dedup_test.shared.events";
    var source1 = new TestEventNamespaceSource(perspectiveNamespaces: [uniqueNs]);
    var source2 = new TestEventNamespaceSource(receptorNamespaces: [uniqueNs]);
    EventNamespaceRegistry.Register(source1);
    EventNamespaceRegistry.Register(source2);

    // Act
    var namespaces = EventNamespaceRegistry.GetAllNamespaces();

    // Assert - should only appear once despite being in two sources
    await Assert.That(namespaces.Contains(uniqueNs)).IsTrue();
    // Count the occurrences by counting matches
    var count = namespaces.Count(ns => ns.Equals(uniqueNs, StringComparison.OrdinalIgnoreCase));
    await Assert.That(count).IsEqualTo(1);
  }

  [Test]
  public async Task GetAllNamespaces_CaseInsensitive_DeduplicatesAsync() {
    // Arrange - same namespace with different casing (use unique names)
    var uniqueId = Guid.NewGuid().ToString("N");
    var ns1 = $"CaseTest{uniqueId}.Orders.Events";
    var ns2 = $"casetest{uniqueId}.orders.events";
    var source1 = new TestEventNamespaceSource(perspectiveNamespaces: [ns1]);
    var source2 = new TestEventNamespaceSource(perspectiveNamespaces: [ns2]);
    EventNamespaceRegistry.Register(source1);
    EventNamespaceRegistry.Register(source2);

    // Act
    var namespaces = EventNamespaceRegistry.GetAllNamespaces();

    // Assert - should deduplicate case-insensitively (only 1 entry despite 2 registrations)
    var count = namespaces.Count(ns => ns.Equals(ns2, StringComparison.OrdinalIgnoreCase));
    await Assert.That(count).IsEqualTo(1);
  }

  #endregion

  #region GetPerspectiveNamespaces Tests

  [Test]
  public async Task GetPerspectiveNamespaces_ReturnsPerspectiveNamespacesOnlyAsync() {
    // Arrange
    var source = new TestEventNamespaceSource(
        perspectiveNamespaces: ["perspective_only_test.perspective.events"],
        receptorNamespaces: ["perspective_only_test.receptor.events"]
    );
    EventNamespaceRegistry.Register(source);

    // Act
    var namespaces = EventNamespaceRegistry.GetPerspectiveNamespaces();

    // Assert
    await Assert.That(namespaces.Contains("perspective_only_test.perspective.events")).IsTrue();
    await Assert.That(namespaces.Contains("perspective_only_test.receptor.events")).IsFalse();
  }

  [Test]
  public async Task GetPerspectiveNamespaces_CombinesFromMultipleSourcesAsync() {
    // Arrange
    var source1 = new TestEventNamespaceSource(perspectiveNamespaces: ["perspective_combine_test.ns1"]);
    var source2 = new TestEventNamespaceSource(perspectiveNamespaces: ["perspective_combine_test.ns2"]);
    EventNamespaceRegistry.Register(source1);
    EventNamespaceRegistry.Register(source2);

    // Act
    var namespaces = EventNamespaceRegistry.GetPerspectiveNamespaces();

    // Assert
    await Assert.That(namespaces.Contains("perspective_combine_test.ns1")).IsTrue();
    await Assert.That(namespaces.Contains("perspective_combine_test.ns2")).IsTrue();
  }

  #endregion

  #region GetReceptorNamespaces Tests

  [Test]
  public async Task GetReceptorNamespaces_ReturnsReceptorNamespacesOnlyAsync() {
    // Arrange
    var source = new TestEventNamespaceSource(
        perspectiveNamespaces: ["receptor_only_test.perspective.events"],
        receptorNamespaces: ["receptor_only_test.receptor.events"]
    );
    EventNamespaceRegistry.Register(source);

    // Act
    var namespaces = EventNamespaceRegistry.GetReceptorNamespaces();

    // Assert
    await Assert.That(namespaces.Contains("receptor_only_test.receptor.events")).IsTrue();
    await Assert.That(namespaces.Contains("receptor_only_test.perspective.events")).IsFalse();
  }

  [Test]
  public async Task GetReceptorNamespaces_CombinesFromMultipleSourcesAsync() {
    // Arrange
    var source1 = new TestEventNamespaceSource(receptorNamespaces: ["receptor_combine_test.ns1"]);
    var source2 = new TestEventNamespaceSource(receptorNamespaces: ["receptor_combine_test.ns2"]);
    EventNamespaceRegistry.Register(source1);
    EventNamespaceRegistry.Register(source2);

    // Act
    var namespaces = EventNamespaceRegistry.GetReceptorNamespaces();

    // Assert
    await Assert.That(namespaces.Contains("receptor_combine_test.ns1")).IsTrue();
    await Assert.That(namespaces.Contains("receptor_combine_test.ns2")).IsTrue();
  }

  #endregion

  #region Clear Tests

  [Test]
  [NotInParallel(Order = int.MaxValue)] // Run last and not in parallel to avoid affecting other tests
  public async Task Clear_RemovesAllSourcesAsync() {
    // Arrange - add a source first
    var source = new TestEventNamespaceSource(perspectiveNamespaces: ["clear_test.ns1"]);
    EventNamespaceRegistry.Register(source);
    var countBefore = EventNamespaceRegistry.RegisteredCount;
    await Assert.That(countBefore).IsGreaterThan(0);

    // Act
    EventNamespaceRegistry.Clear();

    // Assert
    await Assert.That(EventNamespaceRegistry.RegisteredCount).IsEqualTo(0);
    await Assert.That(EventNamespaceRegistry.GetAllNamespaces().Count).IsEqualTo(0);
  }

  #endregion

  #region Test Helpers

  /// <summary>
  /// Test implementation of IEventNamespaceSource for unit testing.
  /// </summary>
  private sealed class TestEventNamespaceSource : IEventNamespaceSource {
    private readonly HashSet<string> _perspectiveNamespaces;
    private readonly HashSet<string> _receptorNamespaces;
    private readonly HashSet<string> _allNamespaces;

    public TestEventNamespaceSource(
        string[]? perspectiveNamespaces = null,
        string[]? receptorNamespaces = null) {
      _perspectiveNamespaces = new HashSet<string>(
          perspectiveNamespaces ?? [],
          StringComparer.OrdinalIgnoreCase);
      _receptorNamespaces = new HashSet<string>(
          receptorNamespaces ?? [],
          StringComparer.OrdinalIgnoreCase);

      _allNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var ns in _perspectiveNamespaces) {
        _allNamespaces.Add(ns);
      }
      foreach (var ns in _receptorNamespaces) {
        _allNamespaces.Add(ns);
      }
    }

    public IReadOnlySet<string> GetPerspectiveEventNamespaces() => _perspectiveNamespaces;
    public IReadOnlySet<string> GetReceptorEventNamespaces() => _receptorNamespaces;
    public IReadOnlySet<string> GetAllEventNamespaces() => _allNamespaces;
  }

  #endregion
}
