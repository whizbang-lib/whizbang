using TUnit.Assertions;
using Whizbang.Core.Generated;

namespace Whizbang.Core.Tests.Generated;

/// <summary>
/// Tests for DiagnosticCategory enum - categorization of diagnostic information.
/// </summary>
[Category("Diagnostics")]
public class DiagnosticCategoryTests {

  [Test]
  public async Task DiagnosticCategory_None_HasValueZeroAsync() {
    // Arrange & Act
    var none = DiagnosticCategory.None;

    // Assert
    await Assert.That((int)none).IsEqualTo(0);
  }

  [Test]
  public async Task DiagnosticCategory_All_CombinesAllCategoriesAsync() {
    // Arrange
    var expected = DiagnosticCategory.ReceptorDiscovery | DiagnosticCategory.Dispatcher | DiagnosticCategory.EventHandling;

    // Act
    var all = DiagnosticCategory.All;

    // Assert
    await Assert.That(all).IsEqualTo(expected);
    await Assert.That((int)all).IsEqualTo(7);
  }

  [Test]
  public async Task DiagnosticCategory_SupportsFlags_BitwiseOperationsAsync() {
    // Arrange
    var combined = DiagnosticCategory.ReceptorDiscovery | DiagnosticCategory.Dispatcher;

    // Act
    var hasReceptorDiscovery = (combined & DiagnosticCategory.ReceptorDiscovery) == DiagnosticCategory.ReceptorDiscovery;
    var hasDispatcher = (combined & DiagnosticCategory.Dispatcher) == DiagnosticCategory.Dispatcher;
    var hasEventHandling = (combined & DiagnosticCategory.EventHandling) == DiagnosticCategory.EventHandling;

    // Assert
    await Assert.That(hasReceptorDiscovery).IsTrue();
    await Assert.That(hasDispatcher).IsTrue();
    await Assert.That(hasEventHandling).IsFalse();
  }

  [Test]
  public async Task DiagnosticCategory_IndividualFlags_HaveUniqueValuesAsync() {
    // Arrange & Act
    var receptorDiscovery = DiagnosticCategory.ReceptorDiscovery;
    var dispatcher = DiagnosticCategory.Dispatcher;
    var eventHandling = DiagnosticCategory.EventHandling;

    // Assert
    await Assert.That((int)receptorDiscovery).IsEqualTo(1);
    await Assert.That((int)dispatcher).IsEqualTo(2);
    await Assert.That((int)eventHandling).IsEqualTo(4);
  }
}
