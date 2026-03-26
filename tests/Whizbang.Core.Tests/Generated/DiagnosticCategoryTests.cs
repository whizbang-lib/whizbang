using TUnit.Assertions;
using Whizbang.Core.Generated;

namespace Whizbang.Core.Tests.Generated;

/// <summary>
/// Tests for DiagnosticCategories enum - categorization of diagnostic information.
/// </summary>
[Category("Diagnostics")]
public class DiagnosticCategoryTests {

  [Test]
  public async Task DiagnosticCategory_None_HasValueZeroAsync() {
    // Arrange & Act
    var none = DiagnosticCategories.None;

    // Assert
    await Assert.That((int)none).IsEqualTo(0);
  }

  [Test]
  public async Task DiagnosticCategory_All_CombinesAllCategoriesAsync() {
    // Arrange
    var expected = DiagnosticCategories.ReceptorDiscovery | DiagnosticCategories.Dispatcher | DiagnosticCategories.EventHandling;

    // Act
    var all = DiagnosticCategories.All;

    // Assert
    await Assert.That(all).IsEqualTo(expected);
    await Assert.That((int)all).IsEqualTo(7);
  }

  [Test]
  public async Task DiagnosticCategory_SupportsFlags_BitwiseOperationsAsync() {
    // Arrange
    var combined = DiagnosticCategories.ReceptorDiscovery | DiagnosticCategories.Dispatcher;

    // Act
    var hasReceptorDiscovery = (combined & DiagnosticCategories.ReceptorDiscovery) == DiagnosticCategories.ReceptorDiscovery;
    var hasDispatcher = (combined & DiagnosticCategories.Dispatcher) == DiagnosticCategories.Dispatcher;
    var hasEventHandling = (combined & DiagnosticCategories.EventHandling) == DiagnosticCategories.EventHandling;

    // Assert
    await Assert.That(hasReceptorDiscovery).IsTrue();
    await Assert.That(hasDispatcher).IsTrue();
    await Assert.That(hasEventHandling).IsFalse();
  }

  [Test]
  public async Task DiagnosticCategory_IndividualFlags_HaveUniqueValuesAsync() {
    // Arrange & Act
    var receptorDiscovery = DiagnosticCategories.ReceptorDiscovery;
    var dispatcher = DiagnosticCategories.Dispatcher;
    var eventHandling = DiagnosticCategories.EventHandling;

    // Assert
    await Assert.That((int)receptorDiscovery).IsEqualTo(1);
    await Assert.That((int)dispatcher).IsEqualTo(2);
    await Assert.That((int)eventHandling).IsEqualTo(4);
  }
}
