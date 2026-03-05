using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for PerspectiveModelConsistencyAnalyzer WHIZ300.
/// Verifies that perspective classes use consistent model types across all implemented interfaces.
/// </summary>
/// <docs>diagnostics/whiz300</docs>
[Category("Analyzers")]
public class PerspectiveModelConsistencyAnalyzerTests {
  // ========================================
  // WHIZ300: Consistent Model Type Tests
  // ========================================

  /// <summary>
  /// Test that a class with no perspective interfaces produces no diagnostic.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_NoPerspectiveInterfaces_NoDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class OrderView { }

            public class OrderCreated : IEvent { }

            public class RegularClass {
              // Not a perspective, no interfaces
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelConsistencyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ300")).IsEmpty();
  }

  /// <summary>
  /// Test that a class with a single perspective interface produces no diagnostic.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_SinglePerspectiveInterface_NoDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class OrderView { }

            public class OrderCreated : IEvent { }

            public class OrderPerspective : IPerspectiveFor<OrderView, OrderCreated> {
              public OrderView Apply(OrderView current, OrderCreated @event) => current;
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelConsistencyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ300")).IsEmpty();
  }

  /// <summary>
  /// Test that a class with multiple perspective interfaces using the SAME model type produces no diagnostic.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MultiplePerspectivesWithSameModel_NoDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class OrderView { }

            public class OrderCreated : IEvent { }
            public class OrderUpdated : IEvent { }

            public class OrderPerspective :
                IPerspectiveFor<OrderView, OrderCreated>,
                IPerspectiveFor<OrderView, OrderUpdated> {
              public OrderView Apply(OrderView current, OrderCreated @event) => current;
              public OrderView Apply(OrderView current, OrderUpdated @event) => current;
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelConsistencyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ300")).IsEmpty();
  }

  /// <summary>
  /// Test that a class with multiple perspective interfaces using DIFFERENT model types produces WHIZ300.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MultiplePerspectivesWithDifferentModels_ReportsDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class OrderView { }
            public class ProductView { }

            public class OrderCreated : IEvent { }
            public class ProductCreated : IEvent { }

            public class BadPerspective :
                IPerspectiveFor<OrderView, OrderCreated>,
                IPerspectiveFor<ProductView, ProductCreated> {
              public OrderView Apply(OrderView current, OrderCreated @event) => current;
              public ProductView Apply(ProductView current, ProductCreated @event) => current;
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelConsistencyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ300")).Count().IsEqualTo(1);
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ300");
    await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);

    var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("BadPerspective");
    await Assert.That(message).Contains("OrderView");
    await Assert.That(message).Contains("ProductView");
  }

  /// <summary>
  /// Test that mixing IPerspectiveFor and IPerspectiveWithActionsFor with the same model produces no diagnostic.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MixedInterfacesWithSameModel_NoDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public class OrderView { }

            public class OrderCreated : IEvent { }
            public class OrderDeleted : IEvent { }

            public class OrderPerspective :
                IPerspectiveFor<OrderView, OrderCreated>,
                IPerspectiveWithActionsFor<OrderView, OrderDeleted> {
              public OrderView Apply(OrderView current, OrderCreated @event) => current;
              public OrderView Apply(OrderView current, OrderDeleted @event) => current;
              public ValueTask<PerspectiveActionResult> OnAppliedAsync(
                  OrderView before, OrderView after, OrderDeleted @event,
                  ILensWriter<OrderView> writer, CancellationToken ct) =>
                  new(PerspectiveActionResult.Success);
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelConsistencyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ300")).IsEmpty();
  }

  /// <summary>
  /// Test that mixing IPerspectiveFor and IPerspectiveWithActionsFor with DIFFERENT models produces WHIZ300.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MixedInterfacesWithDifferentModels_ReportsDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public class OrderView { }
            public class ProductView { }

            public class OrderCreated : IEvent { }
            public class ProductDeleted : IEvent { }

            public class BadMixedPerspective :
                IPerspectiveFor<OrderView, OrderCreated>,
                IPerspectiveWithActionsFor<ProductView, ProductDeleted> {
              public OrderView Apply(OrderView current, OrderCreated @event) => current;
              public ProductView Apply(ProductView current, ProductDeleted @event) => current;
              public ValueTask<PerspectiveActionResult> OnAppliedAsync(
                  ProductView before, ProductView after, ProductDeleted @event,
                  ILensWriter<ProductView> writer, CancellationToken ct) =>
                  new(PerspectiveActionResult.Success);
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelConsistencyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ300")).Count().IsEqualTo(1);
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ300");
    await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);

    var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("BadMixedPerspective");
  }

  /// <summary>
  /// Test that a class with multiple perspectives using the same model but handling multiple events produces no diagnostic.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MultipleEventsForSameModel_NoDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class OrderView { }

            public class OrderCreated : IEvent { }
            public class OrderUpdated : IEvent { }
            public class OrderCancelled : IEvent { }

            public class OrderPerspective :
                IPerspectiveFor<OrderView, OrderCreated>,
                IPerspectiveFor<OrderView, OrderUpdated>,
                IPerspectiveFor<OrderView, OrderCancelled> {
              public OrderView Apply(OrderView current, OrderCreated @event) => current;
              public OrderView Apply(OrderView current, OrderUpdated @event) => current;
              public OrderView Apply(OrderView current, OrderCancelled @event) => current;
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelConsistencyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ300")).IsEmpty();
  }

  /// <summary>
  /// Test that the analyzer detects model inconsistency even when class has many interfaces.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ManyInterfacesWithOneInconsistentModel_ReportsDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class OrderView { }
            public class ProductView { }

            public class OrderCreated : IEvent { }
            public class OrderUpdated : IEvent { }
            public class ProductDeleted : IEvent { }

            public class BadPerspectiveWithMany :
                IPerspectiveFor<OrderView, OrderCreated>,
                IPerspectiveFor<OrderView, OrderUpdated>,
                IPerspectiveFor<ProductView, ProductDeleted> {
              public OrderView Apply(OrderView current, OrderCreated @event) => current;
              public OrderView Apply(OrderView current, OrderUpdated @event) => current;
              public ProductView Apply(ProductView current, ProductDeleted @event) => current;
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelConsistencyAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ300")).Count().IsEqualTo(1);
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ300");
    await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);

    var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("BadPerspectiveWithMany");
    await Assert.That(message).Contains("OrderView");
    await Assert.That(message).Contains("ProductView");
  }
}
