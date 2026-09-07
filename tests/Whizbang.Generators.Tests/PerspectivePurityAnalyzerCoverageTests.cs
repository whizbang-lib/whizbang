using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage for <see cref="PerspectivePurityAnalyzer"/> paths the existing
/// <c>PerspectivePurityAnalyzerTests</c> never exercise: a constructor parameter typed as a
/// generic type parameter (not a concrete named type), a generic perspective whose <c>Apply</c>
/// return type is its own unbound type parameter, and an <c>Apply</c> method returning the bare,
/// non-generic <c>Task</c> rather than <c>Task&lt;TModel&gt;</c>.
/// </summary>
/// <tests>Whizbang.Generators/PerspectivePurityAnalyzer.cs</tests>
[Category("Analyzers")]
public class PerspectivePurityAnalyzerCoverageTests {
  /// <summary>
  /// Verifies that a constructor parameter typed as a generic type parameter (rather than a
  /// concrete named type) is treated as an unproven, non-pure service. Without this fallback,
  /// a generic perspective injecting a dependency through its own type parameter would never be
  /// checked for purity at all, silently letting an unconstrained (and potentially impure)
  /// dependency through replay.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task GenericTypeParameterConstructorInjection_ReportsWHIZ105Async() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class Order {
              public Guid Id { get; set; }
            }

            public class OrderUpdated {
              public Guid OrderId { get; set; }
            }

            public class GenericServicePerspective<TService> : IPerspectiveFor<Order, OrderUpdated>
                where TService : class {
              private readonly TService _service;

              public GenericServicePerspective(TService service) {
                _service = service;
              }

              public Order Apply(Order current, OrderUpdated @event) => current;
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz105 = diagnostics.Where(d => d.Id == "WHIZ105").ToArray();
    await Assert.That(whiz105.Length).IsEqualTo(1);
    await Assert.That(whiz105[0].GetMessage(CultureInfo.InvariantCulture)).Contains("TService")
      .Because("a generic type parameter is not a concrete type INamedTypeSymbol can inspect for [PureService], so it must be treated as unproven");
  }

  /// <summary>
  /// Verifies that a generic perspective whose <c>Apply</c> method returns its own unbound type
  /// parameter (rather than a concrete named type) is not misreported as async. Without this
  /// fallback, checking a type-parameter return type for "is this Task&lt;T&gt;?" would need a
  /// concrete named type to inspect and could throw instead of simply concluding "not a Task".
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task GenericModelApplyMethod_DoesNotReportWHIZ100Async() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class SomeEvent {
              public Guid Id { get; set; }
            }

            public class GenericModelPerspective<TModel> : IPerspectiveFor<TModel, SomeEvent>
                where TModel : class {
              public TModel Apply(TModel current, SomeEvent e) => current;
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ100")).IsFalse()
      .Because("TModel is the open declaration's own type parameter, not a Task<T> return type - there is nothing async here");
  }

  /// <summary>
  /// Verifies that an <c>Apply</c> method returning the bare, non-generic <c>Task</c> (rather than
  /// <c>Task&lt;TModel&gt;</c>) still reports WHIZ100, falling back to a generic "TModel"
  /// placeholder in the message since there is no type argument to name. Without this fallback,
  /// the diagnostic message for this exact shape would throw or render blank instead of guiding
  /// the fix.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task NonGenericTaskApplyMethod_ReportsWHIZ100WithGenericPlaceholderAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Threading.Tasks;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class Order {
              public Guid Id { get; set; }
            }

            public class OrderUpdated {
              public Guid OrderId { get; set; }
            }

            public class OrderPerspective : IPerspectiveFor<Order, OrderUpdated> {
              public Task Apply(Order current, OrderUpdated @event) {
                return Task.CompletedTask;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz100 = diagnostics.Where(d => d.Id == "WHIZ100").ToArray();
    await Assert.That(whiz100.Length).IsEqualTo(1);
    await Assert.That(whiz100[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Task<TModel>")
      .Because("a bare Task has no type argument to extract, so the message must fall back to a generic placeholder rather than fail");
  }
}
