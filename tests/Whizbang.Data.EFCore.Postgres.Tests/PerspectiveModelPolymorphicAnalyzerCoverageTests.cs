using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="PerspectiveModelPolymorphicAnalyzer"/> paths the existing
/// <c>PerspectiveModelPolymorphicAnalyzerTests</c> never exercise: an open generic type
/// parameter standing in for TModel, and the Newtonsoft.Json ignore-attribute variant of
/// the ignored-property check.
/// </summary>
[Category("Unit")]
[Category("Analyzers")]
[Category("Shard2")]
public class PerspectiveModelPolymorphicAnalyzerCoverageTests {
  #region Open Generic Type Parameter As TModel - Guarded, Not Crashed

  /// <summary>
  /// Verifies that a generic perspective class whose model is its own open type parameter
  /// (so the first type argument of <c>IPerspectiveFor&lt;TState, TestEvent&gt;</c> is an
  /// <c>ITypeParameterSymbol</c>, not an <c>INamedTypeSymbol</c>) is skipped rather than
  /// crashing the analyzer, and that a genuinely polymorphic model elsewhere in the same
  /// compilation is still analyzed normally. If this guard regressed to an unconditional
  /// cast, a consumer who writes a generic base perspective class would see the analyzer
  /// throw (reported as an AD0001 analyzer-crash diagnostic) instead of quietly moving on,
  /// which would also mask the real WHIZ811 finding below it in build output.
  /// </summary>
  [Test]
  public async Task GenericPerspective_WithOpenTypeParameterModel_SkippedWithoutCrashingAsync() {
    // Arrange
    const string source = """
            using System;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public abstract class PaymentMethod {
                    public string Name { get; set; } = string.Empty;
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public PaymentMethod? Payment { get; set; }
                }

                public record TestEvent(Guid Id);

                // TState is an open type parameter here - the first type argument of
                // IPerspectiveFor<TState, TestEvent> is an ITypeParameterSymbol, not an
                // INamedTypeSymbol, and must be skipped rather than crash the analyzer.
                public class GenericPerspective<TState> : Whizbang.Core.Perspectives.IPerspectiveFor<TState, TestEvent> {
                }

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert - exactly the one real finding, proving the generic class was skipped cleanly
    await Assert.That(diagnostics).Count().IsEqualTo(1)
        .Because("the generic perspective's unresolved model must be skipped, leaving only the real TestModel finding");
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ811");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Payment");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("TestModel");
  }

  #endregion

  #region Newtonsoft.Json Ignore Attribute Variant

  /// <summary>
  /// Verifies that a property carrying Newtonsoft.Json's <c>[JsonIgnore]</c> (as opposed to
  /// the System.Text.Json or EF Core variants already covered) is excluded from the
  /// polymorphic-property check. If this branch regressed, a consumer using Newtonsoft.Json
  /// to exclude a polymorphic property from serialization would get a spurious WHIZ811
  /// suggestion for a property that is never actually persisted.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithNewtonsoftJsonIgnoredAbstractProperty_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace Newtonsoft.Json {
                public sealed class JsonIgnoreAttribute : System.Attribute { }
            }

            namespace TestNamespace {
                public abstract class PaymentMethod {
                    public string Name { get; set; } = string.Empty;
                }

                public class TestModel {
                    public Guid Id { get; set; }

                    [Newtonsoft.Json.JsonIgnore]
                    public PaymentMethod? Payment { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty().Because("Newtonsoft.Json's [JsonIgnore] means the property is never serialized, so it is not a persistence hazard");
  }

  #endregion
}
